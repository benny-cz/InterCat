using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace InterCat.Desktop;

/// <summary>
/// Where a window asks a person to choose a file or a folder: the platform's pickers, through the window's storage
/// provider, unless a test answers in their place, so every flow behind a picker can be run whole - chosen, cancelled or
/// refused. Each returns what was chosen: a path, or null or nothing when the person chose none.
/// </summary>
internal class PathChoices(TopLevel window)
{
    /// <summary>A file to save to; the person may name one that does not exist yet.</summary>
    public virtual async Task<string?> SaveFileAsync(FilePickerSaveOptions options) =>
        (await window.StorageProvider.SaveFilePickerAsync(options))?.Path.LocalPath;

    /// <summary>One existing file to open.</summary>
    public virtual async Task<string?> OpenFileAsync(FilePickerOpenOptions options) =>
        await window.StorageProvider.OpenFilePickerAsync(options) is [{ } file, ..] ? file.Path.LocalPath : null;

    /// <summary>Existing folders, as many as the options allow.</summary>
    public virtual async Task<IReadOnlyList<string>> OpenFoldersAsync(FolderPickerOpenOptions options) =>
        [.. (await window.StorageProvider.OpenFolderPickerAsync(options)).Select(folder => folder.Path.LocalPath)];

    /// <summary>One existing folder.</summary>
    public async Task<string?> OpenFolderAsync(FolderPickerOpenOptions options) =>
        await OpenFoldersAsync(options) is [{ } folder, ..] ? folder : null;
}
