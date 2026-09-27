using CommunityToolkit.Mvvm.ComponentModel;
using GoMidi.Core;
using Microsoft.UI.Dispatching;

namespace GoMidi.Services;

/// <summary>
/// Arms a one-shot playback start at an NTP-corrected wall-clock time. The
/// original only offered minutes and seconds and could fire up to an hour late;
/// this picks the next real occurrence of the chosen time instead.
/// </summary>
public sealed partial class ScheduledPlaybackService : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueueTimer _timer;

    private DateTimeOffset _target;
    private bool _fired;

    public ScheduledPlaybackService(AppServices services)
    {
        _services = services;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Raised on the UI thread when the scheduled moment arrives.</summary>
    public event EventHandler? Triggered;

    [ObservableProperty]
    public partial bool IsArmed { get; set; }

    [ObservableProperty]
    public partial string CountdownText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetText { get; set; } = string.Empty;

    /// <summary>Arms playback for the next occurrence of the supplied local time.</summary>
    public void Arm(TimeSpan timeOfDay)
    {
        // Read the clock through the NTP offset so the start lines up with the
        // rest of the group rather than with this machine's drift.
        DateTimeOffset now = _services.Ntp.NetworkTime;
        DateTime target = now.Date + timeOfDay;
        if (target <= now.LocalDateTime)
        {
            target = target.AddDays(1);
        }

        // Resolve the offset for the target instant itself: using today's offset
        // would land an hour off if a DST change falls in between.
        _target = new DateTimeOffset(target, TimeZoneInfo.Local.GetUtcOffset(target));
        _fired = false;
        IsArmed = true;
        TargetText = _target.ToString("MM-dd HH:mm:ss");
        Poll();
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        IsArmed = false;
        CountdownText = string.Empty;
        TargetText = string.Empty;
    }

    private void Poll()
    {
        if (!IsArmed || _fired)
        {
            return;
        }

        DateTimeOffset now = _services.Ntp.NetworkTime;

        // Scheduled playback compensates for output latency by firing early.
        TimeSpan lead = TimeSpan.FromMilliseconds(Math.Max(0, _services.Engine.LatencyCompensationMs));
        TimeSpan remaining = _target - now - lead;

        if (remaining <= TimeSpan.Zero)
        {
            _fired = true;
            _timer.Stop();
            IsArmed = false;
            CountdownText = string.Empty;
            Triggered?.Invoke(this, EventArgs.Empty);
            return;
        }

        CountdownText = remaining.TotalHours >= 1
            ? $"还有 {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"还有 {remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}
