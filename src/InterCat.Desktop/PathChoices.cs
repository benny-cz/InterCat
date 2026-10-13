using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace InterCat.Desktop;

/// <summary>A window that says why the system's dialog to choose a file or folder could not open.</summary>
internal interface IChoosingWindow
{
    /// <summary>Says, where the window says what its actions did, why nothing could be chosen.</summary>
    void PickerFailed(string why);
}

/// <summary>
/// Where a window asks a person to choose a file or a folder: the platform's pickers, through the window's storage
/// provider, unless a test answers in their place, so every flow behind a picker can be run whole - chosen, cancelled or
/// refused. Each returns what was chosen: a path, or null or nothing when the person chose none. A picker the system
/// could not show chooses nothing too, and its window says why: the flow behind it never takes the window down.
/// </summary>
internal class PathChoices(TopLevel window)
{
    /// <summary>A file to save to; the person may name one that does not exist yet.</summary>
    public Task<string?> SaveFileAsync(FilePickerSaveOptions options) =>
        Guarded(() => AskSaveFileAsync(options), "file", null);

    /// <summary>One existing file to open.</summary>
    public Task<string?> OpenFileAsync(FilePickerOpenOptions options) =>
        Guarded(() => AskOpenFileAsync(options), "file", null);

    /// <summary>Existing folders, as many as the options allow.</summary>
    public Task<IReadOnlyList<string>> OpenFoldersAsync(FolderPickerOpenOptions options) =>
        Guarded(() => AskOpenFoldersAsync(options), "folder", []);

    /// <summary>One existing folder.</summary>
    public async Task<string?> OpenFolderAsync(FolderPickerOpenOptions options) =>
        await OpenFoldersAsync(options) is [{ } folder, ..] ? folder : null;

    /// <summary>The folder at <paramref name="path"/>, for a picker to start in; null where the system offers none.</summary>
    public async Task<IStorageFolder?> FolderAtAsync(string path)
    {
        try
        {
            return await AskFolderAtAsync(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Where the picker starts is a convenience: without it, the picker starts where the system starts it.
            return null;
        }
    }

    /// <summary>The platform's folder at a path; a test may have it fail.</summary>
    protected virtual Task<IStorageFolder?> AskFolderAtAsync(string path) =>
        window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path));

    /// <summary>The platform's save picker; a test answers here in a person's place.</summary>
    protected virtual async Task<string?> AskSaveFileAsync(FilePickerSaveOptions options) =>
        (await window.StorageProvider.SaveFilePickerAsync(options))?.Path.LocalPath;

    /// <summary>The platform's open picker; a test answers here in a person's place.</summary>
    protected virtual async Task<string?> AskOpenFileAsync(FilePickerOpenOptions options) =>
        await window.StorageProvider.OpenFilePickerAsync(options) is [{ } file, ..] ? file.Path.LocalPath : null;

    /// <summary>The platform's folder picker; a test answers here in a person's place.</summary>
    protected virtual async Task<IReadOnlyList<string>> AskOpenFoldersAsync(FolderPickerOpenOptions options) =>
        [.. (await window.StorageProvider.OpenFolderPickerAsync(options)).Select(folder => folder.Path.LocalPath)];

    /// <summary>
    /// Asks, and when the system could not show its dialog - a shell that failed, no portal to show one - chooses
    /// nothing, and has the window say why.
    /// </summary>
    private async Task<T> Guarded<T>(Func<Task<T>> ask, string what, T none)
    {
        try
        {
            return await ask();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            (window as IChoosingWindow)?.PickerFailed(
                $"The system could not show its dialog to choose a {what}: {exception.Message.TrimEnd('.')}.");
            return none;
        }
    }
}
