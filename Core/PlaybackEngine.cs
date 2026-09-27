using System.Diagnostics;

namespace GoMidi.Core;

/// <summary>Live routing settings for one of the eight playback channels.</summary>
public sealed class ChannelState
{
    public int Transpose { get; set; }

    public bool Enabled { get; set; }

    public nint WindowHandle { get; set; }

    /// <summary>-1 routes every track through this channel.</summary>
    public int TrackIndex { get; set; } = -1;

    public ChannelState Clone() => new()
    {
        Transpose = Transpose,
        Enabled = Enabled,
        WindowHandle = WindowHandle,
        TrackIndex = TrackIndex,
    };
}

/// <summary>Raised when the song reaches its end or is stopped by the engine.</summary>
public sealed class PlaybackEndedEventArgs : EventArgs
{
    public bool ReachedEnd { get; init; }
}

/// <summary>Why a note never made it to the keyboard.</summary>
public enum NoteDropReason
{
    None,
    ChannelDisabled,
    TrackMismatch,
    Percussion,
    OutOfRange,
    NoBinding,
}

/// <summary>
/// Outcome of the last routing pass. Without this a song whose notes are all
/// filtered out looks identical to a song that is playing normally: the
/// transport runs, but no key is ever sent.
/// </summary>
public sealed class RoutingReport
{
    public int TotalNotes { get; init; }
    public int RoutedNotes { get; init; }
    public int DroppedChannelDisabled { get; init; }
    public int DroppedTrackMismatch { get; init; }
    public int DroppedPercussion { get; init; }
    public int DroppedOutOfRange { get; init; }
    public int DroppedNoBinding { get; init; }
    public int EnabledChannels { get; init; }
    public int GlobalShift { get; init; }
    public int MinPitch { get; init; }
    public int MaxPitch { get; init; }
    public bool PercussionIncluded { get; init; }

    /// <summary>Channels pointing at a window handle that no longer exists.</summary>
    public int StaleWindowChannels { get; init; }

    /// <summary>How many pitches inside the current range have a key bound to them.</summary>
    public int BindingsInRange { get; init; }

    public bool HasPlayableNotes => RoutedNotes > 0;

    /// <summary>The single most useful explanation for an empty routing pass.</summary>
    public string ExplainSilence()
    {
        if (TotalNotes == 0)
        {
            return "这个文件里没有音符。";
        }

        // Check the keymap before the range: with nothing bound, widening the
        // range would not help, and saying so would send the user the wrong way.
        if (BindingsInRange == 0)
        {
            return $"当前键位方案在音域 {MinPitch}–{MaxPitch} 内没有任何按键绑定。" +
                   "请到「键位」页点击琴键绑定按键，或换用内置的「默认键位」方案。";
        }

        if (DroppedOutOfRange >= DroppedNoBinding && DroppedOutOfRange > 0)
        {
            return $"文件里有 {TotalNotes} 个音符，但移调后都落在当前音域之外（{MinPitch}–{MaxPitch}）。" +
                   "请到「键位」页把音域调宽，或换一个键位方案。";
        }

        if (DroppedNoBinding > 0)
        {
            return $"文件里有 {TotalNotes} 个音符，但当前键位方案没有为这些音高绑定按键。请到「键位」页补上绑定。";
        }

        if (DroppedPercussion > 0)
        {
            return "这个文件只有第 10 通道（打击乐）的音符，自动路由会跳过它们。";
        }

        if (DroppedTrackMismatch > 0)
        {
            return "「通道」页里选择的音轨和这个文件对不上，请改成「全部音轨」或选对音轨。";
        }

        return "没有可播放的音符。";
    }

    public string Describe() =>
        $"音符 {TotalNotes} → 已排程 {RoutedNotes}；" +
        $"丢弃：超音域 {DroppedOutOfRange}、无键位 {DroppedNoBinding}、打击乐 {DroppedPercussion}、" +
        $"音轨不匹配 {DroppedTrackMismatch}、通道关闭 {DroppedChannelDisabled}；" +
        $"启用通道 {EnabledChannels}，全局移调 {GlobalShift:+#;-#;0}，音域 {MinPitch}-{MaxPitch}" +
        (PercussionIncluded ? "，含打击乐" : string.Empty) +
        (StaleWindowChannels > 0 ? $"，失效窗口 {StaleWindowChannels}" : string.Empty);
}

