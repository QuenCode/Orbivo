using System.Diagnostics.CodeAnalysis;

namespace Orbivo.Core.Formats;

/// <summary>
/// Where the six cube faces lie in the picture of one eye and how each one is turned (F10, Cubemap and EAC).
/// Same notation as ffmpeg's v360 filter (<c>in_forder</c>, <c>in_frot</c>), so layouts made with it can be named directly.
/// </summary>
/// <remarks>
/// <para>
/// The grid has <see cref="Columns"/> × <see cref="Rows"/> equal cells, counted row by row from the top left.
/// <see cref="FaceOrder"/> names the face in each cell (r, l, u, d, f, b = right, left, up, down, front, back),
/// <see cref="FaceRotation"/> how far the face in that cell is turned clockwise (0–3 quarter turns).
/// </para>
/// <para>
/// Unturned faces look as seen from the middle of the cube: front, back, left and right upright; on the up face
/// the top edge points backwards, on the down face it points forwards (checked against v360, docs/DECISIONS.md).
/// </para>
/// </remarks>
public sealed record CubemapLayout
{
    private const string FaceLetters = "rludfb";

    public CubemapLayout(int columns, int rows, string faceOrder, string faceRotation, int paddingPixels = 0)
    {
        if (!TryValidate(columns, rows, faceOrder, faceRotation, paddingPixels, out string? error))
        {
            throw new ArgumentException(error);
        }

        Columns = columns;
        Rows = rows;
        FaceOrder = faceOrder.ToLowerInvariant();
        FaceRotation = faceRotation;
        PaddingPixels = paddingPixels;
    }

    /// <summary>3 × 2: right, left, up / down, front, back; no rotation (v360 <c>c3x2</c>, Facebook).</summary>
    public static CubemapLayout Grid3x2 { get; } = new(3, 2, "rludfb", "000000");

    /// <summary>6 × 1 in one row: right, left, up, down, front, back (v360 <c>c6x1</c>).</summary>
    public static CubemapLayout Strip6x1 { get; } = new(6, 1, "rludfb", "000000");

    /// <summary>1 × 6 in one column: right, left, up, down, front, back (v360 <c>c1x6</c>).</summary>
    public static CubemapLayout Strip1x6 { get; } = new(1, 6, "rludfb", "000000");

    /// <summary>
    /// The EAC layout of YouTube (Google Spherical Video V2, v360 <c>eac</c>): left, front, right / down, back, up,
    /// the lower row turned by 270°, 90° and 270° clockwise.
    /// </summary>
    public static CubemapLayout YouTubeEac { get; } = new(3, 2, "lfrdbu", "000313");

    /// <summary>The preset layouts offered in the operator window, by name.</summary>
    public static IReadOnlyList<(string Name, CubemapLayout Layout)> Presets { get; } =
    [
        ("3x2", Grid3x2),
        ("6x1", Strip6x1),
        ("1x6", Strip1x6),
        ("eac", YouTubeEac),
    ];

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Six letters, one per cell row by row: r, l, u, d, f, b, each exactly once.</summary>
    public string FaceOrder { get; }

    /// <summary>Six digits 0–3, one per cell row by row: clockwise quarter turns of the face in that cell.</summary>
    public string FaceRotation { get; }

    /// <summary>
    /// Unused border around each face in pixels of the eye's picture (Spherical Video V2 <c>cbmp</c> padding).
    /// </summary>
    public int PaddingPixels { get; }

    /// <summary>Name of the preset this layout equals (padding aside), or null.</summary>
    public string? PresetName
    {
        get
        {
            foreach ((string name, CubemapLayout layout) in Presets)
            {
                if (layout.Columns == Columns && layout.Rows == Rows && layout.FaceOrder == FaceOrder && layout.FaceRotation == FaceRotation)
                {
                    return name;
                }
            }

            return null;
        }
    }

    /// <summary>A preset by name ("3x2", "6x1", "1x6", "eac"; case-insensitive), or null.</summary>
    public static CubemapLayout? FromPresetName(string? name)
    {
        foreach ((string presetName, CubemapLayout layout) in Presets)
        {
            if (string.Equals(presetName, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>Checks the values without throwing; <paramref name="error"/> explains the first problem (English, for the log).</summary>
    public static bool TryValidate(int columns, int rows, string? faceOrder, string? faceRotation, int paddingPixels, [NotNullWhen(false)] out string? error)
    {
        if (columns < 1 || rows < 1 || columns * rows != 6)
        {
            error = $"A cube layout needs 6 cells, not {columns} x {rows}.";
            return false;
        }

        if (faceOrder is null || faceOrder.Length != 6)
        {
            error = $"The face order must have 6 letters (r, l, u, d, f, b), not '{faceOrder}'.";
            return false;
        }

        string lower = faceOrder.ToLowerInvariant();
        foreach (char letter in FaceLetters)
        {
            if (lower.Count(c => c == letter) != 1)
            {
                error = $"The face order '{faceOrder}' must name each of r, l, u, d, f, b exactly once.";
                return false;
            }
        }

        if (faceRotation is null || faceRotation.Length != 6 || faceRotation.Any(c => c is < '0' or > '3'))
        {
            error = $"The face rotation must have 6 digits 0-3, not '{faceRotation}'.";
            return false;
        }

        if (paddingPixels is < 0 or > 1024)
        {
            error = $"The padding must be 0-1024 pixels, not {paddingPixels}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Grid cell (column, row from the top left) and clockwise quarter turns of <paramref name="face"/>.</summary>
    public (int Column, int Row, int Rotation) GetCell(CubeFace face)
    {
        int cell = FaceOrder.IndexOf(FaceLetters[(int)face], StringComparison.Ordinal);
        return (cell % Columns, cell / Columns, FaceRotation[cell] - '0');
    }

    public override string ToString() => $"{Columns}x{Rows} {FaceOrder} {FaceRotation}" + (PaddingPixels > 0 ? $" padding {PaddingPixels}" : string.Empty);
}
