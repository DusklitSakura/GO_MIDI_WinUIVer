using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoMidi.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace GoMidi.ViewModels;

/// <summary>One row in the playlist.</summary>
public partial class PlaylistItemViewModel : ObservableObject
{
    public PlaylistItemViewModel(string path, int index)
    {
        Path = path;
        Index = index;
    }

    public string Path { get; }

    public int Index { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMissing { get; set; }
}

/// <summary>
/// Drives the player surface: playlist management, song loading, transport,
/// play modes, A/B loop and the periodic UI refresh from the playback engine.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services = AppServices.Current;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<int> _shuffleOrder = new();

    // Kept as fields so they can be unsubscribed again: the engine, the hotkey
    // service and the scheduler all outlive this view model.
    private readonly EventHandler<PlaybackEndedEventArgs> _playbackEndedHandler;
    private readonly EventHandler _togglePlaybackHandler;
    private readonly EventHandler _scheduledTriggerHandler;

    private int _shuffleCursor;
    private bool _suppressSelectionLoad;
    private bool _disposed;

    public PlayerViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += (_, _) => Refresh();

        // Every navigation creates a fresh page and therefore a fresh view model,
        // so each subscription must be removed again or the old instances keep
        // reacting to engine events forever.
        _playbackEndedHandler = OnPlaybackEnded;
        _togglePlaybackHandler = (_, _) => _dispatcher.TryEnqueue(PlayPause);
        _scheduledTriggerHandler = OnScheduledTrigger;

        _services.Engine.PlaybackEnded += _playbackEndedHandler;
        _services.TogglePlaybackRequested += _togglePlaybackHandler;
        _services.Scheduler.Triggered += _scheduledTriggerHandler;
        WireMediaSession();

