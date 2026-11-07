using System.Diagnostics;
using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A file InterCat replaces - an investigation, the window's settings - waits out another handle's brief hold on it, as
/// another window re-reading it or a scanner holds it, rather than lose the change; it refuses what no hold explains at once,
/// and a hold that outlasts its patience in the end.
/// </summary>
public sealed class FileReplacementTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), "intercat-file-replacement-tests", Guid.NewGuid().ToString("N"));

    public FileReplacementTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A reader still holding a file lets go when the test process ends.
        }
    }

    [Fact(DisplayName = "§6.8: a file InterCat replaces waits out a reader's brief hold on it, and is replaced once the reader lets go")]
    public async Task AReplaceWaitsOutABriefHold()
    {
        (string staged, string destination) = Files("new", "old");
        Task replacing;
        using (Hold(destination))
        {
            replacing = OnItsOwnThread(() => FileReplacement.Move(staged, destination, overwrite: true));
            await Task.Delay(300);
            Assert.False(replacing.IsCompleted, "The replace did not wait for the reader to let go.");
        }

        await replacing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("new", false), (File.ReadAllText(destination), File.Exists(staged)));
    }

    [Fact(DisplayName = "§6.8: a replace waits out a hold on the staged file it moves, which Windows refuses as a sharing violation rather than as access denied")]
    public async Task AReplaceWaitsOutAHoldOnItsStagedFile()
    {
        (string staged, string destination) = Files("new", "old");
        Task replacing;
        using (Hold(staged))
        {
            replacing = OnItsOwnThread(() => FileReplacement.Move(staged, destination, overwrite: true));
            await Task.Delay(300);
            Assert.False(replacing.IsCompleted, "The replace did not wait for the staged file's reader to let go.");
        }

        await replacing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("new", false), (File.ReadAllText(destination), File.Exists(staged)));
    }

    [Fact(DisplayName = "§6.8: a hold outlasting a replace's patience refuses the replace in the end, the file it would replace untouched and the staged file left to its writer")]
    public async Task AHoldOutlastingThePatienceRefusesIt()
    {
        (string staged, string destination) = Files("new", "old");
        using (Hold(destination))
        {
            long started = Stopwatch.GetTimestamp();
            Task refusing = OnItsOwnThread(() => FileReplacement.Move(staged, destination, overwrite: true, TimeSpan.FromMilliseconds(250)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => refusing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.FromMilliseconds(250), FileReplacement.Patience);
        }

        Assert.Equal(("old", "new"), (File.ReadAllText(destination), File.ReadAllText(staged)));
    }

    [Fact(DisplayName = "§6.8: a replace refuses at once what no hold explains - a file kept where it is not to be replaced, and a staged file that is not there")]
    public void WhatNoHoldExplainsIsRefusedAtOnce()
    {
        (string staged, string destination) = Files("new", "old");
        long started = Stopwatch.GetTimestamp();
        IOException kept = Assert.Throws<IOException>(() => FileReplacement.Move(staged, destination, overwrite: false, TimeSpan.FromSeconds(30)));
        Assert.IsNotType<UnauthorizedAccessException>(kept);
        Assert.Throws<FileNotFoundException>(() =>
            FileReplacement.Move(Path.Combine(root, "nowhere"), destination, overwrite: true, TimeSpan.FromSeconds(30)));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "A refusal no hold explains was waited out.");
        Assert.Equal(("old", "new"), (File.ReadAllText(destination), File.ReadAllText(staged)));
    }

    [Fact(DisplayName = "§6.8: an investigation's change waits out another window's read of its file rather than lose the change")]
    public async Task AnInvestigationsChangeWaitsOutARead()
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Now);
        Task<WorkspaceNote> noting;
        using (Hold(workspace))
        {
            noting = OnItsOwnThread(() => InvestigationWorkspace.AddNote(workspace, "kept through a read", null, Now));
            await Task.Delay(300);
            Assert.False(noting.IsCompleted, "The change did not wait for the read to end.");
        }

        WorkspaceNote note = await noting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(note.NoteId, Assert.Single(InvestigationWorkspace.Read(workspace).Notes).NoteId);
    }

    /// <summary>A staged file and the file it would replace, each holding its text.</summary>
    private (string Staged, string Destination) Files(string staged, string destination)
    {
        string stagedPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".staged");
        string destinationPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(stagedPath, staged);
        File.WriteAllText(destinationPath, destination);
        return (stagedPath, destinationPath);
    }

    /// <summary>A reader holding <paramref name="path"/> as InterCat's own reads do, sharing reading alone.</summary>
    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Runs a blocking replace on a thread of its own, as a window's save runs beside a read, so the waits it sleeps through
    /// never hold a thread the pool lends the other tests.
    /// </summary>
    private static Task OnItsOwnThread(Action replace) =>
        Task.Factory.StartNew(replace, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary><see cref="OnItsOwnThread(Action)"/>, for a change that answers what it made.</summary>
    private static Task<T> OnItsOwnThread<T>(Func<T> change) =>
        Task.Factory.StartNew(change, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
