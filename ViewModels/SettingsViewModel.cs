using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoMidi.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Reflection;

namespace GoMidi.ViewModels;

/// <summary>
/// Settings surface: latency, chord handling, NTP-backed scheduled playback,
/// the global hotkey, and where the app keeps its files.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services = AppServices.Current;
    private readonly DispatcherQueueTimer _clock;
    private readonly System.ComponentModel.PropertyChangedEventHandler _schedulerHandler;
    private bool _loading;
    private bool _disposed;

    public SettingsViewModel()
    {
        _loading = true;

        AppConfig config = _services.Config;
        LatencyCompensation = config.LatencyCompensationMs;
        HotkeyEnabled = config.HotkeyEnabled;
        DecomposeChords = config.DecomposeChords;
        Speed = config.Speed;
        ScheduleTime = DateTimeOffset.Now.AddMinutes(1).TimeOfDay;
        ScheduleSeconds = 0;

        _loading = false;

        VersionText = ResolveVersion();
        ConfigPathText = ConfigService.DataFolder;

        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();

        // The scheduler is a process-wide singleton; this page is not, so the
        // handler has to be removed again when the page goes away.
        _schedulerHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(Services.ScheduledPlaybackService.IsArmed))
            {
                IsScheduled = _services.Scheduler.IsArmed;
            }
            else if (e.PropertyName is nameof(Services.ScheduledPlaybackService.CountdownText))
            {
                CountdownText = _services.Scheduler.CountdownText;
            }
        };

        _services.Scheduler.PropertyChanged += _schedulerHandler;

        UpdateClock();
        UpdateNtpStatus();
        UpdatePrivilege();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clock.Stop();
        _services.Scheduler.PropertyChanged -= _schedulerHandler;
    }

    [ObservableProperty]
    public partial double LatencyCompensation { get; set; }

    [ObservableProperty]
    public partial bool HotkeyEnabled { get; set; }

    [ObservableProperty]
    public partial bool DecomposeChords { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; }

    [ObservableProperty]
    public partial TimeSpan ScheduleTime { get; set; }

    [ObservableProperty]
    public partial double ScheduleSeconds { get; set; }

    [ObservableProperty]
    public partial bool IsScheduled { get; set; }

    [ObservableProperty]
    public partial string CountdownText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClockText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NtpStatusText { get; set; } = "尚未同步";

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigPathText { get; set; } = string.Empty;

    // -- run privilege -------------------------------------------------------

    public bool IsElevated { get; } = AppServices.Current.IsElevated;

    [ObservableProperty]
    public partial string PrivilegeTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivilegeDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivilegeActionText { get; set; } = string.Empty;

    private void UpdatePrivilege()
    {
        PrivilegeTitle = IsElevated ? "管理员权限" : "普通权限";
        PrivilegeDetail = IsElevated
            ? "当前以管理员权限运行。"
            : "当前以普通权限运行。";
        PrivilegeActionText = IsElevated ? "切换为普通权限" : "切换为管理员权限";
    }

    /// <summary>Relaunches the app at the opposite privilege level.</summary>
    [RelayCommand]
    private void TogglePrivilege()
    {
        bool wantElevated = !IsElevated;

        if (!_services.TryRestart(wantElevated, out string error))
        {
            App.ShowInfoBar("无法切换运行权限", error, InfoBarSeverity.Warning);
            return;
        }

        // The new instance is already starting; this one hands over and exits.
        App.Window.Close();
    }

    // -- live values ---------------------------------------------------------

    private void UpdateClock()
    {
        DateTimeOffset ntp = _services.Ntp.NetworkTime;
        ClockText = _services.Ntp.IsSynchronized
            ? $"{ntp:HH:mm:ss}（网络时间）"
            : $"{DateTimeOffset.Now:HH:mm:ss}（本机时间）";
    }

    private void UpdateNtpStatus()
    {
        NtpStatusText = _services.Ntp.IsSynchronized
            ? $"已同步 · {_services.Ntp.LastServer} · 偏移 {_services.Ntp.Offset.TotalMilliseconds:+0;-0;0} ms · 往返 {_services.Ntp.RoundTripMs:0} ms"
            : string.IsNullOrEmpty(_services.Ntp.LastError)
                ? "尚未同步，定时播放将使用本机时间"
                : $"同步失败：{_services.Ntp.LastError}";
    }

    [RelayCommand]
    private async Task SyncNtpAsync()
    {
        IsSyncing = true;
        NtpStatusText = "正在同步…";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        bool ok = await _services.Ntp.SyncAsync(cts.Token);

        IsSyncing = false;
        UpdateNtpStatus();
        UpdateClock();

        App.ShowInfoBar(
            ok ? "时间同步完成" : "时间同步失败",
            NtpStatusText,
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    [RelayCommand]
    private void ToggleSchedule()
    {
        if (IsScheduled)
        {
            _services.Scheduler.Cancel();
            IsScheduled = false;
            CountdownText = string.Empty;
            _services.SetStatus("已取消定时播放");
            return;
        }

        TimeSpan target = ScheduleTime + TimeSpan.FromSeconds(Math.Clamp(ScheduleSeconds, 0, 59));
        _services.Scheduler.Arm(target);
        IsScheduled = _services.Scheduler.IsArmed;
        CountdownText = _services.Scheduler.CountdownText;
        _services.SetStatus($"定时播放已设定 · 目标 {_services.Scheduler.TargetText}");
        App.ShowInfoBar("定时播放已设定", $"将在 {_services.Scheduler.TargetText} 自动开始演奏。");
    }

    [RelayCommand]
    private void OpenConfigFolder() => OpenFolder(ConfigService.DataFolder);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(ConfigService.LogFolder);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.ShowInfoBar("无法打开文件夹", ex.Message, InfoBarSeverity.Error);
        }
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(SettingsViewModel).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the source-revision suffix the SDK appends.
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    // -- config fan-out ------------------------------------------------------

    partial void OnLatencyCompensationChanged(double value)
    {
        if (_loading)
        {
            return;
        }

        int clamped = (int)Math.Clamp(value, 0, 500);
        _services.Engine.LatencyCompensationMs = clamped;
        _services.Config.LatencyCompensationMs = clamped;
        _services.Settings.RequestSave();
    }

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _services.Config.HotkeyEnabled = value;
        _services.ApplyHotkey();
        _services.Settings.RequestSave();
    }

    partial void OnDecomposeChordsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _services.Engine.DecomposeChords = value;
        _services.Config.DecomposeChords = value;
        _services.Settings.RequestSave();
    }

    partial void OnSpeedChanged(double value)
    {
        if (_loading)
        {
            return;
        }

        _services.Engine.Speed = value;
        _services.Config.Speed = value;
        _services.Settings.RequestSave();
    }

    /// <summary>Stops the clock while the page is off-screen. The page is cached, so this is not a teardown.</summary>
    public void Suspend() => _clock.Stop();

    /// <summary>Restarts the clock and refreshes the derived text when the page returns.</summary>
    public void Resume()
    {
        _clock.Start();
        UpdateClock();
        UpdateNtpStatus();
        UpdatePrivilege();
    }
}
