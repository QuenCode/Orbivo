using Orbivo.Core.Formats;

namespace Orbivo.App;

/// <summary>
/// Command line options.
/// </summary>
/// <param name="NoXr">
/// <c>--no-xr</c>: render into a desktop window instead of the headset (debug viewport, from M1).
/// </param>
/// <param name="ImagePath">
/// <c>--image &lt;path&gt;</c>: equirect 360 still image for the headset or the debug viewport. Relative to the current directory.
/// </param>
/// <param name="VideoPath">
/// <c>--video &lt;path&gt;</c>: equirect 360 video to load at start (M3; the playlist follows in M4). Plays when the operator presses Play.
/// </param>
/// <param name="StereoLayout">
/// <c>--stereo mono|tb|sbs</c>: stereo layout of image and video. Without it, square images are top-bottom, all others mono.
/// </param>
/// <param name="UnknownArguments">Arguments that were not recognized; they are logged as a warning.</param>
public sealed record StartupOptions(
    bool NoXr,
    string? ImagePath,
    string? VideoPath,
    StereoLayout? StereoLayout,
    IReadOnlyList<string> UnknownArguments)
{
    public const string NoXrSwitch = "--no-xr";
    public const string ImageOption = "--image";
    public const string VideoOption = "--video";
    public const string StereoOption = "--stereo";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool noXr = false;
        string? imagePath = null;
        string? videoPath = null;
        StereoLayout? stereoLayout = null;
        var unknown = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (Is(arg, NoXrSwitch))
            {
                noXr = true;
            }
            else if (Is(arg, ImageOption))
            {
                imagePath = TakeValue(args, ref i, ImageOption, unknown);
            }
            else if (Is(arg, VideoOption))
            {
                videoPath = TakeValue(args, ref i, VideoOption, unknown);
            }
            else if (Is(arg, StereoOption))
            {
                string? value = TakeValue(args, ref i, StereoOption, unknown);
                if (value is not null)
                {
                    stereoLayout = ParseStereoLayout(value);
                    if (stereoLayout is null)
                    {
                        unknown.Add($"{StereoOption} {value} (expected mono, tb or sbs)");
                    }
                }
            }
            else
            {
                unknown.Add(arg);
            }
        }

        return new StartupOptions(noXr, imagePath, videoPath, stereoLayout, unknown);
    }

    private static StereoLayout? ParseStereoLayout(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "mono" => Core.Formats.StereoLayout.Mono,
            "tb" or "topbottom" or "top-bottom" => Core.Formats.StereoLayout.TopBottom,
            "sbs" or "sidebyside" or "side-by-side" => Core.Formats.StereoLayout.SideBySide,
            _ => null,
        };
    }

    private static string? TakeValue(IReadOnlyList<string> args, ref int index, string option, List<string> unknown)
    {
        if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return args[++index];
        }

        unknown.Add($"{option} (value missing)");
        return null;
    }

    private static bool Is(string arg, string option) => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase);
}
