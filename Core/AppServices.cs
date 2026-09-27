using System.Text;

namespace GoMidi.Core;

/// <summary>
/// Very small thread-safe rolling log, written to
/// <c>%LOCALAPPDATA%\GoMidi\logs\gomidi.log</c>. Kept deliberately simple —
/// enough to diagnose a launch or playback problem without a debugger.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly string LogPath = Path.Combine(ConfigService.LogFolder, "gomidi.log");
    private const long MaxBytes = 512 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(ConfigService.LogFolder);
                Roll();
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Logging must never be the reason the app fails.
            }
        }
    }

    private static void Roll()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        string previous = LogPath + ".1";
        File.Delete(previous);
        File.Move(LogPath, previous);
    }
}

/// <summary>Window size in device-independent pixels.</summary>
public readonly record struct AppConfigSize(int Width, int Height);

/// <summary>
/// Process-wide service container. The app is a single cohesive instrument
/// rather than a set of independent features, so one shared container is
/// clearer here than a DI graph.
/// </summary>
public sealed class AppServices : IDisposable
{
    private static readonly Lazy<AppServices> Instance = new(() => new AppServices());

    private MainWindow? _window;
    private bool _disposed;

    private AppServices()
    {
        Settings = new ConfigService();
        Keys = new KeyManager();
        Engine = new PlaybackEngine(Keys);
        Playlists = new PlaylistManager();
        Ntp = new NtpClient();

        try
        {
            Hotkeys = new GlobalHotkeyService();
        }
        catch (Exception ex)
        {
            Log.Error("全局热键服务初始化失败", ex);
            Hotkeys = null;
        }

        Scheduler = new GoMidi.Services.ScheduledPlaybackService(this);

        RestoreFromConfig();
    }

    public static AppServices Current => Instance.Value;

    public ConfigService Settings { get; }

    public AppConfig Config => Settings.Config;

    public KeyManager Keys { get; }

    public PlaybackEngine Engine { get; }

    public PlaylistManager Playlists { get; }

    public NtpClient Ntp { get; }

    public GlobalHotkeyService? Hotkeys { get; }

    /// <summary>One-shot timer that starts playback at a chosen wall-clock time.</summary>
    public GoMidi.Services.ScheduledPlaybackService Scheduler { get; }

    /// <summary>
    /// Windows media session, so hardware media keys and the system media popup
    /// can drive playback. Null when the session could not be created.
    /// </summary>
    public MediaSession? Media { get; private set; }

    /// <summary>Live one-line description of what the app is doing, shown in the title bar.</summary>
    public event EventHandler<string>? StatusChanged;

    public string StatusText { get; private set; } = "就绪";

    /// <summary>Raised when the global hotkey asks for a play/pause toggle.</summary>
    public event EventHandler? TogglePlaybackRequested;

    public AppConfigSize WindowSize { get; set; }

    /// <summary>The song currently loaded in the player, shared with the channel and settings pages.</summary>
    public MidiFile? CurrentSong { get; private set; }

    /// <summary>Raised when a different song is loaded or the current one is unloaded.</summary>
    public event EventHandler? CurrentSongChanged;

    /// <summary>True when this process runs with an administrator token.</summary>
    public bool IsElevated => Elevation.IsCurrentProcessElevated;

    /// <summary>Compact privilege marker appended to the title-bar status line.</summary>
    public string PrivilegeSuffix => IsElevated ? " · 管理员" : " · 普通权限";

    /// <summary>
    /// Persists everything, then relaunches the app at the requested privilege
    /// level. The caller closes the window when this returns <c>true</c>.
    /// </summary>
    public bool TryRestart(bool elevated, out string error)
    {
        // Flush before handing over: the new instance reads the same config file.
        PersistChannels();
        PersistPlaylists();
        Settings.Flush();
        return Elevation.TryRestart(elevated, out error);
    }

