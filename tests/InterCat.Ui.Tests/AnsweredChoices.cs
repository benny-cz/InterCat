using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InterCat.Desktop;

namespace InterCat.Ui.Tests;

/// <summary>Answers the window's choices in turn, as a person would, and keeps what each asked.</summary>
internal sealed class AnsweredChoices(TopLevel window) : PathChoices(window)
{
    private readonly Queue<string[]> answers = new();

    public List<FilePickerSaveOptions> Saves { get; } = [];
    public List<FilePickerOpenOptions> Opens { get; } = [];
    public List<FolderPickerOpenOptions> Folders { get; } = [];
    public int Asked => Saves.Count + Opens.Count + Folders.Count;

    /// <summary>The next choice: a path, or none when null.</summary>
    public void Answer(string? answer) => answers.Enqueue(answer is null ? [] : [answer]);

    /// <summary>The next choice of several folders at once.</summary>
    public void AnswerFolders(params string[] chosen) => answers.Enqueue(chosen);

    public override Task<string?> SaveFileAsync(FilePickerSaveOptions options)
    {
        Saves.Add(options);
        return Task.FromResult(answers.Dequeue().FirstOrDefault());
    }

    public override Task<string?> OpenFileAsync(FilePickerOpenOptions options)
    {
        Opens.Add(options);
        return Task.FromResult(answers.Dequeue().FirstOrDefault());
    }

    public override Task<IReadOnlyList<string>> OpenFoldersAsync(FolderPickerOpenOptions options)
    {
        Folders.Add(options);
        return Task.FromResult<IReadOnlyList<string>>(answers.Dequeue());
    }
}
