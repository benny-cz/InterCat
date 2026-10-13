using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InterCat.Desktop;

namespace InterCat.Ui.Tests;

/// <summary>
/// Answers the window's choices in turn, as a person would, and keeps what each asked; or, as a system that could not
/// show its dialog, fails to.
/// </summary>
internal sealed class AnsweredChoices(TopLevel window) : PathChoices(window)
{
    private readonly Queue<string[]?> answers = new();
    private readonly Queue<Exception> failures = new();

    public List<FilePickerSaveOptions> Saves { get; } = [];
    public List<FilePickerOpenOptions> Opens { get; } = [];
    public List<FolderPickerOpenOptions> Folders { get; } = [];
    public int Asked => Saves.Count + Opens.Count + Folders.Count;

    /// <summary>The next choice: a path, or none when null.</summary>
    public void Answer(string? answer) => answers.Enqueue(answer is null ? [] : [answer]);

    /// <summary>The next choice of several folders at once.</summary>
    public void AnswerFolders(params string[] chosen) => answers.Enqueue(chosen);

    /// <summary>The next dialog the system could not show, failing as <paramref name="failure"/>.</summary>
    public void Fail(Exception failure)
    {
        answers.Enqueue(null);
        failures.Enqueue(failure);
    }

    protected override Task<string?> AskSaveFileAsync(FilePickerSaveOptions options)
    {
        Saves.Add(options);
        return Task.FromResult(Next().FirstOrDefault());
    }

    protected override Task<string?> AskOpenFileAsync(FilePickerOpenOptions options)
    {
        Opens.Add(options);
        return Task.FromResult(Next().FirstOrDefault());
    }

    protected override Task<IReadOnlyList<string>> AskOpenFoldersAsync(FolderPickerOpenOptions options)
    {
        Folders.Add(options);
        return Task.FromResult<IReadOnlyList<string>>(Next());
    }

    /// <summary>How the system fails to find the folder a picker would start in; null while it finds it.</summary>
    public Exception? StartFailure { get; set; }

    protected override Task<IStorageFolder?> AskFolderAtAsync(string path) =>
        StartFailure is { } failure ? throw failure : base.AskFolderAtAsync(path);

    private string[] Next() => answers.Dequeue() ?? throw failures.Dequeue();
}
