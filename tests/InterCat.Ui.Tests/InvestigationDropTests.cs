using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// Sessions' folders dragged onto an investigation's window join it, as the folders Add sessions chooses do, and what
/// the window says of an addition is never lost to a reading already under way (§8.4).
/// </summary>
public sealed class InvestigationDropTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("intercat-investigation-drop-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [AvaloniaFact(DisplayName = "§8.4: sessions' folders dropped on an investigation's window join it as Add sessions adds them; an investigation file opens in its own window, and anything else says it was not added")]
    public async Task FoldersDroppedJoinTheInvestigation()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string workspace = Path.Combine(folder, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, "Not a session.");
        var main = new MainWindow { Width = 1_280, Height = 800 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            await Until(() => window.View is not null);

            // Words dragged from another window are not taken; files and folders are.
            var words = new DataTransfer();
            words.Add(DataTransferItem.CreateText(demo.ServerSession));
            Assert.Equal(DragDropEffects.None, Offered(window, words));
            Assert.Equal(DragDropEffects.Copy, Offered(window, await CarryingAsync(window, notes)));

            // Several sessions' folders join at once, as several chosen do; a file among them is not added, said so.
            const string NotAFolder = "notes.txt was not added: a session joins an investigation by its folder.";
            DropOn(window, await CarryingAsync(window, demo.ClientSession, notes, demo.ServerSession));
            await Until(() => window.View is { Members.Count: 2 });
            await Until(() => StatusOf(window) == "2 sessions added. " + NotAFolder);

            // A session already a member is refused, as Add sessions refuses it.
            DropOn(window, await CarryingAsync(window, demo.ServerSession));
            string refused = Path.GetFileName(demo.ServerSession) + " was not added: Session ";
            await Until(() => StatusOf(window)?.StartsWith(refused, StringComparison.Ordinal) == true);
            Assert.Equal(2, window.View!.Members.Count);

            // A file alone is not added either, and the window says so.
            DropOn(window, await CarryingAsync(window, notes));
            await Until(() => StatusOf(window) == NotAFolder);

            // An investigation file opens in a window of its own, as one dropped on InterCat's main window does.
            DropOn(window, await CarryingAsync(window, demo.WorkspacePath));
            await Until(() => main.Investigations.Count == 2);
            Assert.Equal(Path.GetFullPath(demo.WorkspacePath), main.Investigations[1].WorkspacePath);
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).Members.Count);
        }
        finally
        {
            foreach (Window investigation in main.Investigations.ToArray())
            {
                investigation.Close();
            }

            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: a refresh asked for while the investigation is read waits for that reading and reads again, its words kept")]
    public async Task ARefreshAskedForWhileReadingIsNotLost()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var main = new MainWindow { Width = 1_280, Height = 800 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            await Until(() => window.View is not null);

            // An addition, a relink or a note says what it did as it reads the investigation again; one finished while
            // a reading was under way said nothing, and showed what that reading found, which may have looked first.
            Task reading = window.RefreshAsync();
            Task asked = window.RefreshAsync("Asked while the investigation was read.");
            Assert.False(asked.IsCompleted);
            await Task.WhenAll(reading, asked);
            Assert.Equal("Asked while the investigation was read.", StatusOf(window));
        }
        finally
        {
            foreach (Window investigation in main.Investigations.ToArray())
            {
                investigation.Close();
            }

            main.Close();
        }
    }

    /// <summary>What a drag from the desktop carries: the files and folders at <paramref name="paths"/>.</summary>
    private static async Task<DataTransfer> CarryingAsync(TopLevel window, params string[] paths)
    {
        var carried = new DataTransfer();
        foreach (string path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
                : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            carried.Add(DataTransferItem.CreateFile(item!));
        }

        return carried;
    }

    /// <summary>What the window offers to do with what a drag carries over its middle and away again.</summary>
    private static DragDropEffects Offered(Window window, DataTransfer carried)
    {
        DragDropEffects offered = DragDropEffects.Copy | DragDropEffects.Link;
        void Note(object? sender, DragEventArgs e) => offered = e.DragEffects;
        window.AddHandler(DragDrop.DragEnterEvent, Note, handledEventsToo: true);
        window.AddHandler(DragDrop.DragOverEvent, Note, handledEventsToo: true);
        Drag(window, carried, RawDragEventType.DragEnter);
        Drag(window, carried, RawDragEventType.DragOver);
        Drag(window, carried, RawDragEventType.DragLeave);
        window.RemoveHandler(DragDrop.DragEnterEvent, Note);
        window.RemoveHandler(DragDrop.DragOverEvent, Note);
        return offered;
    }

    /// <summary>Drags what is carried over the window's middle and lets it go there, as a person drops it.</summary>
    private static void DropOn(Window window, DataTransfer carried)
    {
        Drag(window, carried, RawDragEventType.DragEnter);
        Drag(window, carried, RawDragEventType.DragOver);
        Drag(window, carried, RawDragEventType.Drop);
    }

    private static void Drag(Window window, DataTransfer carried, RawDragEventType step) => window.DragDrop(
        new Point(window.Width / 2, window.Height / 2), step, carried, DragDropEffects.Copy | DragDropEffects.Link);

    private static string? StatusOf(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Investigation status").Text;

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 200 && !condition(); wait++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }
}
