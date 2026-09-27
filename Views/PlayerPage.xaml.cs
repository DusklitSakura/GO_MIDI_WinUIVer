using GoMidi.Core;
using GoMidi.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GoMidi.Views;

/// <summary>
/// The performance surface: playlist on the left, piano roll and transport on
/// the right. Owns the render tick that drives the custom-drawn controls.
/// </summary>
public sealed partial class PlayerPage : Page
{
    private readonly DispatcherQueueTimer _renderTimer;
    private bool _wired;

    public PlayerPage()
    {
        InitializeComponent();

        _renderTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(60);
        _renderTimer.Tick += (_, _) => RenderFrame();

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();

            // Suspend rather than dispose: the page is cached, so this same view
            // model comes back when the page is shown again, and its engine
            // subscriptions must survive.
            ViewModel.Suspend();
        };
    }

    public PlayerViewModel ViewModel { get; } = new();

    // -- x:Bind helpers ------------------------------------------------------

    public static string PlayGlyph(bool isPlaying) => isPlaying ? "\uE769" : "\uE768";

    public static string PlayLabel(bool isPlaying) => isPlaying ? "暂停" : "播放";

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowWhenEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HideWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // -- lifecycle -----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_wired)
        {
            ViewModel.Resume();
            _renderTimer.Start();
            return;
        }

        _wired = true;

        ViewModel.SongChanged += (_, song) =>
        {
            Roll.SetSong(song);
            SeekBar.Duration = song?.Length ?? 0;
            SeekBar.ClearLoop();
        };

        ViewModel.LoopChanged += (_, _) => SeekBar.ClearLoop();

        SeekBar.SeekRequested += (_, seconds) =>
        {
            ViewModel.Seek(seconds);
            Roll.SetPosition(seconds);

            // Scrubbing to a point and releasing resumes playback, as it did originally.
            AppServices.Current.Engine.Play();
        };

        SeekBar.LoopPointsChanged += (_, _) =>
        {
            PlaybackEngine engine = AppServices.Current.Engine;
            bool wasLooping = engine.LoopEnabled;

            engine.SetLoopPoints(SeekBar.LoopA, SeekBar.LoopB);
            ViewModel.SetLoopState(SeekBar.HasLoop);

            // Dropping the B point jumps back to A and starts looping, matching
            // the original's third-state transition.
            if (SeekBar.HasLoop && !wasLooping)
            {
                engine.Seek(SeekBar.LoopA);
                engine.Play();
            }
        };

        Roll.SetSong(ViewModel.Song);
        SeekBar.Duration = ViewModel.Song?.Length ?? 0;
        _renderTimer.Start();
    }

    private void RenderFrame()
    {
        double time = AppServices.Current.Engine.CurrentTime;
        Roll.SetPosition(time);
        SeekBar.Position = time;
    }

    // -- playlist interactions ----------------------------------------------

    private void OnFileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ViewModel.PlaySelectedCommand.Execute(null);

    private void OnPlaySelectedItem(object sender, RoutedEventArgs e) => ViewModel.PlaySelectedCommand.Execute(null);

    private void OnRemoveSelectedItem(object sender, RoutedEventArgs e) => ViewModel.RemoveSelectedCommand.Execute(null);

    private void OnFileDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move || args.Items.Count == 0)
        {
            return;
        }

        if (args.Items[0] is PlaylistItemViewModel moved)
        {
            // Find where the item landed, then tell the model to match.
            int landing = ViewModel.VisibleFiles.IndexOf(moved);
            if (landing < 0 || landing >= ViewModel.VisibleFiles.Count)
            {
                return;
            }

            PlaylistItemViewModel target = ViewModel.VisibleFiles[Math.Min(landing, ViewModel.VisibleFiles.Count - 1)];
            if (target.Index != moved.Index)
            {
                ViewModel.MoveFile(moved, target);
            }
        }
    }

    private void OnNewPlaylist(object sender, RoutedEventArgs e) => ViewModel.NewPlaylistCommand.Execute(null);

    private void OnRenamePlaylist(object sender, RoutedEventArgs e) => ViewModel.RenamePlaylistCommand.Execute(null);

    private void OnDeletePlaylist(object sender, RoutedEventArgs e) => ViewModel.DeletePlaylistCommand.Execute(null);

    // -- drag & drop import --------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入到播放列表";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            await HandleDropAsync(e);
        }
        catch (Exception ex)
        {
            // An exception out of an async void handler is fatal, and a silent
            // failure here would look exactly like a dead drop target.
            Log.Error("拖拽导入失败", ex);
            App.ShowInfoBar("无法导入拖入的文件", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task HandleDropAsync(DragEventArgs e)
    {
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();

        foreach (IStorageItem item in items)
        {
            if (item is StorageFile file && IsMidiFile(file.Path))
            {
                paths.Add(file.Path);
            }
            else if (item is StorageFolder folder)
            {
                paths.AddRange(Directory
                    .EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                    .Where(IsMidiFile)
                    .Take(500));
            }
        }

        if (paths.Count == 0)
        {
            App.ShowInfoBar("没有可导入的文件", "请拖入 .mid 或 .midi 文件。", InfoBarSeverity.Warning);
            return;
        }

        int added = ViewModel.AddFiles(paths);
        App.ShowInfoBar("导入完成", added > 0 ? $"新增 {added} 首曲目。" : "这些曲目已经在列表中。");
    }

    private static bool IsMidiFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".midi", StringComparison.OrdinalIgnoreCase);
    }
}