        // The view model is created whenever the page is (re)opened, so it must
        // re-select whatever was playing rather than jumping to track one.
        SearchText = string.Empty;
        ReloadFromManager(selectIndex: ResolveInitialIndex());
        _timer.Start();
        UpdateMediaSession();
    }

    /// <summary>
    /// Connects the Windows media session so the hardware media keys and the
    /// system media popup drive the transport.
    /// </summary>
    private void WireMediaSession()
    {
        MediaSession? media = _services.Media;
        if (media is null)
        {
            return;
        }

        media.PreviousRequested += OnMediaPrevious;
        media.NextRequested += OnMediaNext;
        media.PlayPauseRequested += OnMediaPlayPause;
        media.StopRequested += OnMediaStop;
    }

    private void OnMediaPrevious(object? sender, EventArgs e) => Previous();

    private void OnMediaNext(object? sender, EventArgs e) => Next();

    private void OnMediaPlayPause(object? sender, EventArgs e) => PlayPause();

    private void OnMediaStop(object? sender, EventArgs e) => Stop();

    /// <summary>Pushes the track title and transport state to the media session.</summary>
    private void UpdateMediaSession()
    {
        MediaSession? media = _services.Media;
        if (media is null)
        {
            return;
        }

        if (Song is null)
        {
            media.UpdatePlaybackStatus(hasSong: false, playing: false, paused: false);
            return;
        }

        media.UpdateNowPlaying(Song.FileName, $"{Song.Tracks.Count} 条音轨 · {Song.InitialBpm:0.#} BPM");
        media.UpdatePlaybackStatus(hasSong: true, IsPlaying, IsPaused);
    }

    /// <summary>
    /// Index of the song that should be selected when the player page opens:
    /// whatever the engine already has loaded, falling back to the last song
    /// used in a previous session, and only then to the top of the list.
    /// </summary>
    private int ResolveInitialIndex()
    {
        IReadOnlyList<string> files = Playlist.Files;
        if (files.Count == 0)
        {
            return -1;
        }

        string wanted = _services.Engine.LoadedSongPath;
        if (string.IsNullOrEmpty(wanted))
        {
            wanted = _services.CurrentSong?.FilePath ?? _services.Config.LastSelectedFile;
        }

        if (!string.IsNullOrEmpty(wanted))
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i], wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Full teardown. Navigation does not call this — the page is cached and its
    /// view model lives as long as the app — but it keeps the class honest if the
    /// player is ever hosted somewhere that is destroyed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _services.Engine.PlaybackEnded -= _playbackEndedHandler;
        _services.TogglePlaybackRequested -= _togglePlaybackHandler;
        _services.Scheduler.Triggered -= _scheduledTriggerHandler;

        if (_services.Media is { } media)
        {
            media.PreviousRequested -= OnMediaPrevious;
            media.NextRequested -= OnMediaNext;
            media.PlayPauseRequested -= OnMediaPlayPause;
            media.StopRequested -= OnMediaStop;
        }
    }

    // -- observable state ----------------------------------------------------

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = "未选择文件";

    [ObservableProperty]
    public partial string CurrentDetail { get; set; } = "导入 MIDI 文件后即可开始演奏";

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial double Duration { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "00:00";

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool HasSong { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; } = 1.0;

    [ObservableProperty]
    public partial bool DecomposeChords { get; set; }

    [ObservableProperty]
    public partial string PlayModeName { get; set; } = PlayModes.Name(PlayMode.Single);

    [ObservableProperty]
    public partial string TransposeHint { get; set; } = "智能移调 0";

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial PlaylistItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial int SelectedPlaylistIndex { get; set; }

    [ObservableProperty]
    public partial bool IsLooping { get; set; }

    public ObservableCollection<PlaylistItemViewModel> VisibleFiles { get; } = new();

    public ObservableCollection<string> PlaylistNames { get; } = new();

    /// <summary>Set by the page so the piano roll and seek bar can follow the song.</summary>
    public event EventHandler<MidiFile?>? SongChanged;

    /// <summary>Set by the page to reflect A/B marker changes.</summary>
    public event EventHandler? LoopChanged;

    public MidiFile? Song { get; private set; }

    public int CurrentViewIndex { get; private set; } = -1;

    private Playlist Playlist => _services.Playlists.Current!;

    private PlayMode Mode => _services.Config.PlayMode;

    // -- playlist ------------------------------------------------------------

    private void ReloadFromManager(int selectIndex)
    {
        PlaylistNames.Clear();
        foreach (Playlist playlist in _services.Playlists.Playlists)
        {
            PlaylistNames.Add(playlist.Name);
        }

        SelectedPlaylistIndex = _services.Playlists.CurrentIndex;
        RebuildFileList();

        if (selectIndex >= 0 && selectIndex < Playlist.Files.Count)
        {
            SelectFile(selectIndex);
        }
    }

    private void RebuildFileList()
    {
        string filter = SearchText?.Trim() ?? string.Empty;
        int previousIndex = SelectedItem?.Index ?? -1;

        VisibleFiles.Clear();
        IReadOnlyList<string> files = Playlist.Files;

        for (int i = 0; i < files.Count; i++)
        {
            string fileName = System.IO.Path.GetFileName(files[i]);
            if (filter.Length > 0 && fileName.Contains(filter, StringComparison.OrdinalIgnoreCase) == false)
            {
                continue;
            }

            bool missing = !System.IO.File.Exists(files[i]);
            VisibleFiles.Add(new PlaylistItemViewModel(files[i], i)
            {
                IsMissing = missing,
                Detail = missing ? "文件不存在" : string.Empty,
            });
        }

        if (previousIndex >= 0)
        {
            _suppressSelectionLoad = true;
            SelectedItem = VisibleFiles.FirstOrDefault(i => i.Index == previousIndex);
            _suppressSelectionLoad = false;
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildFileList();

    partial void OnSelectedItemChanged(PlaylistItemViewModel? value)
    {
        // A single click loads the song without starting it, matching the original.
        if (_suppressSelectionLoad || value is null)
        {
            return;
        }

        LoadIndex(value.Index, autoPlay: false);
    }

    partial void OnSelectedPlaylistIndexChanged(int value)
    {
        if (value < 0 || value >= _services.Playlists.Count || value == _services.Playlists.CurrentIndex)
        {
            return;
        }

        _services.Playlists.SetCurrentPlaylist(value);
        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        CurrentViewIndex = -1;
        ReloadFromManager(selectIndex: -1);
        _services.SetStatus($"已切换到播放列表「{Playlist.Name}」");
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        // A failing picker used to look like a dead button, because an
        // AsyncRelayCommand swallows the exception. Log and surface everything.
        try
        {
            Log.Info("导入 MIDI：打开文件选择器");

            IReadOnlyList<string> paths = await GoMidi.Services.FilePickers.PickMidiFilesAsync();
            Log.Info($"导入 MIDI：已选择 {paths.Count} 个文件");

            if (paths.Count == 0)
            {
                return;
            }

            int added = Playlist.AddFiles(paths);
            _services.PersistPlaylists();
            _services.Settings.RequestSave();
            RebuildFileList();

            if (added == 0)
            {
                _services.SetStatus("这些文件已在播放列表中");
                return;
            }

            _services.SetStatus($"已导入 {added} 个文件");

            if (CurrentViewIndex < 0 && VisibleFiles.Count > 0)
            {
                SelectFile(VisibleFiles[0].Index);
            }
        }
        catch (Exception ex)
        {
            Log.Error("导入 MIDI 失败", ex);
            App.ShowInfoBar("无法打开文件选择器", ex.Message, InfoBarSeverity.Error);
            _services.SetStatus($"导入失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        int index = SelectedItem.Index;
        bool wasCurrent = index == CurrentViewIndex;

        Playlist.Files.RemoveAt(index);
        _services.PersistPlaylists();
        _services.Settings.RequestSave();

        if (wasCurrent)
        {
            StopCommand.Execute(null);
            CurrentViewIndex = -1;
            SetSong(null);
        }
        else if (index < CurrentViewIndex)
        {
            CurrentViewIndex--;
        }

        RebuildFileList();
    }

    [RelayCommand]
    private async Task ClearListAsync()
    {
        if (Playlist.Files.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = App.CurrentXamlRoot,
            Title = "清空播放列表",
            Content = $"将移除「{Playlist.Name}」中的 {Playlist.Files.Count} 首曲目，此操作不可撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        StopCommand.Execute(null);
        Playlist.Files.Clear();
        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        CurrentViewIndex = -1;
        SetSong(null);
        RebuildFileList();
    }

    [RelayCommand]
    private async Task NewPlaylistAsync()
    {
        string? name = await PromptForNameAsync("新建播放列表", "新建列表", "创建");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        int index = _services.Playlists.CreatePlaylist(name);
        _services.Playlists.SetCurrentPlaylist(index);
        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        CurrentViewIndex = -1;
        ReloadFromManager(selectIndex: -1);
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync()
    {
        int index = _services.Playlists.CurrentIndex;
        Playlist? playlist = _services.Playlists[index];
        if (playlist is null)
        {
            return;
        }

        string? name = await PromptForNameAsync("重命名播放列表", playlist.Name, "保存");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_services.Playlists.RenamePlaylist(index, name))
        {
            _services.SetStatus("该名称已被占用");
            return;
        }

        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        ReloadFromManager(selectIndex: CurrentViewIndex);
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        int index = _services.Playlists.CurrentIndex;
        Playlist? playlist = _services.Playlists[index];
        if (playlist is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = App.CurrentXamlRoot,
            Title = "删除播放列表",
            Content = _services.Playlists.Count == 1
                ? $"这是最后一个播放列表，将改为清空「{playlist.Name}」。"
                : $"将删除「{playlist.Name}」及其中的 {playlist.Files.Count} 首曲目。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        StopCommand.Execute(null);
        _services.Playlists.DeletePlaylist(index);
        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        CurrentViewIndex = -1;
        SetSong(null);
        ReloadFromManager(selectIndex: -1);
    }

    /// <summary>Applies a drag-reorder gesture to the underlying playlist.</summary>
    public void MoveFile(PlaylistItemViewModel source, PlaylistItemViewModel target)
    {
        int from = source.Index;
        int to = target.Index;
        if (from == to || !_services.Playlists.MoveFile(from, to))
        {
            return;
        }

        if (CurrentViewIndex == from)
        {
            CurrentViewIndex = to;
        }
        else if (from < CurrentViewIndex && to >= CurrentViewIndex)
        {
            CurrentViewIndex--;
        }
        else if (from > CurrentViewIndex && to <= CurrentViewIndex)
        {
            CurrentViewIndex++;
        }

        _services.PersistPlaylists();
        _services.Settings.RequestSave();
        RebuildFileList();
    }

    // -- transport -----------------------------------------------------------

    [RelayCommand]
    private void PlayPause()
    {
        if (Song is null)
        {
            // With nothing loaded, 播放 behaves like 下一曲 so the button is never dead.
            if (VisibleFiles.Count > 0)
            {
                PlayIndex(VisibleFiles[0].Index);
            }

            return;
        }

        PlaybackEngine engine = _services.Engine;
        if (engine.IsPlaying)
        {
            engine.Pause();
        }
        else
        {
            engine.Play();
            PrepareShuffleIfNeeded();
        }

        Refresh(force: true);
    }

    [RelayCommand]
    private void Stop()
    {
        _services.Engine.Stop();
        Refresh(force: true);
    }

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(1);

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedItem is not null)
        {
            PlayIndex(SelectedItem.Index);
        }
    }

    [RelayCommand]
    private void CyclePlayMode()
    {
        PlayMode next = PlayModes.Next(Mode);
        _services.Config.PlayMode = next;
        PlayModeName = PlayModes.Name(next);
        _services.Settings.RequestSave();
        ResetShuffle();
        _services.SetStatus($"播放模式：{PlayModeName}");
    }

    [RelayCommand]
    private void ClearLoopPoints()
    {
        _services.Engine.SetLoopPoints(-1, -1);
        IsLooping = false;
        LoopChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts the loaded song from the beginning at the scheduled moment. The
    /// original wired this to the play/pause toggle and never rewound, so a
    /// second trigger could just pause; this always starts cleanly from zero.
    /// </summary>
    private void OnScheduledTrigger(object? sender, EventArgs e)
    {
        if (Song is null)
        {
            _services.SetStatus("定时播放已触发，但没有加载曲目");
            App.ShowInfoBar("定时播放", "到点了，但还没有选择曲目。", InfoBarSeverity.Warning);
            return;
        }

        _services.Engine.Seek(0);
        _services.Engine.Play();
        Refresh(force: true);
        _services.SetStatus($"定时播放开始 · {Song.FileName}");
    }

    /// <summary>Handles a finished song according to the current play mode.</summary>
    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (!e.ReachedEnd)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            switch (Mode)
            {
                case PlayMode.Single:
                    _services.Engine.Seek(0);
                    Refresh(force: true);
                    _services.SetStatus("播放结束");
                    break;

                case PlayMode.SingleLoop:
                    _services.Engine.Seek(0);
                    _services.Engine.Play();
                    break;

                default:
                    Step(1, fromEndOfTrack: true);
                    break;
            }
        });
    }

    private void Step(int direction, bool fromEndOfTrack = false)
    {
        IReadOnlyList<string> files = Playlist.Files;
        if (files.Count == 0)
        {
            return;
        }

        int current = CurrentViewIndex;

        if (Mode == PlayMode.Shuffle && direction > 0)
        {
            if (_shuffleCursor + 1 < _shuffleOrder.Count)
            {
                _shuffleCursor++;
                PlayIndex(_shuffleOrder[_shuffleCursor]);
                return;
            }

            // One full pass finished; reshuffle for the next.
            if (Mode == PlayMode.Shuffle)
            {
                BuildShuffleOrder();
                if (_shuffleOrder.Count > 0)
                {
                    PlayIndex(_shuffleOrder[0]);
                }

                return;
            }
        }

        int index = current < 0 ? (direction > 0 ? 0 : files.Count - 1) : current + direction;

        if (index < 0 || index >= files.Count)
        {
            if (Mode is PlayMode.SequentialLoop or PlayMode.SingleLoop)
            {
                index = direction > 0 ? 0 : files.Count - 1;
            }
            else if (fromEndOfTrack)
            {
                // Non-looping modes simply stop at the end of the list.
                _services.SetStatus("播放列表已结束");
                _services.Engine.Seek(0);
                Refresh(force: true);
                return;
            }
            else
            {
                index = Math.Clamp(index, 0, files.Count - 1);
            }
        }

        PlayIndex(index);
    }

    /// <summary>Loads the song at <paramref name="index"/> and optionally starts it.</summary>
    public void PlayIndex(int index)
    {
        if (!LoadIndex(index, autoPlay: true))
        {
            return;
        }

        if (Mode == PlayMode.Shuffle)
        {
            PrepareShuffleIfNeeded();
        }
    }

    private bool LoadIndex(int index, bool autoPlay)
    {
        IReadOnlyList<string> files = Playlist.Files;
        if (index < 0 || index >= files.Count)
        {
            return false;
        }

        string path = files[index];
        MidiFile? song = Song;
        if (song is null || !string.Equals(song.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            song = MidiFile.Load(path, out string error);
            if (song is null)
            {
                _services.SetStatus($"无法加载：{error}");
                App.ShowInfoBar("无法打开 MIDI 文件", error, InfoBarSeverity.Error);
                return false;
            }
        }

        CurrentViewIndex = index;
        _services.Config.LastSelectedFile = path;
        _services.Settings.RequestSave();

        SetSong(song);

        // Re-selecting the song that is already loaded must not stop it. Without
        // this guard, reopening the player page while playing would reload the
        // same file from scratch and interrupt playback.
        bool alreadyLoaded = string.Equals(
            _services.Engine.LoadedSongPath, path, StringComparison.OrdinalIgnoreCase);

        if (!alreadyLoaded)
        {
            _services.Engine.Load(song);
        }

        // Warn before the user presses play: an all-filtered song otherwise looks
        // like it is playing while nothing reaches the game.
        RoutingReport routing = _services.Engine.LastRouting;
        if (!routing.HasPlayableNotes)
        {
            App.ShowInfoBar("这首歌没有可播放的音符", routing.ExplainSilence(), InfoBarSeverity.Warning);
        }
        else if (routing.StaleWindowChannels > 0)
        {
            App.ShowInfoBar(
                "目标窗口已失效",
                $"有 {routing.StaleWindowChannels} 个通道指向的窗口已经关闭，发过去的按键会被丢弃。请到「通道」页重新选择目标窗口。",
                InfoBarSeverity.Warning);
        }

        foreach (PlaylistItemViewModel item in VisibleFiles)
        {
        }

        _suppressSelectionLoad = true;
        SelectedItem = VisibleFiles.FirstOrDefault(i => i.Index == index);
        _suppressSelectionLoad = false;

        if (autoPlay)
        {
            _services.Engine.Play();
        }

        Refresh(force: true);
        _services.SetStatus(autoPlay ? $"正在演奏：{song.FileName}" : $"已加载：{song.FileName}");
        return true;
    }

    private void SetSong(MidiFile? song)
    {        Song = song;
        HasSong = song is not null;
        _services.SetCurrentSong(song);

        if (song is null)
        {
            CurrentTitle = "未选择文件";
            CurrentDetail = "导入 MIDI 文件后即可开始演奏";
            Duration = 0;
            DurationText = "00:00";
            DurationText = "00:00";
        }
        else
        {
            CurrentTitle = song.FileName;
            int trackCount = song.Tracks.Count;
            int noteCount = song.Tracks.Sum(t => t.NoteCount);
            CurrentDetail = $"{trackCount} 条音轨 · {noteCount} 个音符 · {song.InitialBpm:0.#} BPM · {song.InitialTimeSignatureNumerator}/{song.InitialTimeSignatureDenominator}";
            Duration = song.Length;
            DurationText = FormatTime(song.Length);

            Log.Info($"载入 {song.FileName}: format {song.Format}, {trackCount} 轨, {noteCount} 音符, " +
                     $"{song.Length:0.###}s, division {song.Division}");
            foreach (MidiTrackInfo track in song.Tracks)
            {
                Log.Info($"  轨道「{track.Name}」: {track.NoteCount} 音符, 通道 {track.Channel}");
            }
        }

        Position = 0;
        PositionText = "00:00";
        UpdateTransposeHint();
        UpdateMediaSession();
        SongChanged?.Invoke(this, song);
    }

    /// <summary>Seeks to an absolute time, used by the seek bar and by the scheduler.</summary>
    public void Seek(double seconds) => _services.Engine.Seek(seconds);

    // -- shuffle -------------------------------------------------------------

    private void ResetShuffle()
    {
        _shuffleOrder.Clear();
        _shuffleCursor = 0;
    }

    private void PrepareShuffleIfNeeded()
    {
        if (Mode == PlayMode.Shuffle && _shuffleOrder.Count != Playlist.Files.Count)
        {
            BuildShuffleOrder();
        }
    }

    private void BuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        for (int i = 0; i < Playlist.Files.Count; i++)
        {
            _shuffleOrder.Add(i);
        }

        // Fisher–Yates, matching the original's shuffled full-pass semantics.
        var random = Random.Shared;
        for (int i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        // Start the pass at the currently loaded song when there is one.
        int current = _shuffleOrder.IndexOf(CurrentViewIndex);
        _shuffleCursor = current >= 0 ? current : 0;
    }

    // -- periodic refresh ----------------------------------------------------

    private double _lastShownSeconds = -1;

    private void Refresh() => Refresh(force: false);

    private void Refresh(bool force)
    {
        PlaybackEngine engine = _services.Engine;

        double position = engine.CurrentTime;
        Position = position;

        // Only touch the label when the displayed second actually changes.
        double shown = Math.Floor(position);
        if (force || Math.Abs(shown - _lastShownSeconds) >= 1)
        {
            _lastShownSeconds = shown;
            PositionText = FormatTime(position);

            // Keep the title-bar status line in step with the elapsed time.
            UpdateStatus();
        }

        bool playing = engine.IsPlaying;
        bool paused = engine.IsPaused;
        if (force || playing != IsPlaying || paused != IsPaused)
        {
            IsPlaying = playing;
            IsPaused = paused;
            UpdateStatus();
            UpdateMediaSession();
        }

        if (force)
        {
            Duration = engine.Duration;
            IsLooping = engine.LoopEnabled;
            UpdateTransposeHint();
        }

        // The engine can rebuild its routing; keep the hint honest.
        if (playing && !force)
        {
            return;
        }
    }

    private void UpdateStatus()
    {
        if (Song is null)
        {
            _services.SetStatus("就绪 · 导入 MIDI 文件后即可开始演奏");
            return;
        }

        string state = IsPlaying ? "播放中" : IsPaused ? "已暂停" : "已就绪";
        _services.SetStatus($"{state} · {Song.FileName} · {FormatTime(Position)} / {FormatTime(Duration)}");
    }

    private void UpdateTransposeHint()
    {
        TransposePlan plan = _services.Engine.TransposePlan;
        List<string> parts = new();

        if (plan.GlobalShift != 0)
        {
            parts.Add($"全部音轨 {FormatShift(plan.GlobalShift)}");
        }

        // Only tracks that actually carry notes can receive a meaningful shift.
        for (int i = 0; i < plan.TrackShift.Length; i++)
        {
            if (plan.TrackShift[i] == 0)
            {
                continue;
            }

            string name = Song is not null && i < Song.Tracks.Count
                ? Song.Tracks[i].Name
                : $"轨道 {i + 1}";
            parts.Add($"{name} {FormatShift(plan.TrackShift[i])}");
        }

        TransposeHint = parts.Count == 0 ? "智能移调 0（无需移调）" : "智能移调 " + string.Join(" · ", parts);
    }

    private static string FormatShift(int semitones) => semitones > 0 ? $"+{semitones}" : semitones.ToString();

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        int total = (int)Math.Floor(seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    // -- helpers -------------------------------------------------------------

    private void SelectFile(int index)
    {
        _suppressSelectionLoad = true;
        SelectedItem = VisibleFiles.FirstOrDefault(i => i.Index == index);
        _suppressSelectionLoad = false;

        if (SelectedItem is not null)
        {
            LoadIndex(index, autoPlay: false);
        }
    }

    private static async Task<string?> PromptForNameAsync(string title, string initial, string primaryText)
    {
        var box = new TextBox { Text = initial, SelectionStart = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = App.CurrentXamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public void Suspend() => _timer.Stop();

    public void Resume()
    {
        _timer.Start();
        Refresh(force: true);
    }

    /// <summary>Adds files from a drag-and-drop gesture and refreshes the list.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        int added = Playlist.AddFiles(paths);
        if (added > 0)
        {
            _services.PersistPlaylists();
            _services.Settings.RequestSave();
            RebuildFileList();

            if (CurrentViewIndex < 0 && VisibleFiles.Count > 0)
            {
                SelectFile(VisibleFiles[0].Index);
            }
        }

        return added;
    }

    /// <summary>Mirrors the A/B loop state coming back from the seek bar.</summary>
    public void SetLoopState(bool looping)
    {
        IsLooping = looping;
        if (looping)
        {
            _services.SetStatus(
                $"AB 循环 · {FormatTime(_services.Engine.LoopStart)} → {FormatTime(_services.Engine.LoopEnd)}");
        }
    }

    // -- config fan-out ------------------------------------------------------

    partial void OnSpeedChanged(double value)
    {
        _services.Engine.Speed = value;
        _services.Config.Speed = value;
        _services.Settings.RequestSave();
    }

    partial void OnDecomposeChordsChanged(bool value)
    {
        _services.Engine.DecomposeChords = value;
        _services.Config.DecomposeChords = value;
        _services.Settings.RequestSave();
    }
}
