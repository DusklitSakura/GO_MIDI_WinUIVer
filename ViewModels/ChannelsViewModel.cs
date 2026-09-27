using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoMidi.Core;
using System.Collections.ObjectModel;

namespace GoMidi.ViewModels;

/// <summary>Backing model for one of the eight channel routing cards.</summary>
public partial class ChannelCardViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private bool _loading;

    public ChannelCardViewModel(int index)
    {
        Index = index;
        Title = $"通道 {index + 1}";
    }

    public int Index { get; }

    public string Title { get; }

    public ObservableCollection<string> WindowChoices { get; } = new();

    public ObservableCollection<string> TrackChoices { get; } = new();

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial double Transpose { get; set; }

    [ObservableProperty]
    public partial int SelectedWindowIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedTrackIndex { get; set; }

    [ObservableProperty]
    public partial string AutoShiftText { get; set; } = "智能移调 0";

    [ObservableProperty]
    public partial string RouteSummary { get; set; } = "全部音轨 → 前台窗口";

    /// <summary>Windows currently offered; index 0 is "未选择".</summary>
    public List<WindowInfo> Windows { get; } = new();

    public void Load(IReadOnlyList<WindowInfo> windows, IReadOnlyList<string> trackChoices, ChannelState state)
    {
        _loading = true;

        Windows.Clear();
        WindowChoices.Clear();
        WindowChoices.Add("未选择（前台窗口）");
        Windows.Add(new WindowInfo(nint.Zero, "未选择（前台窗口）", string.Empty, 0));
        foreach (WindowInfo window in windows)
        {
            Windows.Add(window);
            WindowChoices.Add(window.Display);
        }

        TrackChoices.Clear();
        foreach (string choice in trackChoices)
        {
            TrackChoices.Add(choice);
        }

        Enabled = state.Enabled;
        Transpose = state.Transpose;

        // Re-bind the saved window by handle first, then by title as a fallback.
        int windowIndex = Windows.FindIndex(w => w.Hwnd == state.WindowHandle && state.WindowHandle != nint.Zero);
        if (windowIndex < 0)
        {
            windowIndex = 0;
        }

        SelectedWindowIndex = windowIndex;

        int trackIndex = state.TrackIndex < 0 ? 0 : state.TrackIndex + 1;
        SelectedTrackIndex = trackIndex >= 0 && trackIndex < TrackChoices.Count ? trackIndex : 0;

        _loading = false;
        UpdateSummary();
    }

    /// <summary>Re-binds a saved window by title after a restart, when handles are stale.</summary>
    public bool TryRestoreWindow(string title, string processName, IReadOnlyList<WindowInfo> windows)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        int index = -1;
        for (int i = 0; i < windows.Count; i++)
        {
            if (string.Equals(windows[i].Title, title, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(processName) ||
                 string.Equals(windows[i].ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        _loading = true;
        SelectedWindowIndex = index + 1;
        _loading = false;
        UpdateSummary();
        return true;
    }

    public void SetAutoShift(int semitones)
    {
        AutoShiftText = semitones == 0 ? "智能移调 0（无需移调）" : $"智能移调 {(semitones > 0 ? "+" : string.Empty)}{semitones}";
    }

    private void Push()
    {
        if (_loading)
        {
            return;
        }

        nint handle = SelectedWindowIndex > 0 && SelectedWindowIndex < Windows.Count
            ? Windows[SelectedWindowIndex].Hwnd
            : nint.Zero;

        WindowInfo? selected = SelectedWindowIndex > 0 && SelectedWindowIndex < Windows.Count
            ? Windows[SelectedWindowIndex]
            : null;

        int track = SelectedTrackIndex <= 0 ? -1 : SelectedTrackIndex - 1;

        _services.Engine.SetChannel(Index, state =>
        {
            state.Enabled = Enabled;
            state.Transpose = (int)Transpose;
            state.WindowHandle = handle;
            state.TrackIndex = track;
        });

        ChannelConfig stored = _services.Config.Channels[Index];
        stored.Enabled = Enabled;
        stored.Transpose = (int)Transpose;
        stored.TrackIndex = track;
        stored.WindowHandle = handle;
        stored.WindowTitle = selected?.Title ?? string.Empty;
        stored.ProcessName = selected?.ProcessName ?? string.Empty;

        _services.Settings.RequestSave();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        string window = SelectedWindowIndex > 0 && SelectedWindowIndex < Windows.Count
            ? Windows[SelectedWindowIndex].Title
            : "前台窗口";

        string track = SelectedTrackIndex > 0 && SelectedTrackIndex < TrackChoices.Count
            ? TrackChoices[SelectedTrackIndex]
            : "全部音轨";

        RouteSummary = $"{track} → {window}";
    }

    partial void OnEnabledChanged(bool value) => Push();

    partial void OnTransposeChanged(double value) => Push();

    partial void OnSelectedWindowIndexChanged(int value) => Push();

    partial void OnSelectedTrackIndexChanged(int value) => Push();
}

/// <summary>Page model for the eight-channel routing surface.</summary>
public partial class ChannelsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services = AppServices.Current;
    private readonly EventHandler _songChangedHandler;
    private bool _disposed;

    public ChannelsViewModel()
    {
        for (int i = 0; i < AppConfig.ChannelCount; i++)
        {
            Channels.Add(new ChannelCardViewModel(i));
        }

        _songChangedHandler = (_, _) => Reload();
        _services.CurrentSongChanged += _songChangedHandler;
        RefreshWindows();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.CurrentSongChanged -= _songChangedHandler;
    }

    public ObservableCollection<ChannelCardViewModel> Channels { get; } = new();

    [ObservableProperty]
    public partial string WindowCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TrackHint { get; set; } = string.Empty;

    /// <summary>Re-enumerates the top-level windows and re-binds every channel card.</summary>
    [RelayCommand]
    public void RefreshWindows()
    {
        _windows = KeyboardSimulator.GetWindowList();
        WindowCountText = $"检测到 {_windows.Count} 个可操作窗口";
        Reload();
    }

    private List<WindowInfo> _windows = new();

    private void Reload()
    {
        IReadOnlyList<string> tracks = _services.TrackChoices;
        TrackHint = tracks.Count <= 1
            ? "尚未加载曲目，音轨只能选择「全部音轨」"
            : $"当前曲目共 {tracks.Count - 1} 条音轨";

        ChannelState[] states = _services.Engine.SnapshotChannels();
        TransposePlan plan = _services.Engine.TransposePlan;

        for (int i = 0; i < Channels.Count; i++)
        {
            ChannelCardViewModel card = Channels[i];
            ChannelConfig stored = _services.Config.Channels[i];
            ChannelState state = i < states.Length ? states[i] : new ChannelState();

            card.Load(_windows, tracks, state);

            // Window handles do not survive a restart; re-bind the saved target
            // by title + process so the routing comes back automatically.
            if (state.WindowHandle == nint.Zero && !string.IsNullOrWhiteSpace(stored.WindowTitle))
            {
                card.TryRestoreWindow(stored.WindowTitle, stored.ProcessName, _windows);
            }

            card.SetAutoShift(plan.ForTrack(state.TrackIndex));
        }
    }

    [RelayCommand]
    private void EnableAll()
    {
        foreach (ChannelCardViewModel card in Channels)
        {
            card.Enabled = true;
        }
    }

    [RelayCommand]
    private void DisableAll()
    {
        foreach (ChannelCardViewModel card in Channels)
        {
            card.Enabled = false;
        }
    }
}
