using Orbivo.Core.Geometry;
using Orbivo.Core.Localization;

namespace Orbivo.Core.Formats;

/// <summary>Where a part of the format comes from (shown to the operator).</summary>
public enum FormatSource
{
    /// <summary>Nothing said anything; the standard value.</summary>
    Default,

    /// <summary>Guessed from the picture size.</summary>
    Guess,

    /// <summary>Spherical Video metadata in the file (sv3d/st3d).</summary>
    Metadata,

    /// <summary>Sidecar file or the operator window (F13).</summary>
    Sidecar,

    /// <summary><c>--stereo</c> on the command line (this session only, not saved).</summary>
    CommandLine,
}

/// <summary>Source of each part of a <see cref="VideoFormat"/>.</summary>
public sealed record FormatSources(
    FormatSource Projection,
    FormatSource Layout,
    FormatSource SwapEyes,
    FormatSource Cubemap,
    FormatSource Fisheye,
    FormatSource Offset);

/// <summary>Result of <see cref="VideoFormatResolver.Resolve"/>.</summary>
/// <param name="Format">The format to show the video with.</param>
/// <param name="Automatic">
/// Per part, what it would be if the operator left just that part automatic (metadata, guess, command line). Layout
/// and cube layout follow the projection in use, so "Automatisch" in the operator window names what the choice gives.
/// </param>
/// <param name="Sources">Where each part of <paramref name="Format"/> comes from.</param>
/// <param name="AutomaticSources">Where each part of <paramref name="Automatic"/> comes from (never <see cref="FormatSource.Sidecar"/>).</param>
/// <param name="Warnings">Hints for the operator (German): unsupported or incomplete metadata, implausible sizes.</param>
public sealed record ResolvedVideoFormat(
    VideoFormat Format,
    VideoFormat Automatic,
    FormatSources Sources,
    FormatSources AutomaticSources,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Decides the format of a video (F12, F13): per part, command line (<c>--stereo</c>) over sidecar over metadata over
/// a guess from the picture size. Pure function, unit-tested; the player calls it on its control thread.
/// </summary>
public static class VideoFormatResolver
{
    private const double AspectTolerance = 0.02;

    /// <param name="metadata">What the file says (sv3d/st3d).</param>
    /// <param name="width">Decoded width in pixels.</param>
    /// <param name="height">Decoded height in pixels.</param>
    /// <param name="overrides">Sidecar content (operator choices).</param>
    /// <param name="commandLineLayout"><c>--stereo</c>, or null.</param>
    public static ResolvedVideoFormat Resolve(
        EmbeddedFormatMetadata metadata,
        int width,
        int height,
        FormatOverrides overrides,
        StereoLayout? commandLineLayout = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(overrides);
        var warnings = new List<string>();

        // Projection: metadata, else equirect 360.
        (ProjectionType autoProjection, FormatSource autoProjectionSource) = ProjectionFromMetadata(metadata.Spherical, warnings);
        ProjectionType projection = overrides.Projection ?? autoProjection;
        FormatSource projectionSource = overrides.Projection is null ? autoProjectionSource : FormatSource.Sidecar;

        // Cube layout: 3 x 2 (with the padding from the metadata), for EAC the YouTube layout.
        int padding = metadata.Spherical?.Kind == SphericalProjectionKind.Cubemap ? metadata.Spherical.Padding : 0;
        CubemapLayout autoCubemap = AutomaticCubemap(projection, padding);
        FormatSource autoCubemapSource = padding > 0 ? FormatSource.Metadata : FormatSource.Default;

        // Stereo layout: metadata, else a guess from the size that fits the projection.
        CubemapLayout cubemap = overrides.Cubemap ?? autoCubemap;
        (StereoLayout autoLayout, FormatSource autoLayoutSource, bool autoSwap) =
            LayoutFromMetadata(metadata, projection, cubemap, width, height, warnings);
        if (commandLineLayout is { } forced)
        {
            autoLayout = forced;
            autoLayoutSource = FormatSource.CommandLine;
        }

        StereoLayout layout = commandLineLayout ?? overrides.Layout ?? autoLayout;
        FormatSource layoutSource = commandLineLayout is not null
            ? FormatSource.CommandLine
            : overrides.Layout is null ? autoLayoutSource : FormatSource.Sidecar;

        if (metadata.Spherical is { } spherical && (spherical.YawDegrees != 0 || spherical.PitchDegrees != 0 || spherical.RollDegrees != 0))
        {
            warnings.Add(Loc.Format(CoreText.MetadataOrientationIgnored, spherical.YawDegrees, spherical.PitchDegrees, spherical.RollDegrees));
        }

        var automatic = new VideoFormat(autoProjection, autoLayout, autoSwap, autoCubemap, FisheyeParameters.Default, OrientationOffset.Zero);
        var format = new VideoFormat(
            projection,
            layout,
            overrides.SwapEyes ?? autoSwap,
            cubemap,
            overrides.Fisheye ?? FisheyeParameters.Default,
            overrides.Offset ?? OrientationOffset.Zero);
        var automaticSources = new FormatSources(
            autoProjectionSource,
            autoLayoutSource,
            autoSwap ? FormatSource.Metadata : FormatSource.Default,
            autoCubemapSource,
            FormatSource.Default,
            FormatSource.Default);
        var sources = new FormatSources(
            projectionSource,
            layoutSource,
            overrides.SwapEyes is not null ? FormatSource.Sidecar : automaticSources.SwapEyes,
            overrides.Cubemap is not null ? FormatSource.Sidecar : automaticSources.Cubemap,
            overrides.Fisheye is not null ? FormatSource.Sidecar : FormatSource.Default,
            overrides.Offset is { IsZero: false } ? FormatSource.Sidecar : FormatSource.Default);

        CheckAspect(format, width, height, warnings);
        return new ResolvedVideoFormat(format, automatic, sources, automaticSources, warnings);
    }

    /// <summary>Width / height of one eye that a projection normally has (squeezed side-by-side aside).</summary>
    public static double NaturalEyeAspect(ProjectionType projection, CubemapLayout cubemap)
    {
        return projection switch
        {
            ProjectionType.Equirect360 => 2.0,
            ProjectionType.Cubemap or ProjectionType.Eac => (double)cubemap.Columns / cubemap.Rows,
            _ => 1.0,
        };
    }

    private static CubemapLayout AutomaticCubemap(ProjectionType projection, int padding)
    {
        CubemapLayout preset = projection == ProjectionType.Eac ? CubemapLayout.YouTubeEac : CubemapLayout.Grid3x2;
        return padding > 0 ? new CubemapLayout(preset.Columns, preset.Rows, preset.FaceOrder, preset.FaceRotation, padding) : preset;
    }

    private static (ProjectionType Projection, FormatSource Source) ProjectionFromMetadata(SphericalMapping? spherical, List<string> warnings)
    {
        if (spherical is null)
        {
            return (ProjectionType.Equirect360, FormatSource.Default);
        }

        switch (spherical.Kind)
        {
            case SphericalProjectionKind.Equirectangular:
                return (ProjectionType.Equirect360, FormatSource.Metadata);
            case SphericalProjectionKind.HalfEquirectangular:
                return (ProjectionType.Equirect180, FormatSource.Metadata);
            case SphericalProjectionKind.EquirectangularTile:
                if (IsHalf(spherical))
                {
                    return (ProjectionType.Equirect180, FormatSource.Metadata);
                }

                warnings.Add(Loc.Format(CoreText.MetadataEquirectTile, spherical.BoundLeft, spherical.BoundTop, spherical.BoundRight, spherical.BoundBottom));
                return (ProjectionType.Equirect360, FormatSource.Metadata);
            case SphericalProjectionKind.Cubemap:
                return (ProjectionType.Cubemap, FormatSource.Metadata);
            case SphericalProjectionKind.Fisheye:
                return (ProjectionType.Fisheye, FormatSource.Metadata);
            default:
                warnings.Add(Loc.Format(CoreText.MetadataProjectionUnsupported, Describe(spherical.Kind)));
                return (ProjectionType.Equirect360, FormatSource.Default);
        }
    }

    private static bool IsHalf(SphericalMapping spherical)
    {
        return Math.Abs(spherical.BoundLeft - 0.25) < 0.005 && Math.Abs(spherical.BoundRight - 0.25) < 0.005
            && spherical.BoundTop < 0.005 && spherical.BoundBottom < 0.005;
    }

    private static (StereoLayout Layout, FormatSource Source, bool Swap) LayoutFromMetadata(
        EmbeddedFormatMetadata metadata,
        ProjectionType projection,
        CubemapLayout cubemap,
        int width,
        int height,
        List<string> warnings)
    {
        if (metadata.Stereo is { } stereo)
        {
            StereoLayout? layout = stereo.Kind switch
            {
                Stereo3DKind.TwoD => StereoLayout.Mono,
                Stereo3DKind.TopBottom => StereoLayout.TopBottom,
                Stereo3DKind.SideBySide => StereoLayout.SideBySide,
                _ => null,
            };
            if (layout is { } known)
            {
                return (known, FormatSource.Metadata, stereo.Inverted && known != StereoLayout.Mono);
            }

            warnings.Add(Loc.Format(CoreText.MetadataStereoUnsupported, stereo.Kind));
        }

        StereoLayout guess = GuessLayout(projection, cubemap, width, height);
        if (metadata.Spherical is not null && metadata.Stereo is null && guess != StereoLayout.Mono)
        {
            warnings.Add(Loc.Format(CoreText.MetadataStereoMissing, Describe(guess)));
        }

        return (guess, FormatSource.Guess, false);
    }

    /// <summary>
    /// The layout whose eye has the projection's natural aspect: mono first, then top-bottom, then side-by-side;
    /// mono if none fits. Side-by-side with squeezed eyes cannot be told from mono and needs metadata or the operator.
    /// </summary>
    public static StereoLayout GuessLayout(ProjectionType projection, CubemapLayout cubemap, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return StereoLayout.Mono;
        }

        double natural = NaturalEyeAspect(projection, cubemap);
        foreach (StereoLayout layout in (ReadOnlySpan<StereoLayout>)[StereoLayout.Mono, StereoLayout.TopBottom, StereoLayout.SideBySide])
        {
            (int eyeWidth, int eyeHeight) = ProjectionMath.EyeSize(layout, width, height);
            if (Math.Abs(((double)eyeWidth / eyeHeight / natural) - 1) <= AspectTolerance)
            {
                return layout;
            }
        }

        return StereoLayout.Mono;
    }

    private static void CheckAspect(VideoFormat format, int width, int height, List<string> warnings)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        (int eyeWidth, int eyeHeight) = ProjectionMath.EyeSize(format.Layout, width, height);
        double aspect = (double)eyeWidth / eyeHeight;
        double natural = NaturalEyeAspect(format.Projection, format.Cubemap);
        bool squeezedSideBySide = format.Projection == ProjectionType.Equirect360 && format.Layout == StereoLayout.SideBySide
            && Math.Abs(aspect - 1) <= AspectTolerance;
        if (Math.Abs((aspect / natural) - 1) > AspectTolerance && !squeezedSideBySide)
        {
            warnings.Add(Loc.Format(CoreText.FormatAspectMismatch, eyeWidth, eyeHeight, Describe(format.Projection), Describe(format.Layout), natural));
        }
    }

    /// <summary>German name of a projection for the operator.</summary>
    public static string Describe(ProjectionType projection) => projection switch
    {
        ProjectionType.Equirect360 => "Equirect 360°",
        ProjectionType.Equirect180 => "Equirect 180° (VR180)",
        ProjectionType.Cubemap => "Cubemap",
        ProjectionType.Eac => "EAC",
        ProjectionType.Fisheye => "Fisheye",
        _ => projection.ToString(),
    };

    /// <summary>German name of a stereo layout for the operator.</summary>
    public static string Describe(StereoLayout layout) => layout switch
    {
        StereoLayout.TopBottom => "Top-Bottom",
        StereoLayout.SideBySide => "Side-by-Side",
        _ => "Mono",
    };

    private static string Describe(SphericalProjectionKind kind) => Enum.IsDefined(kind) ? kind.ToString() : Loc.Format(CoreText.MetadataProjectionNumber, (int)kind);
}
