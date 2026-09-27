using Microsoft.Windows.Storage.Pickers;

namespace GoMidi.Services;

/// <summary>
/// File pickers that actually open in an unpackaged app.
///
/// The legacy <c>Windows.Storage.Pickers</c> types need package identity: called
/// from a plain .exe they fail with <c>COMException 0x80004005 (E_FAIL)</c>
/// before any UI appears. The Windows App SDK pickers take the window id
/// directly and work without a package.
/// </summary>
internal static class FilePickers
{
    /// <summary>Multi-select picker for MIDI files. Returns absolute paths.</summary>
    public static async Task<IReadOnlyList<string>> PickMidiFilesAsync()
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "导入",
        };

        picker.FileTypeFilter.Add(".mid");
        picker.FileTypeFilter.Add(".midi");

        IReadOnlyList<PickFileResult> results = await picker.PickMultipleFilesAsync();
        return results.Select(r => r.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    /// <summary>Single-select picker for a keymap text file.</summary>
    public static async Task<string?> PickKeymapFileAsync()
    {
        var picker = new FileOpenPicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };

        picker.FileTypeFilter.Add(".txt");

        PickFileResult? result = await picker.PickSingleFileAsync();
        return string.IsNullOrEmpty(result?.Path) ? null : result.Path;
    }

    /// <summary>Save picker for exporting a keymap. Returns the chosen path.</summary>
    public static async Task<string?> PickKeymapSavePathAsync(string suggestedName)
    {
        var picker = new FileSavePicker(App.Window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };

        picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });

        PickFileResult? result = await picker.PickSaveFileAsync();
        return string.IsNullOrEmpty(result?.Path) ? null : result.Path;
    }
}