/// <summary>
/// Drives note dispatch on a dedicated high-priority thread. Notes are routed
/// through the eight channel configurations, offset by an automatic
/// transposition plus the channel's manual one, and replayed through
/// <see cref="KeyboardSimulator"/>. Active keys are reference-counted so
/// overlapping notes never leave a key stuck.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    /// <summary>How far ahead of the clock a batch is released, in seconds.</summary>
    private const double LookaheadSeconds = 0.0015;

    /// <summary>Notes starting within this window of each other form one chord.</summary>
    private const double ChordThresholdSeconds = 0.03;

    /// <summary>Offset applied to each successive note of a decomposed chord.</summary>
    private const double ChordStaggerSeconds = 0.05;

    private readonly KeyManager _keys;
    private readonly KeyboardSimulator _simulator = new();
    private readonly Lock _gate = new();
    private readonly ChannelState[] _channels;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Dictionary<(int Vk, nint Hwnd), int> _activeKeys = new();

    private Thread? _thread;
    private volatile bool _shutdown;
    private volatile bool _playing;
    private volatile bool _paused;

    private double _currentTime;
    private double _duration;

    // All four of the fields below belong to the playback thread; the UI thread
    // only ever asks for work by setting one of the flags. _resumeSongTime /
    // _resumeTicks are never touched from the UI thread at all.
    private double _resumeSongTime;
    private long _resumeTicks;

    private int _configVersion;
    private int _builtVersion = -1;
    private volatile bool _seekRequested;
    private volatile bool _releaseRequested;
    private volatile bool _clockDirty;

    private List<RawNote> _notes = new();
    private PlayEvent[] _events = Array.Empty<PlayEvent>();
    private TransposePlan _plan = new();
    private RoutingReport _report = new();
    private string _loadedSongPath = string.Empty;
    private int _trackCount = 1;

    private double _speed = 1.0;
    private int _minPitch = KeyManager.Ff14MinPitch;
    private int _maxPitch = KeyManager.Ff14MaxPitch;
    private bool _decompose;
    private long _latencyCompensationUs;

    private double _loopA = -1;
    private double _loopB = -1;
    private bool _loopEnabled;

    private int _dispatchIndex;

    public PlaybackEngine(KeyManager keys)
    {
        _keys = keys;
        _channels = new ChannelState[AppConfig.ChannelCount];
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new ChannelState { Enabled = i == 0 };
        }

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "GoMidi.Playback",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    /// <summary>Raised (from the playback thread) whenever the UI should refresh.</summary>
    public event EventHandler? StateChanged;

    public bool IsPlaying => _playing;

    public bool IsPaused => _paused;

    public double CurrentTime => Volatile.Read(ref _currentTime);

    public double Duration => Volatile.Read(ref _duration);

    /// <summary>Playback rate. The original allowed 0.1×–100×.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            double clamped = Math.Clamp(value, 0.1, 100.0);
            lock (_gate)
            {
                // Re-anchor the clock so the change takes effect without a jump.
                if (_playing && !_paused)
                {
                    _resumeSongTime = ReadClock();
                    _resumeTicks = Stopwatch.GetTimestamp();
                }

                _speed = clamped;
            }
        }
    }

    public bool DecomposeChords
    {
        get => _decompose;
        set
        {
            if (_decompose == value)
            {
                return;
            }

            _decompose = value;
            Invalidate();
        }
    }

    public int MinPitch
    {
        get => _minPitch;
        set => SetRange(value, _maxPitch);
    }

    public int MaxPitch
    {
        get => _maxPitch;
        set => SetRange(_minPitch, value);
    }

    public int LatencyCompensationMs
    {
        get => (int)(Interlocked.Read(ref _latencyCompensationUs) / 1000);
        set => Interlocked.Exchange(ref _latencyCompensationUs, Math.Clamp(value, -500, 500) * 1000L);
    }

    /// <summary>Automatic transposition currently in effect, for display in the channel cards.</summary>
    public TransposePlan TransposePlan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    /// <summary>Outcome of the most recent routing pass; see <see cref="RoutingReport"/>.</summary>
    public RoutingReport LastRouting
    {
        get
        {
            lock (_gate)
            {
                return _report;
            }
        }
    }

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            if (_loopEnabled == value)
            {
                return;
            }

            _loopEnabled = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double LoopStart => _loopA;

    public double LoopEnd => _loopB;

    public void SetRange(int minPitch, int maxPitch)
    {
        int lo = Math.Clamp(Math.Min(minPitch, maxPitch), 0, 127);
        int hi = Math.Clamp(Math.Max(minPitch, maxPitch), 0, 127);
        if (lo == _minPitch && hi == _maxPitch)
        {
            return;
        }

        _minPitch = lo;
        _maxPitch = hi;
        Invalidate();
    }

    public void SetChannels(IReadOnlyList<ChannelState> channels)
    {
        lock (_gate)
        {
            for (int i = 0; i < _channels.Length && i < channels.Count; i++)
            {
                _channels[i] = channels[i].Clone();
            }
        }

        Invalidate();
    }

    public void SetChannel(int index, Action<ChannelState> mutate)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _channels.Length)
            {
                return;
            }

            mutate(_channels[index]);
        }

        Invalidate();
    }

    public ChannelState[] SnapshotChannels()
    {
        lock (_gate)
        {
            return _channels.Select(c => c.Clone()).ToArray();
        }
    }

    /// <summary>Called when the keymap changed, so events must be recomputed.</summary>
    public void NotifyKeymapChanged() => Invalidate();

    /// <summary>Sets or clears the A/B loop points. Pass -1 to clear.</summary>
    public void SetLoopPoints(double a, double b)
    {
        lock (_gate)
        {
            _loopA = a;
            _loopB = b;
            _loopEnabled = a >= 0 && b > a;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Path of the song currently held by the engine, so callers can tell whether
    /// a load request is a real change or would needlessly stop playback.
    /// </summary>
    public string LoadedSongPath
    {
        get
        {
            lock (_gate)
            {
                return _loadedSongPath;
            }
        }
    }

    public void Load(MidiFile song)
    {
        bool wasPlaying = _playing;
        Stop();

        lock (_gate)
        {
            _notes = song.Tracks.SelectMany(t => t.Notes).ToList();
            _trackCount = Math.Max(1, song.Tracks.Count);
            _duration = song.Length;
            _loadedSongPath = song.FilePath;
            _loopA = _loopB = -1;
            _loopEnabled = false;
        }

        Volatile.Write(ref _currentTime, 0);
        Volatile.Write(ref _duration, song.Length);
        Invalidate();
        WaitForRebuild();

        // A song whose notes are all filtered out plays "silently": the transport
        // runs but no key is ever sent. Record why, and let the UI say so.
        RoutingReport report = LastRouting;
        Log.Info($"路由结果：{report.Describe()}");
        if (!report.HasPlayableNotes)
        {
            Log.Warn($"没有可播放的音符：{report.ExplainSilence()}");
        }

        if (wasPlaying)
        {
            Play();
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_notes.Count == 0)
            {
                return;
            }

            if (_playing)
            {
                return;
            }

            if (_paused)
            {
                // Resume where the pause left off; the clock is re-anchored by
                // the playback thread so no timing state is written from here.
                _paused = false;
                _playing = true;
                _clockDirty = true;
                _wake.Set();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            double start = Volatile.Read(ref _currentTime);
            if (start >= _duration - 1e-6)
            {
                start = 0;
            }

            Volatile.Write(ref _currentTime, start);
            Interlocked.Increment(ref _configVersion);
            _playing = true;
            _paused = false;
            _clockDirty = true;
        }

        _wake.Set();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing || _paused)
            {
                return;
            }

            // _currentTime is refreshed by the playback thread every tick, so it
            // already holds an accurate pause position.
            _paused = true;
            _playing = false;

            // Key release happens on the playback thread; see Tick().
            _releaseRequested = true;
        }

        _wake.Set();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_gate)
        {
            bool wasActive = _playing || _paused;
            _playing = false;
            _paused = false;
            Volatile.Write(ref _currentTime, 0);
            _releaseRequested = true;

            if (!wasActive)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                _wake.Set();
                return;
            }
        }

        _wake.Set();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(double seconds)
    {
        lock (_gate)
        {
            // Marking the seek forces the playback thread to rebuild, which also
            // releases keys and re-indexes the dispatch cursor at the new point.
            Volatile.Write(ref _currentTime, Math.Clamp(seconds, 0, Math.Max(0, _duration)));
            _seekRequested = true;
            _releaseRequested = true;
        }

        _wake.Set();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Invalidate()
    {
        Interlocked.Increment(ref _configVersion);
        _wake.Set();
    }

    /// <summary>Blocks briefly until the background thread has rebuilt its event list.</summary>
    private void WaitForRebuild()
    {
        int target = Volatile.Read(ref _configVersion);
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _builtVersion) < target && sw.ElapsedMilliseconds < 1500)
        {
            Thread.Sleep(5);
        }
    }

    /// <summary>Playback-thread clock. Every field it reads is owned by that thread.</summary>
    private double ReadClock()
    {
        if (!_playing || _paused)
        {
            return Volatile.Read(ref _currentTime);
        }

        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _resumeTicks) / (double)Stopwatch.Frequency;
        return _resumeSongTime + elapsed * _speed;
    }

    // -- background thread ---------------------------------------------------

    private void ThreadMain()
    {
        NativeMethods.TimeBeginPeriod(1);
        try
        {
            while (!_shutdown)
            {
                try
                {
                    _wake.Wait(2);
                    _wake.Reset();
                    Tick();
                }
                catch (Exception ex)
                {
                    // A failure in one tick must never take the thread down.
                    Log.Error("播放线程异常", ex);
                }
            }
        }
        finally
        {
            // The final key release happens here, on the playback thread, so the
            // UI thread never touches the simulator or _activeKeys.
            ReleaseAllKeys();
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private void Tick()
    {
        // Requests raised by the UI thread are serviced here so that _activeKeys
        // and the keyboard simulator are only ever touched by this thread.
        if (_releaseRequested)
        {
            _releaseRequested = false;
            ReleaseAllKeys();
        }

        int version = Volatile.Read(ref _configVersion);
        bool needRebuild = version != Volatile.Read(ref _builtVersion) || _seekRequested;

        if (needRebuild)
        {
            _seekRequested = false;

            RebuildEvents();

            // Publish the version that was actually current when the rebuild
            // finished, so a concurrent config change is not silently marked done.
            Volatile.Write(ref _builtVersion, Volatile.Read(ref _configVersion));
            ReleaseAllKeys();

            double now = Volatile.Read(ref _currentTime);
            _dispatchIndex = LowerBound(now);
            _resumeSongTime = now;
            _resumeTicks = Stopwatch.GetTimestamp();
        }

        if (_clockDirty)
        {
            _clockDirty = false;
            _resumeSongTime = Volatile.Read(ref _currentTime);
            _resumeTicks = Stopwatch.GetTimestamp();
        }

        if (!_playing || _paused)
        {
            return;
        }

        double songTime = ReadClock();

        // An A/B loop wraps before any event past B is released.
        if (_loopEnabled && _loopB > _loopA && songTime >= _loopB)
        {
            ReleaseAllKeys();
            Volatile.Write(ref _currentTime, _loopA);
            _resumeSongTime = _loopA;
            _resumeTicks = Stopwatch.GetTimestamp();
            _dispatchIndex = LowerBound(_loopA);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        double horizon = songTime + LookaheadSeconds - Interlocked.Read(ref _latencyCompensationUs) / 1_000_000.0;

        var batch = new List<KeyInputEvent>(32);
        PlayEvent[] events = _events;
        while (_dispatchIndex < events.Length && events[_dispatchIndex].Time <= horizon)
        {
            PlayEvent e = events[_dispatchIndex++];
            batch.Add(new KeyInputEvent(e.IsNoteOn, e.Vk, e.Modifier, e.Hwnd));

            var key = (e.Vk, e.Hwnd);
            if (e.IsNoteOn)
            {
                _activeKeys.TryGetValue(key, out int count);
                _activeKeys[key] = count + 1;
            }
            else if (_activeKeys.TryGetValue(key, out int count))
            {
                if (count <= 1)
                {
                    _activeKeys.Remove(key);
                }
                else
                {
                    _activeKeys[key] = count - 1;
                }
            }
        }

        // A Pause/Stop that arrived while the batch was being assembled has
        // already released everything; dispatching now would leave the note-ons
        // in the target window with nothing to release them.
        if (batch.Count > 0 && _playing && !_paused)
        {
            _simulator.SendKeyEvents(batch);
        }
        else if (batch.Count > 0)
        {
            _activeKeys.Clear();
            return;
        }

        Volatile.Write(ref _currentTime, songTime);

        if (_dispatchIndex >= events.Length && _activeKeys.Count == 0 && songTime >= _duration - 1e-6)
        {
            _playing = false;
            _paused = false;
            ReleaseAllKeys();
            PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs { ReachedEnd = true });
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int LowerBound(double time)
    {
        PlayEvent[] events = _events;
        int lo = 0;
        int hi = events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (events[mid].Time < time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Expands every note into per-channel note-on/note-off pairs, applying the
    /// automatic transposition plus the channel's manual one.
    /// </summary>
    private void RebuildEvents()
    {
        List<RawNote> notes;
        ChannelState[] channels;
        int minPitch;
        int maxPitch;
        bool decompose;
        int trackCount;

        lock (_gate)
        {
            notes = _notes;
            channels = _channels.Select(c => c.Clone()).ToArray();
            minPitch = _minPitch;
            maxPitch = _maxPitch;
            decompose = _decompose;
            trackCount = _trackCount;
        }

        TransposePlan plan = SmartTranspose.Build(
            notes, trackCount, minPitch, maxPitch, pitch => _keys.GetMapping(pitch).IsValid);

        // With every channel switched off the original still plays through one
        // implicit global configuration rather than going silent.
        if (!channels.Any(c => c.Enabled))
        {
            channels = [new ChannelState { Enabled = true, TrackIndex = -1, Transpose = 0, WindowHandle = nint.Zero }];
        }

        // Dropping percussion only makes sense when there is something else to
        // play. A file whose notes are all on channel 10 (a drum-only
        // arrangement, or a badly exported MIDI) would otherwise be silenced
        // completely, which looks exactly like a broken player.
        bool hasMelodicNotes = notes.Any(n => n.Channel != 9);
        bool includePercussion = !hasMelodicNotes;

        // A track index left over from a longer song would silently reject every
        // note in this one, so treat an out-of-range index as "all tracks".
        var allTracksByChannel = new bool[channels.Length];
        for (int c = 0; c < channels.Length; c++)
        {
            int track = channels[c].TrackIndex;
            allTracksByChannel[c] = track < 0 || track >= trackCount;
        }

        var staleWindowChannels = 0;
        for (int c = 0; c < channels.Length; c++)
        {
            ChannelState channel = channels[c];
            if (channel.Enabled && channel.WindowHandle != nint.Zero && !NativeMethods.IsWindow(channel.WindowHandle))
            {
                staleWindowChannels++;
            }
        }

        var routed = new List<RoutedNote>(notes.Count);
        int droppedChannelDisabled = 0;
        int droppedTrackMismatch = 0;
        int droppedPercussion = 0;
        int droppedOutOfRange = 0;
        int droppedNoBinding = 0;

        foreach (RawNote note in notes)
        {
            bool routedThisNote = false;
            NoteDropReason reason = NoteDropReason.None;

            for (int c = 0; c < channels.Length; c++)
            {
                ChannelState channel = channels[c];
                if (!channel.Enabled)
                {
                    reason = Prefer(reason, NoteDropReason.ChannelDisabled);
                    continue;
                }

                bool allTracks = allTracksByChannel[c];
                if (!allTracks && channel.TrackIndex != note.TrackIndex)
                {
                    reason = Prefer(reason, NoteDropReason.TrackMismatch);
                    continue;
                }

                if (allTracks && note.Channel == 9 && !includePercussion)
                {
                    reason = Prefer(reason, NoteDropReason.Percussion);
                    continue;
                }

                // A manual transpose replaces the automatic one rather than adding to it.
                int automatic = channel.Transpose != 0
                    ? 0
                    : (allTracks ? plan.GlobalShift : plan.ForTrack(note.TrackIndex));

                int pitch = note.Pitch + automatic + channel.Transpose;
                if (pitch < minPitch || pitch > maxPitch)
                {
                    reason = Prefer(reason, NoteDropReason.OutOfRange);
                    continue;
                }

                KeyMapping mapping = _keys.GetMapping(pitch);
                if (!mapping.IsValid || mapping.VkCode == 0)
                {
                    reason = Prefer(reason, NoteDropReason.NoBinding);
                    continue;
                }

                routed.Add(new RoutedNote(
                    c, channel.WindowHandle,
                    note.StartSeconds, note.StartSeconds + note.Duration,
                    mapping.VkCode, mapping.Modifier));

                routedThisNote = true;
            }

            if (routedThisNote)
            {
                continue;
            }

            switch (reason)
            {
                case NoteDropReason.ChannelDisabled:
                    droppedChannelDisabled++;
                    break;
                case NoteDropReason.TrackMismatch:
                    droppedTrackMismatch++;
                    break;
                case NoteDropReason.Percussion:
                    droppedPercussion++;
                    break;
                case NoteDropReason.OutOfRange:
                    droppedOutOfRange++;
                    break;
                case NoteDropReason.NoBinding:
                    droppedNoBinding++;
                    break;
            }
        }

        if (decompose)
        {
            routed = ApplyChordDecomposition(routed);
        }

        var events = new List<PlayEvent>(routed.Count * 2);
        foreach (RoutedNote note in routed)
        {
            events.Add(new PlayEvent(note.Start, true, note.Vk, note.Modifier, note.Hwnd));
            events.Add(new PlayEvent(note.End, false, note.Vk, note.Modifier, note.Hwnd));
        }

        // Note-offs sort before note-ons at the same instant so repeated pitches
        // retrigger cleanly.
        events.Sort(static (a, b) =>
        {
            int byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.IsNoteOn.CompareTo(b.IsNoteOn);
        });

        var report = new RoutingReport
        {
            TotalNotes = notes.Count,
            RoutedNotes = routed.Count,
            DroppedChannelDisabled = droppedChannelDisabled,
            DroppedTrackMismatch = droppedTrackMismatch,
            DroppedPercussion = droppedPercussion,
            DroppedOutOfRange = droppedOutOfRange,
            DroppedNoBinding = droppedNoBinding,
            EnabledChannels = channels.Count(c => c.Enabled),
            GlobalShift = plan.GlobalShift,
            MinPitch = minPitch,
            MaxPitch = maxPitch,
            PercussionIncluded = includePercussion,
            StaleWindowChannels = staleWindowChannels,
            BindingsInRange = CountBindingsInRange(minPitch, maxPitch),
        };

        lock (_gate)
        {
            _plan = plan;
            _events = events.ToArray();
            _report = report;
        }
    }

    /// <summary>
    /// Keeps the more actionable explanation when several channels each drop the
    /// same note: an out-of-range note is more useful to surface than one that a
    /// disabled channel also happened to reject.
    /// </summary>
    private static NoteDropReason Prefer(NoteDropReason current, NoteDropReason candidate) =>
        Rank(candidate) > Rank(current) ? candidate : current;

    private int CountBindingsInRange(int minPitch, int maxPitch)
    {
        int count = 0;
        for (int pitch = minPitch; pitch <= maxPitch && pitch <= 127; pitch++)
        {
            if (pitch >= 0 && _keys.GetMapping(pitch).IsValid)
            {
                count++;
            }
        }

        return count;
    }

    private static int Rank(NoteDropReason reason) => reason switch
    {
        NoteDropReason.OutOfRange => 5,
        NoteDropReason.NoBinding => 4,
        NoteDropReason.Percussion => 3,
        NoteDropReason.TrackMismatch => 2,
        NoteDropReason.ChannelDisabled => 1,
        _ => 0,
    };

    /// <summary>
    /// Many games accept only one key per frame, so simultaneous notes are
    /// spread out. Chords are grouped per target window, notes that already
    /// share a key are left out of the cluster, and the remainder is truncated
    /// to monophonic so each keypress gets a clean slot.
    /// </summary>
    private static List<RoutedNote> ApplyChordDecomposition(List<RoutedNote> routed)
    {
        var result = new List<RoutedNote>(routed.Count);

        foreach (IGrouping<nint, RoutedNote> group in routed.GroupBy(n => n.Hwnd))
        {
            List<RoutedNote> ordered = group.OrderBy(n => n.Start).ToList();
            var staggered = new List<RoutedNote>(ordered.Count);

            int i = 0;
            while (i < ordered.Count)
            {
                int j = i;
                while (j + 1 < ordered.Count && ordered[j + 1].Start - ordered[i].Start <= ChordThresholdSeconds)
                {
                    j++;
                }

                var seenKeys = new HashSet<int>();
                int position = 0;
                for (int k = i; k <= j; k++)
                {
                    RoutedNote note = ordered[k];

                    // Two notes on the same key are not a chord to spread.
                    if (!seenKeys.Add(note.Vk))
                    {
                        staggered.Add(note);
                        continue;
                    }

                    double shift = position++ * ChordStaggerSeconds;
                    staggered.Add(note with { Start = note.Start + shift, End = note.End + shift });
                }

                i = j + 1;
            }

            // Monophonic truncation: a key press ends when the next one starts.
            staggered.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int k = 0; k + 1 < staggered.Count; k++)
            {
                if (staggered[k].End > staggered[k + 1].Start)
                {
                    staggered[k] = staggered[k] with { End = staggered[k + 1].Start };
                }
            }

            result.AddRange(staggered);
        }

        return result;
    }

    /// <summary>Playback-thread only. Every caller lives on that thread.</summary>
    private void ReleaseAllKeys()
    {
        try
        {
            if (_activeKeys.Count == 0)
            {
                // Nothing was pressed, so do not emit stray key-ups.
                return;
            }

            var keys = _activeKeys.Keys.Select(k => (k.Vk, k.Hwnd)).ToList();
            _activeKeys.Clear();
            _simulator.ReleaseKeys(keys);

            // A modifier can only be latched if a note was in flight.
            _simulator.ReleaseAllModifiers();
        }
        catch (Exception ex)
        {
            _activeKeys.Clear();
            Log.Error("释放按键失败", ex);
        }
    }

    public void Dispose()
    {
        if (_shutdown)
        {
            return;
        }

        // The playback thread performs the final key release in its own finally
        // block, so nothing here touches the simulator.
        _shutdown = true;
        _wake.Set();

        try
        {
            // No timeout: the loop observes _shutdown within one 2 ms wait, and a
            // bounded join could dispose _wake while the thread is still using it.
            _thread?.Join();
        }
        catch (Exception ex)
        {
            Log.Error("等待播放线程退出失败", ex);
        }

        _thread = null;
        _wake.Dispose();
    }

    /// <summary>One scheduled key transition.</summary>
    private readonly record struct PlayEvent(double Time, bool IsNoteOn, int Vk, int Modifier, nint Hwnd);

    /// <summary>A note after track routing, transposition and range filtering.</summary>
    private readonly record struct RoutedNote(
        int Channel, nint Hwnd, double Start, double End, int Vk, int Modifier);
}