    public void SetCurrentSong(MidiFile? song)
    {
        CurrentSong = song;
        CurrentSongChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Track labels for the channel route pickers: index 0 is "全部音轨".</summary>
    public IReadOnlyList<string> TrackChoices
    {
        get
        {
            var choices = new List<string> { "全部音轨" };
            if (CurrentSong is not null)
            {
                foreach (MidiTrackInfo track in CurrentSong.Tracks)
                {
                    choices.Add($"{track.Name}  ({track.NoteCount})");
                }
            }

            return choices;
        }
    }

    public void SetStatus(string text)
    {
        if (string.Equals(StatusText, text, StringComparison.Ordinal))
        {
            return;
        }

        StatusText = text;
        StatusChanged?.Invoke(this, text);
    }

    /// <summary>Wires the shell window once it is created, and registers the global hotkey.</summary>
    public void AttachWindow(MainWindow window)
    {
        _window = window;
        WindowSize = new AppConfigSize(Config.WindowWidth, Config.WindowHeight);
        ApplyHotkey();

        if (Hotkeys is not null)
        {
            Hotkeys.Pressed += (_, _) => TogglePlaybackRequested?.Invoke(this, EventArgs.Empty);
        }

        // The media session needs the window handle, which only exists now.
        try
        {
            Media = new MediaSession(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            Media.TryInitialize(WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
        catch (Exception ex)
        {
            Log.Error("媒体会话初始化失败", ex);
            Media = null;
        }
    }

    /// <summary>(Re)binds the global play/pause hotkey according to config.</summary>
    public void ApplyHotkey()
    {
        if (Hotkeys is null)
        {
            return;
        }

        if (!Config.HotkeyEnabled)
        {
            Hotkeys.Unregister();
            return;
        }

        // F12 — the same key the original registered.
        if (!Hotkeys.Register(0x7B))
        {
            Log.Warn("F12 全局热键注册失败（可能已被其他程序占用）");
        }
    }

    // -- config projection ---------------------------------------------------

    private void RestoreFromConfig()
    {
        AppConfig config = Config;
        config.Normalize();

        // Keymap scheme
        ApplyKeymapScheme(config.CurrentKeymap, persist: false);

        // Engine
        Engine.SetRange(config.MinPitch, config.MaxPitch);
        Engine.Speed = config.Speed;
        Engine.DecomposeChords = config.DecomposeChords;
        Engine.LatencyCompensationMs = config.LatencyCompensationMs;

        var channels = new List<ChannelState>(AppConfig.ChannelCount);
        for (int i = 0; i < AppConfig.ChannelCount; i++)
        {
            ChannelConfig stored = config.Channels[i];
            channels.Add(new ChannelState
            {
                Transpose = stored.Transpose,
                Enabled = stored.Enabled,
                WindowHandle = unchecked((nint)stored.WindowHandle),
                TrackIndex = stored.TrackIndex,
            });
        }

        Engine.SetChannels(channels);

        // Playlists
        if (config.Playlists.Count > 0)
        {
            var restored = config.Playlists.Select(p =>
            {
                var playlist = new Playlist(string.IsNullOrWhiteSpace(p.Name) ? PlaylistManager.DefaultName : p.Name);
                playlist.AddFiles(p.Files ?? new List<string>());
                return playlist;
            });

            Playlists.ReplaceAll(restored, config.CurrentPlaylist);
        }
    }

    /// <summary>Copies runtime channel state back into the persisted config.</summary>
    public void PersistChannels()
    {
        ChannelState[] snapshot = Engine.SnapshotChannels();
        for (int i = 0; i < snapshot.Length && i < Config.Channels.Count; i++)
        {
            Config.Channels[i].Transpose = snapshot[i].Transpose;
            Config.Channels[i].Enabled = snapshot[i].Enabled;
            Config.Channels[i].TrackIndex = snapshot[i].TrackIndex;
        }
    }

    public void PersistPlaylists()
    {
        Config.Playlists = Playlists.Playlists
            .Select(p => new PlaylistDto { Name = p.Name, Files = p.Files.ToList() })
            .ToList();
        Config.CurrentPlaylist = Playlists.CurrentIndex;
    }

    /// <summary>
    /// Switches the active keymap scheme and pushes its note range into the engine.
    /// </summary>
    public void ApplyKeymapScheme(string schemeId, bool persist = true)
    {
        switch (schemeId)
        {
            case KeyManager.SchemeFf14:
                Keys.ResetToDefault();
                break;

            case KeyManager.SchemeYys:
                Keys.LoadYysPreset();
                break;

            default:
            {
                KeymapSchemeDto? scheme = Config.KeymapSchemes
                    .FirstOrDefault(s => string.Equals(s.Name, schemeId, StringComparison.Ordinal));
                if (scheme is null)
                {
                    Keys.ResetToDefault();
                    schemeId = KeyManager.SchemeFf14;
                    break;
                }

                Keys.LoadScheme(scheme.Name, ParseBindings(scheme.Bindings), scheme.MinPitch, scheme.MaxPitch);
                break;
            }
        }

        Engine.SetRange(Keys.MinPitch, Keys.MaxPitch);
        Config.MinPitch = Keys.MinPitch;
        Config.MaxPitch = Keys.MaxPitch;
        Engine.NotifyKeymapChanged();

        if (persist)
        {
            Config.CurrentKeymap = schemeId;
            Settings.RequestSave();
        }
    }

    /// <summary>Stores the live keymap as a named scheme and selects it.</summary>
    public bool SaveCurrentKeymapAsScheme(string name, int minPitch, int maxPitch)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        if (name is KeyManager.SchemeYys or KeyManager.SchemeFf14)
        {
            return false;
        }

        var dto = new KeymapSchemeDto
        {
            Name = name,
            MinPitch = minPitch,
            MaxPitch = maxPitch,
            Bindings = SerializeBindings(Keys.Snapshot()),
        };

        int existing = Config.KeymapSchemes.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (existing >= 0)
        {
            Config.KeymapSchemes[existing] = dto;
        }
        else
        {
            Config.KeymapSchemes.Add(dto);
        }

        Keys.LoadScheme(name, ParseBindings(dto.Bindings), minPitch, maxPitch);
        Config.CurrentKeymap = name;
        Config.MinPitch = minPitch;
        Config.MaxPitch = maxPitch;
        Engine.SetRange(minPitch, maxPitch);
        Engine.NotifyKeymapChanged();
        Settings.RequestSave();
        return true;
    }

    /// <summary>Renames a stored scheme, re-selecting it afterwards.</summary>
    public bool RenameScheme(string id, string newName, int minPitch, int maxPitch)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName is KeyManager.SchemeYys or KeyManager.SchemeFf14)
        {
            return false;
        }

        int index = Config.KeymapSchemes.FindIndex(s => string.Equals(s.Name, id, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        for (int i = 0; i < Config.KeymapSchemes.Count; i++)
        {
            if (i != index && string.Equals(Config.KeymapSchemes[i].Name, newName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        Config.KeymapSchemes[index].Name = newName;
        Config.CurrentKeymap = newName;
        Settings.RequestSave();
        return true;
    }

    public bool DeleteScheme(string name)
    {
        int index = Config.KeymapSchemes.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        Config.KeymapSchemes.RemoveAt(index);
        if (string.Equals(Config.CurrentKeymap, name, StringComparison.Ordinal))
        {
            ApplyKeymapScheme(KeyManager.SchemeFf14);
        }

        Settings.RequestSave();
        return true;
    }

    private static Dictionary<string, string> SerializeBindings(IReadOnlyDictionary<int, KeyMapping> map)
    {
        var result = new Dictionary<string, string>(map.Count);
        foreach ((int pitch, KeyMapping mapping) in map)
        {
            result[pitch.ToString()] = $"{mapping.VkCode},{mapping.Modifier}";
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<int, KeyMapping>> ParseBindings(Dictionary<string, string> bindings)
    {
        foreach ((string key, string value) in bindings)
        {
            if (!int.TryParse(key, out int pitch) || pitch is < 0 or > 127)
            {
                continue;
            }

            string[] parts = value.Split(',');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int vk) ||
                !int.TryParse(parts[1], out int modifier) ||
                vk == 0)
            {
                continue;
            }

            yield return new KeyValuePair<int, KeyMapping>(pitch, new KeyMapping(vk, modifier));
        }
    }

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window is not null)
        {
            WindowSize = _window.GetCurrentSizeDip();
            Config.WindowWidth = WindowSize.Width;
            Config.WindowHeight = WindowSize.Height;
        }

        PersistChannels();
        PersistPlaylists();
        Engine.Dispose();
        Hotkeys?.Dispose();
        Media?.Dispose();
        Ntp.Dispose();
        Settings.Dispose();
    }

    public void Dispose() => Shutdown();
}
