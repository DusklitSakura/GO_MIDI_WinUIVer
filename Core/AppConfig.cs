using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoMidi.Core;

/// <summary>Persisted routing/transpose settings for one of the eight channels.</summary>
public sealed class ChannelConfig
{
    /// <summary>Window title captured when the channel was configured, used to re-bind after a restart.</summary>
    public string WindowTitle { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Live HWND, valid only for the current session.</summary>
    public long WindowHandle { get; set; }

    /// <summary>-1 selects every track.</summary>
    public int TrackIndex { get; set; } = -1;

    public int Transpose { get; set; }

    public bool Enabled { get; set; } = true;

    public ChannelConfig Clone() => new()
    {
        WindowTitle = WindowTitle,
        ProcessName = ProcessName,
        WindowHandle = WindowHandle,
        TrackIndex = TrackIndex,
        Transpose = Transpose,
        Enabled = Enabled,
    };
}

/// <summary>Serializable form of one saved keymap scheme.</summary>
public sealed class KeymapSchemeDto
{
    public string Name { get; set; } = string.Empty;
    public int MinPitch { get; set; } = 48;
    public int MaxPitch { get; set; } = 84;

    /// <summary>Pitch (as text) → "vk,modifier".</summary>
    public Dictionary<string, string> Bindings { get; set; } = new();
}

public sealed class PlaylistDto
{
    public string Name { get; set; } = PlaylistManager.DefaultName;
    public List<string> Files { get; set; } = new();
}

/// <summary>The five playlist modes the original cycled through with 模式.</summary>
public enum PlayMode
{
    Single,
    SingleLoop,
    Sequential,
    SequentialLoop,
    Shuffle,
}

/// <summary>Display names and cycle order for <see cref="PlayMode"/>.</summary>
public static class PlayModes
{
    public static readonly PlayMode[] Cycle =
    [
        PlayMode.Single,
        PlayMode.SingleLoop,
        PlayMode.Sequential,
        PlayMode.SequentialLoop,
        PlayMode.Shuffle,
    ];

    public static string Name(PlayMode mode) => mode switch
    {
        PlayMode.Single => "单曲播放",
        PlayMode.SingleLoop => "单曲循环",
        PlayMode.Sequential => "列表播放",
        PlayMode.SequentialLoop => "列表循环",
        _ => "随机播放",
    };

    public static PlayMode Next(PlayMode mode)
    {
        int index = Array.IndexOf(Cycle, mode);
        return Cycle[(index + 1) % Cycle.Length];
    }
}

/// <summary>Everything the app persists between runs.</summary>
public sealed class AppConfig
{
    public const int ChannelCount = 8;

    public List<ChannelConfig> Channels { get; set; } = CreateDefaultChannels();

    public int MinPitch { get; set; } = 48;
    public int MaxPitch { get; set; } = 84;

    public double Speed { get; set; } = 1.0;
    public bool DecomposeChords { get; set; }
    public int LatencyCompensationMs { get; set; }

    /// <summary>Empty means the built-in default (FF14) scheme.</summary>
    public string CurrentKeymap { get; set; } = string.Empty;

    public List<KeymapSchemeDto> KeymapSchemes { get; set; } = new();

    public List<PlaylistDto> Playlists { get; set; } = new();
    public int CurrentPlaylist { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlayMode PlayMode { get; set; } = PlayMode.Single;

    public string LastSelectedFile { get; set; } = string.Empty;

    public bool HotkeyEnabled { get; set; } = true;

    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    public static List<ChannelConfig> CreateDefaultChannels()
    {
        var list = new List<ChannelConfig>(ChannelCount);
        for (int i = 0; i < ChannelCount; i++)
        {
            list.Add(new ChannelConfig { Enabled = i == 0 });
        }

        return list;
    }

    /// <summary>Guarantees the channel list always has exactly eight well-formed entries.</summary>
    public void Normalize()
    {
        Channels ??= CreateDefaultChannels();

        // A hand-edited config.json can contain nulls where objects are expected.
        for (int i = 0; i < Channels.Count; i++)
        {
            Channels[i] ??= new ChannelConfig { Enabled = false };
        }

        while (Channels.Count < ChannelCount)
        {
            Channels.Add(new ChannelConfig { Enabled = false });
        }

        if (Channels.Count > ChannelCount)
        {
            Channels.RemoveRange(ChannelCount, Channels.Count - ChannelCount);
        }

        KeymapSchemes ??= new List<KeymapSchemeDto>();
        KeymapSchemes.RemoveAll(s => s is null);
        foreach (KeymapSchemeDto scheme in KeymapSchemes)
        {
            scheme.Bindings ??= new Dictionary<string, string>();
        }

        Playlists ??= new List<PlaylistDto>();
        Playlists.RemoveAll(p => p is null);
        foreach (PlaylistDto playlist in Playlists)
        {
            playlist.Files ??= new List<string>();
            if (string.IsNullOrWhiteSpace(playlist.Name))
            {
                playlist.Name = PlaylistManager.DefaultName;
            }
        }

        MinPitch = Math.Clamp(MinPitch, 0, 127);
        MaxPitch = Math.Clamp(MaxPitch, 0, 127);
        if (MinPitch >= MaxPitch)
        {
            MinPitch = 48;
            MaxPitch = 84;
        }

        Speed = Math.Clamp(Speed, 0.1, 100.0);
        LatencyCompensationMs = Math.Clamp(LatencyCompensationMs, 0, 500);
    }
}

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON under
/// <c>%LOCALAPPDATA%\GoMidi</c>. Writes are debounced so rapid slider changes
/// do not hammer the disk.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly System.Timers.Timer _debounce;
    private AppConfig _pending = null!;
    private bool _hasPending;

    public ConfigService()
    {
        Directory.CreateDirectory(DataFolder);
        _debounce = new System.Timers.Timer(600) { AutoReset = false };
        _debounce.Elapsed += (_, _) =>
        {
            lock (_gate)
            {
                if (_hasPending)
                {
                    _hasPending = false;
                    WriteAtomic(_pending);
                }
            }
        };

        Config = Load();
    }

    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoMidi");

    public static string ConfigPath { get; } = Path.Combine(DataFolder, "config.json");

    public static string LogFolder { get; } = Path.Combine(DataFolder, "logs");

    public AppConfig Config { get; }

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt config must never block startup — fall through to defaults.
        }

        return new AppConfig();
    }

    /// <summary>Queues a debounced save of the live config object.</summary>
    public void RequestSave()
    {
        lock (_gate)
        {
            _pending = Config;
            _hasPending = true;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>Writes immediately, e.g. on shutdown.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _debounce.Stop();
            _hasPending = false;
            WriteAtomic(Config);
        }
    }

    private static void WriteAtomic(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; losing a save must not crash playback.
        }
    }

    public void Dispose()
    {
        Flush();
        _debounce.Dispose();
    }
}
