namespace GoMidi.Core;

/// <summary>A named, ordered collection of MIDI files.</summary>
public sealed class Playlist
{
    public Playlist(string name) => Name = name;

    public string Name { get; set; }
    public List<string> Files { get; } = new();

    public bool IsEmpty => Files.Count == 0;

    public bool AddFile(string path)
    {
        if (Files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Files.Add(path);
        return true;
    }

    public int AddFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (AddFile(path))
            {
                added++;
            }
        }

        return added;
    }
}

/// <summary>
/// Owns every playlist and the notion of a "current" one. Ordering inside a
/// playlist is user-controlled (drag to reorder). Ported from
/// <c>PlaylistManager.cpp</c>.
/// </summary>
public sealed class PlaylistManager
{
    private readonly List<Playlist> _playlists = new();
    private int _currentIndex;

    public const string DefaultName = "默认列表";

    public PlaylistManager()
    {
        EnsureDefaultPlaylist();
    }

    public IReadOnlyList<Playlist> Playlists => _playlists;
    public int Count => _playlists.Count;
    public int CurrentIndex => _currentIndex;

    public Playlist? Current =>
        _currentIndex >= 0 && _currentIndex < _playlists.Count ? _playlists[_currentIndex] : null;

    public Playlist? this[int index] =>
        index >= 0 && index < _playlists.Count ? _playlists[index] : null;

    public IReadOnlyList<string> CurrentFiles => Current?.Files ?? (IReadOnlyList<string>)Array.Empty<string>();

    public int CreatePlaylist(string? name = null)
    {
        string unique = GenerateUniqueName(string.IsNullOrWhiteSpace(name) ? "新建列表" : name.Trim());
        _playlists.Add(new Playlist(unique));
        return _playlists.Count - 1;
    }

    /// <summary>Removes a playlist. The final playlist is never removed; it is cleared instead.</summary>
    public bool DeletePlaylist(int index)
    {
        if (index < 0 || index >= _playlists.Count)
        {
            return false;
        }

        if (_playlists.Count == 1)
        {
            _playlists[0].Files.Clear();
            _playlists[0].Name = DefaultName;
            _currentIndex = 0;
            return true;
        }

        _playlists.RemoveAt(index);
        _currentIndex = Math.Clamp(_currentIndex >= index ? _currentIndex - 1 : _currentIndex, 0, _playlists.Count - 1);
        return true;
    }

    public bool RenamePlaylist(int index, string name)
    {
        if (index < 0 || index >= _playlists.Count || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (_playlists.Any(p => !ReferenceEquals(p, _playlists[index]) &&
                                string.Equals(p.Name, trimmed, StringComparison.Ordinal)))
        {
            return false;
        }

        _playlists[index].Name = trimmed;
        return true;
    }

    public bool SetCurrentPlaylist(int index)
    {
        if (index < 0 || index >= _playlists.Count)
        {
            return false;
        }

        _currentIndex = index;
        return true;
    }

    /// <summary>Moves a file inside the current playlist — used by drag reordering.</summary>
    public bool MoveFile(int from, int to)
    {
        Playlist? playlist = Current;
        if (playlist is null || from < 0 || from >= playlist.Files.Count || to < 0 || to >= playlist.Files.Count || from == to)
        {
            return false;
        }

        string file = playlist.Files[from];
        playlist.Files.RemoveAt(from);
        playlist.Files.Insert(to, file);
        return true;
    }

    public void Clear()
    {
        _playlists.Clear();
        EnsureDefaultPlaylist();
    }

    public void ReplaceAll(IEnumerable<Playlist> playlists, int currentIndex)
    {
        _playlists.Clear();
        _playlists.AddRange(playlists.Where(p => p is not null));
        EnsureDefaultPlaylist();
        _currentIndex = Math.Clamp(currentIndex, 0, _playlists.Count - 1);
    }

    private void EnsureDefaultPlaylist()
    {
        if (_playlists.Count == 0)
        {
            _playlists.Add(new Playlist(DefaultName));
            _currentIndex = 0;
        }
    }

    private string GenerateUniqueName(string baseName)
    {
        if (_playlists.All(p => !string.Equals(p.Name, baseName, StringComparison.Ordinal)))
        {
            return baseName;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} ({i})";
            if (_playlists.All(p => !string.Equals(p.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }
}
