using Orbivo.Core.Formats;

namespace Orbivo.Core.Tests.Formats;

public sealed class CubemapLayoutTests
{
    [Theory]
    [InlineData("3x2", 3, 2, "rludfb", "000000")]
    [InlineData("6x1", 6, 1, "rludfb", "000000")]
    [InlineData("1x6", 1, 6, "rludfb", "000000")]
    [InlineData("EAC", 3, 2, "lfrdbu", "000313")]
    public void Presets_MatchFfmpegV360(string name, int columns, int rows, string order, string rotation)
    {
        CubemapLayout? layout = CubemapLayout.FromPresetName(name);

        Assert.NotNull(layout);
        Assert.Equal((columns, rows, order, rotation), (layout.Columns, layout.Rows, layout.FaceOrder, layout.FaceRotation));
        Assert.Equal(name.ToLowerInvariant(), layout.PresetName);
    }

    [Fact]
    public void GetCell_FindsFaceAndRotation()
    {
        CubemapLayout eac = CubemapLayout.YouTubeEac;

        Assert.Equal((1, 0, 0), eac.GetCell(CubeFace.Front));
        Assert.Equal((0, 1, 3), eac.GetCell(CubeFace.Down));
        Assert.Equal((1, 1, 1), eac.GetCell(CubeFace.Back));
        Assert.Equal((2, 1, 3), eac.GetCell(CubeFace.Up));
    }

    [Fact]
    public void CustomLayout_HasNoPresetName_AndIsCaseInsensitive()
    {
        var layout = new CubemapLayout(2, 3, "FBLRUD", "000000", 4);

        Assert.Null(layout.PresetName);
        Assert.Equal("fblrud", layout.FaceOrder);
        Assert.Equal((1, 0, 0), layout.GetCell(CubeFace.Back));
        Assert.Equal((0, 2, 0), layout.GetCell(CubeFace.Up));
    }

    [Theory]
    [InlineData(4, 2, "rludfb", "000000", 0)] // 8 cells
    [InlineData(3, 2, "rludf", "000000", 0)] // 5 letters
    [InlineData(3, 2, "rludff", "000000", 0)] // f twice, b missing
    [InlineData(3, 2, "rludfx", "000000", 0)] // unknown letter
    [InlineData(3, 2, "rludfb", "000040", 0)] // rotation 4
    [InlineData(3, 2, "rludfb", "00000", 0)]
    [InlineData(3, 2, "rludfb", "000000", -1)]
    public void Invalid_IsRejected(int columns, int rows, string order, string rotation, int padding)
    {
        Assert.False(CubemapLayout.TryValidate(columns, rows, order, rotation, padding, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => new CubemapLayout(columns, rows, order, rotation, padding));
    }

    [Fact]
    public void Records_CompareByValue()
    {
        Assert.Equal(CubemapLayout.Grid3x2, new CubemapLayout(3, 2, "RLUDFB", "000000"));
        Assert.NotEqual(CubemapLayout.Grid3x2, new CubemapLayout(3, 2, "rludfb", "000000", 2));
    }
}
