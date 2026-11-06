using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Orbivo.Core.Playlists;

/// <summary>One video in the playlist. The same file may appear more than once; each entry is its own object.</summary>
public sealed class PlaylistEntry : INotifyPropertyChanged
{
    private bool _isCurrent;

    public PlaylistEntry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Full path of the video file.</summary>
    public string Path { get; }

    public string FileName { get; }

    /// <summary>True for the entry the player has loaded.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }

    public override string ToString() => FileName;
}

/// <summary>
/// The operator's playlist (F1–F3): order, the current entry and what comes next. No I/O except
/// <see cref="ExpandPaths"/>; the operator UI turns the decisions into player commands.
/// </summary>
/// <remarks>UI thread only. <see cref="Entries"/> raises collection change events for WPF binding.</remarks>
public sealed class Playlist
{
    /// <summary>File extensions offered in the file dialog and accepted by drag and drop (lower case, with dot).</summary>
    public static readonly IReadOnlyList<string> VideoExtensions = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".mts"];

    private readonly ObservableCollection<PlaylistEntry> _entries = [];
    private PlaylistEntry? _current;

    // After the current entry was removed: the index where it stood, so "Weiter" continues from there.
    private int? _removedCurrentIndex;

    public Playlist()
    {
        Entries = new ReadOnlyObservableCollection<PlaylistEntry>(_entries);
    }

    public ReadOnlyObservableCollection<PlaylistEntry> Entries { get; }

    /// <summary>The entry the player has loaded, or null (nothing loaded, or the loaded entry was removed).</summary>
    public PlaylistEntry? Current
    {
        get => _current;
        private set
        {
            if (_current is not null)
            {
                _current.IsCurrent = false;
            }

            _current = value;
            _removedCurrentIndex = null;
            if (_current is not null)
            {
                _current.IsCurrent = true;
            }
        }
    }

    /// <summary>Position of <see cref="Current"/>, or -1.</summary>
    public int CurrentIndex => _current is null ? -1 : _entries.IndexOf(_current);

    public bool HasNext => NextEntry() is not null;

    public bool HasPrevious => PreviousEntry() is not null;

    /// <summary>True if <paramref name="path"/> has a known video file extension.</summary>
    public static bool IsVideoFile(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns dropped or selected paths into video files: files with a video extension are kept, folders contribute
    /// the video files directly inside them (not in subfolders), sorted by name. Everything else is left out.
    /// </summary>
    public static IReadOnlyList<string> ExpandPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path)
                    .Where(IsVideoFile)
                    .Order(StringComparer.CurrentCultureIgnoreCase)
                    .Select(System.IO.Path.GetFullPath));
            }
            else if (IsVideoFile(path))
            {
                result.Add(System.IO.Path.GetFullPath(path));
            }
        }

        return result;
    }

    /// <summary>Appends the files at the end, or before <paramref name="insertAt"/>. Returns the new entries.</summary>
    public IReadOnlyList<PlaylistEntry> Add(IEnumerable<string> paths, int? insertAt = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int index = Math.Clamp(insertAt ?? _entries.Count, 0, _entries.Count);
        var added = new List<PlaylistEntry>();
        foreach (string path in paths)
        {
            var entry = new PlaylistEntry(path);
            _entries.Insert(index++, entry);
            added.Add(entry);
        }

        return added;
    }

    /// <summary>Removes the entry. If it is the current one, nothing is current afterwards; the player keeps its video.</summary>
    public void Remove(PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = _entries.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        if (entry == _current)
        {
            Current = null;
            _removedCurrentIndex = index;
        }
        else if (_removedCurrentIndex is int removed && index < removed)
        {
            _removedCurrentIndex = removed - 1;
        }

        _entries.RemoveAt(index);
    }

    /// <summary>Moves the entry one place up (-1) or down (+1). Returns false at the ends.</summary>
    public bool Move(PlaylistEntry entry, int direction)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = _entries.IndexOf(entry);
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= _entries.Count)
        {
            return false;
        }

        _entries.Move(index, target);
        return true;
    }

    /// <summary>Moves the entry to <paramref name="newIndex"/> (drag and drop inside the list).</summary>
    public void MoveTo(PlaylistEntry entry, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = _entries.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        int target = Math.Clamp(newIndex, 0, _entries.Count - 1);
        if (target != index)
        {
            _entries.Move(index, target);
        }
    }

    /// <summary>Marks <paramref name="entry"/> as the loaded one (the UI then opens it).</summary>
    public void SetCurrent(PlaylistEntry? entry)
    {
        if (entry is not null && !_entries.Contains(entry))
        {
            throw new ArgumentException("The entry is not in the playlist.", nameof(entry));
        }

        Current = entry;
    }

    /// <summary>Makes the next entry current and returns it, or null at the end (nothing changes then).</summary>
    public PlaylistEntry? MoveNext()
    {
        PlaylistEntry? next = NextEntry();
        if (next is not null)
        {
            Current = next;
        }

        return next;
    }

    /// <summary>Makes the previous entry current and returns it, or null at the start (nothing changes then).</summary>
    public PlaylistEntry? MovePrevious()
    {
        PlaylistEntry? previous = PreviousEntry();
        if (previous is not null)
        {
            Current = previous;
        }

        return previous;
    }

    /// <summary>
    /// The entry after the current one. Without a current entry it is the first entry, so "Weiter" starts the list;
    /// after the current entry was removed it is the entry that took its place.
    /// </summary>
    private PlaylistEntry? NextEntry()
    {
        int next = _current is not null ? CurrentIndex + 1 : _removedCurrentIndex ?? 0;
        return next >= 0 && next < _entries.Count ? _entries[next] : null;
    }

    private PlaylistEntry? PreviousEntry()
    {
        int previous = _current is not null ? CurrentIndex - 1 : (_removedCurrentIndex ?? 0) - 1;
        return previous >= 0 && previous < _entries.Count ? _entries[previous] : null;
    }
}
