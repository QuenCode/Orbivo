using Orbivo.Core.Playlists;

namespace Orbivo.Core.Tests.Playlists;

public sealed class PlaylistTests
{
    [Fact]
    public void Add_AppendsInOrder_AndInsertsAtAPosition()
    {
        var playlist = new Playlist();
        playlist.Add([@"C:\v\a.mp4", @"C:\v\b.mp4"]);
        playlist.Add([@"C:\v\c.mp4"], insertAt: 1);

        Assert.Equal(["a.mp4", "c.mp4", "b.mp4"], playlist.Entries.Select(entry => entry.FileName));
    }

    [Fact]
    public void Next_WithoutCurrent_StartsAtTheFirstEntry()
    {
        Playlist playlist = ThreeEntries();

        Assert.True(playlist.HasNext);
        Assert.False(playlist.HasPrevious);
        Assert.Equal("a.mp4", playlist.MoveNext()?.FileName);
        Assert.True(playlist.Entries[0].IsCurrent);
    }

    [Fact]
    public void NextAndPrevious_StopAtTheEnds()
    {
        Playlist playlist = ThreeEntries();
        playlist.SetCurrent(playlist.Entries[2]);

        Assert.False(playlist.HasNext);
        Assert.Null(playlist.MoveNext());
        Assert.Equal("c.mp4", playlist.Current?.FileName);

        Assert.Equal("b.mp4", playlist.MovePrevious()?.FileName);
        Assert.Equal("a.mp4", playlist.MovePrevious()?.FileName);
        Assert.Null(playlist.MovePrevious());
        Assert.Equal(0, playlist.CurrentIndex);
    }

    [Fact]
    public void OnlyOneEntryIsCurrent()
    {
        Playlist playlist = ThreeEntries();
        playlist.SetCurrent(playlist.Entries[0]);
        playlist.MoveNext();

        Assert.Equal([false, true, false], playlist.Entries.Select(entry => entry.IsCurrent));
    }

    [Fact]
    public void Move_ChangesTheOrder_AndKeepsTheCurrentEntry()
    {
        Playlist playlist = ThreeEntries();
        PlaylistEntry b = playlist.Entries[1];
        playlist.SetCurrent(b);

        Assert.True(playlist.Move(b, -1));
        Assert.False(playlist.Move(b, -1));
        Assert.Equal(["b.mp4", "a.mp4", "c.mp4"], playlist.Entries.Select(entry => entry.FileName));
        Assert.Same(b, playlist.Current);
        Assert.Equal("a.mp4", playlist.MoveNext()?.FileName);

        playlist.MoveTo(playlist.Entries[0], 5);
        Assert.Equal(["a.mp4", "c.mp4", "b.mp4"], playlist.Entries.Select(entry => entry.FileName));
    }

    [Fact]
    public void RemovingTheCurrentEntry_NextContinuesWhereItWas()
    {
        Playlist playlist = ThreeEntries();
        playlist.SetCurrent(playlist.Entries[1]);

        playlist.Remove(playlist.Entries[1]);

        Assert.Null(playlist.Current);
        Assert.DoesNotContain(playlist.Entries, entry => entry.IsCurrent);
        Assert.Equal("a.mp4", playlist.Entries[0].FileName);
        Assert.True(playlist.HasPrevious);
        Assert.Equal("c.mp4", playlist.MoveNext()?.FileName);
    }

    [Fact]
    public void RemovingTheLastCurrentEntry_LeavesNoNext()
    {
        Playlist playlist = ThreeEntries();
        playlist.SetCurrent(playlist.Entries[2]);
        playlist.Remove(playlist.Entries[2]);

        Assert.False(playlist.HasNext);
        Assert.Equal("b.mp4", playlist.MovePrevious()?.FileName);
    }

    [Fact]
    public void TheSameFileCanAppearTwice()
    {
        var playlist = new Playlist();
        playlist.Add([@"C:\v\a.mp4", @"C:\v\a.mp4"]);
        playlist.SetCurrent(playlist.Entries[1]);

        Assert.Equal(1, playlist.CurrentIndex);
        Assert.False(playlist.Entries[0].IsCurrent);
    }

    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("CLIP.MKV", true)]
    [InlineData("clip.mov", true)]
    [InlineData("clip.png", false)]
    [InlineData("clip.mp4.orbivo.json", false)]
    [InlineData("clip", false)]
    public void IsVideoFile(string path, bool expected)
    {
        Assert.Equal(expected, Playlist.IsVideoFile(path));
    }

    [Fact]
    public void ExpandPaths_KeepsVideos_AndTakesFoldersOneLevelDeep()
    {
        string root = Directory.CreateTempSubdirectory("orbivo-playlist-").FullName;
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
            string sub = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
            foreach (string file in new[] { Path.Combine(folder, "b.mp4"), Path.Combine(folder, "A.mkv"), Path.Combine(folder, "notes.txt"), Path.Combine(sub, "deep.mp4"), Path.Combine(root, "single.mov"), Path.Combine(root, "image.png") })
            {
                File.WriteAllBytes(file, []);
            }

            IReadOnlyList<string> expanded = Playlist.ExpandPaths(
                [Path.Combine(root, "single.mov"), folder, Path.Combine(root, "image.png")]);

            Assert.Equal(["single.mov", "A.mkv", "b.mp4"], expanded.Select(Path.GetFileName));
            Assert.All(expanded, path => Assert.True(Path.IsPathFullyQualified(path)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Playlist ThreeEntries()
    {
        var playlist = new Playlist();
        playlist.Add([@"C:\v\a.mp4", @"C:\v\b.mp4", @"C:\v\c.mp4"]);
        return playlist;
    }
}
