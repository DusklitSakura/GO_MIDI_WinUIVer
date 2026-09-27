using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GoMidi.Core;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace GoMidi.ViewModels;

/// <summary>One entry in the keymap scheme picker.</summary>
public sealed record KeymapSchemeItem(string Id, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// Keymap editor model: scheme selection, note range, and per-note rebinding
/// against the on-screen keyboard.
/// </summary>
public partial class KeymapViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private bool _loading;

    public KeymapViewModel()
    {
        ReloadSchemes();
    }

    public ObservableCollection<KeymapSchemeItem> Schemes { get; } = new();

    [ObservableProperty]
    public partial int SelectedSchemeIndex { get; set; }

    [ObservableProperty]
    public partial double MinPitch { get; set; } = 48;

    [ObservableProperty]
    public partial double MaxPitch { get; set; } = 84;

    [ObservableProperty]
    public partial string SchemeHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedNoteText { get; set; } = "未选择音符";

    [ObservableProperty]
    public partial string SelectedBindingText { get; set; } = "点击琴键后按下任意按键即可绑定；右键清除";

    [ObservableProperty]
    public partial string MappingCountText { get; set; } = string.Empty;

    public KeyManager Keys => _services.Keys;

    public int SelectedPitch { get; private set; } = -1;

    /// <summary>Raised when the note range or the mapping changed, so the keyboard repaints.</summary>
    public event EventHandler? KeymapChanged;

    public void ReloadSchemes()
    {
        _loading = true;

        Schemes.Clear();
        Schemes.Add(new KeymapSchemeItem(KeyManager.SchemeFf14, KeyManager.Ff14DisplayName));
        Schemes.Add(new KeymapSchemeItem(KeyManager.SchemeYys, KeyManager.YysDisplayName));
        foreach (KeymapSchemeDto scheme in _services.Config.KeymapSchemes)
        {
            Schemes.Add(new KeymapSchemeItem(scheme.Name, scheme.Name));
        }

        string current = _services.Config.CurrentKeymap;
        int index = 0;
        for (int i = 0; i < Schemes.Count; i++)
        {
            if (string.Equals(Schemes[i].Id, current, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        SelectedSchemeIndex = index;
        _loading = false;

        MinPitch = _services.Keys.MinPitch;
        MaxPitch = _services.Keys.MaxPitch;

        UpdateHint();
        RefreshCounts();
    }

    partial void OnSelectedSchemeIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= Schemes.Count)
        {
            return;
        }

        _services.ApplyKeymapScheme(Schemes[value].Id);

        _loading = true;
        MinPitch = _services.Keys.MinPitch;
        MaxPitch = _services.Keys.MaxPitch;
        _loading = false;

        UpdateHint();
        RefreshCounts();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnMinPitchChanged(double value) => ApplyRange();

    partial void OnMaxPitchChanged(double value) => ApplyRange();

    private void ApplyRange()
    {
        if (_loading)
        {
            return;
        }

        int low = (int)Math.Round(MinPitch);
        int high = (int)Math.Round(MaxPitch);
        if (low >= high)
        {
            return;
        }

        _services.Engine.SetRange(low, high);
        _services.Config.MinPitch = low;
        _services.Config.MaxPitch = high;
        _services.Keys.SetRange(low, high);
        _services.Settings.RequestSave();

        UpdateHint();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a captured keystroke to the given note.</summary>
    public void SetBinding(int pitch, int vkCode, int modifier)
    {
        _services.Keys.SetNote(pitch, new KeyMapping(vkCode, modifier));
        _services.Engine.NotifyKeymapChanged();
        _services.Settings.RequestSave();

        SelectedNoteText = $"{NoteNames.FromPitch(pitch)}  →  {Describe(vkCode, modifier)}";
        SelectedBindingText = "已绑定。继续点击其他琴键可以接着修改，按 Esc 结束。";
        RefreshCounts();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearBinding(int pitch)
    {
        _services.Keys.SetNote(pitch, default);
        _services.Engine.NotifyKeymapChanged();
        _services.Settings.RequestSave();

        SelectedNoteText = $"{NoteNames.FromPitch(pitch)}  →  未绑定";
        RefreshCounts();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectPitch(int pitch)
    {
        SelectedPitch = pitch;
        KeyMapping mapping = _services.Keys.GetMapping(pitch);
        SelectedNoteText = mapping.IsValid
            ? $"{NoteNames.FromPitch(pitch)}  →  {Describe(mapping.VkCode, mapping.Modifier)}"
            : $"{NoteNames.FromPitch(pitch)}  →  未绑定";
        SelectedBindingText = "按下任意按键完成绑定；Delete 清除；Esc 结束编辑。";
    }

    [RelayCommand]
    private async Task NewSchemeAsync()
    {
        string? name = await PromptAsync("另存为键位方案", "我的键位", "保存");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_services.SaveCurrentKeymapAsScheme(name, (int)MinPitch, (int)MaxPitch))
        {
            App.ShowInfoBar("无法保存", "该名称与内置方案冲突或无效。", InfoBarSeverity.Warning);
            return;
        }

        ReloadSchemes();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
        App.ShowInfoBar("已保存", $"键位方案「{name}」已保存。");
    }

    [RelayCommand]
    private async Task RenameSchemeAsync()
    {
        KeymapSchemeItem? current = CurrentScheme;
        if (current is null || IsBuiltIn(current))
        {
            App.ShowInfoBar("无法重命名", "内置方案不能重命名，请先「另存为」。", InfoBarSeverity.Warning);
            return;
        }

        string? name = await PromptAsync("重命名键位方案", current.Display, "保存");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_services.RenameScheme(current.Id, name, (int)MinPitch, (int)MaxPitch))
        {
            App.ShowInfoBar("无法重命名", "该名称已被占用。", InfoBarSeverity.Warning);
            return;
        }

        ReloadSchemes();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task DeleteSchemeAsync()
    {
        KeymapSchemeItem? current = CurrentScheme;
        if (current is null || IsBuiltIn(current))
        {
            App.ShowInfoBar("无法删除", "内置方案不能删除。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = App.CurrentXamlRoot,
            Title = "删除键位方案",
            Content = $"将删除方案「{current.Display}」。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _services.DeleteScheme(current.Id);
        ReloadSchemes();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        string? path;
        try
        {
            path = await GoMidi.Services.FilePickers.PickKeymapFileAsync();
        }
        catch (Exception ex)
        {
            Log.Error("导入键位失败", ex);
            App.ShowInfoBar("无法打开文件选择器", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (path is null)
        {
            return;
        }

        if (!_services.Keys.LoadConfig(path))
        {
            App.ShowInfoBar("导入失败", "文件里没有可识别的键位映射。", InfoBarSeverity.Error);
            return;
        }

        _services.Engine.NotifyKeymapChanged();
        _services.Settings.RequestSave();
        RefreshCounts();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
        App.ShowInfoBar("导入成功", $"已从 {System.IO.Path.GetFileName(path)} 载入键位。保存为方案后可以长期保留。", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        string? path;
        try
        {
            path = await GoMidi.Services.FilePickers.PickKeymapSavePathAsync("键位映射");
        }
        catch (Exception ex)
        {
            Log.Error("导出键位失败", ex);
            App.ShowInfoBar("无法打开保存对话框", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (path is null)
        {
            return;
        }

        bool ok = _services.Keys.SaveConfig(path);
        App.ShowInfoBar(
            ok ? "导出成功" : "导出失败",
            ok ? $"已写入 {System.IO.Path.GetFileName(path)}。" : "无法写入该位置。",
            ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    [RelayCommand]
    private void ResetDefault()
    {
        _services.ApplyKeymapScheme(KeyManager.SchemeFf14);
        ReloadSchemes();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void LoadYys()
    {
        _services.ApplyKeymapScheme(KeyManager.SchemeYys);
        ReloadSchemes();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    private KeymapSchemeItem? CurrentScheme =>
        SelectedSchemeIndex >= 0 && SelectedSchemeIndex < Schemes.Count ? Schemes[SelectedSchemeIndex] : null;

    private static bool IsBuiltIn(KeymapSchemeItem item) =>
        item.Id is KeyManager.SchemeFf14 or KeyManager.SchemeYys;

    private void UpdateHint()
    {
        SchemeHint = CurrentScheme is { } scheme && IsBuiltIn(scheme)
            ? $"{scheme.Display} · 内置方案，修改后请用「另存为」保留"
            : "自定义方案 · 修改会自动保存";

        MappingCountText = $"已绑定 {_services.Keys.Snapshot().Count} 个音符";
    }

    private void RefreshCounts() => UpdateHint();

    private static string Describe(int vkCode, int modifier)
    {
        string mods = KeyManager.DescribeModifier(modifier);
        string key = KeyManager.DescribeKey(vkCode);
        return mods.Length == 0 ? key : $"{mods}+{key}";
    }

    private static async Task<string?> PromptAsync(string title, string initial, string primaryText)
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
}
