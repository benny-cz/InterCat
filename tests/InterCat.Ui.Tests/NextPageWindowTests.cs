using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A list read a page at a time whose next page cannot be read (§3.2, R21): the rows of the pages before it stay listed, the
/// rail and the inspector say the next page could not be read - the inspector why - and M asks for it again.
/// </summary>
public sealed class NextPageWindowTests
{
    private static readonly string Another = SessionStore.AnotherSession.TrimEnd('.');

    [AvaloniaFact(DisplayName = "§3.2: an HTTP channel's next page that could not be read leaves its exchanges listed, says why, and M asks for it again")]
    public async Task ExchangesStayListed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, HttpExchangeLaneTests.ManyExchanges(250, out SourceFieldRowV1[] fields), fields: fields);
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        try
        {
            await Listed(window, workspace, session, () => workspace.HttpReady,
                "250 exchanges · median 200 ns · 100 listed", "Load more exchanges (M)",
                " · grouped by WinINet's exchange numbers; E shows the buffers, and C a buffer's content", 200);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2: an RPC channel's next page that could not be read leaves its calls listed, says why, and M asks for it again")]
    public async Task CallsStayListed()
    {
        using var session = new TemporarySession();
        Guid scm = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Inventory, 400, 1) with { ResourceName = @"C:\\Tools\\caller.exe", SessionRelativeTicks = 100 },
            .. Enumerable.Range(0, 150).SelectMany(index => new[]
            {
                RpcCall(100 + (index * 4), ObservationKind.RequestStart, Direction.Outbound, 400, (ulong)(10 + (index * 2)),
                    Activity(index + 1), scm) with { SessionRelativeTicks = (100 + (index * 4)) * 100L },
                RpcCall(102 + (index * 4), ObservationKind.RequestEnd, Direction.Outbound, 400, (ulong)(11 + (index * 2)),
                    Activity(index + 1), status: 0) with { SessionRelativeTicks = (102 + (index * 4)) * 100L },
            }),
        ]);
        (MainWindow window, WorkspaceViewModel workspace) = await Opened(session);
        try
        {
            ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
            foreach (string key in new[] { caller.GroupKey, caller.Id.ToString() })
            {
                workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
                Assert.True(workspace.Descend());
            }

            await workspace.RpcReady;
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal));
            Assert.True(workspace.Descend());
            await workspace.RpcReady;
            await Listed(window, workspace, session, () => workspace.RpcReady, "150 calls · median 200 ns · 100 listed",
                "Load more calls (M)", " · paired start to stop by activity id; E shows the records", 150);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2: E's next page that could not be read leaves its records listed, says why, and M asks for it again")]
    public async Task RecordsStayListed()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { SessionRelativeTicks = 100 },
            .. Enumerable.Range(0, 250).Select(index =>
                Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 32, 100, (ulong)(10 + index))
                    .Between("127.0.0.1:50000", "10.0.0.9:443") with { SessionRelativeTicks = (10 + index) * 100L }),
        ]);
        (MainWindow window, WorkspaceViewModel workspace) = await Opened(session);
        try
        {
            window.GetControl<ListBox>("RungList").Focus();
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Assert.Equal("100 source records · more available (M)", workspace.LevelSummaryShort);
            string[] listed = [.. workspace.RungRows.Select(row => row.Key)];
            string kept = Keep(session);
            Replace(session);
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Settle(window);
            Assert.Equal(listed, workspace.RungRows.Select(row => row.Key));
            Assert.Equal((true, "Load more records (M)"), (workspace.CanLoadMore, workspace.LoadMoreLabel));
            Assert.Equal("100 source records · the next page could not be read", workspace.LevelSummaryShort);
            Assert.Equal($"Every admitted record in this session · 100 source records · the next page could not be read: {Another}",
                workspace.LevelSummary);
            Assert.Equal(string.Empty, workspace.EmptyReason);

            // With the session back in its folder, M reads the page it could not, and the failure is no longer said.
            Restore(session, kept);
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Assert.Equal(200, workspace.RungRows.Count);
            Assert.Equal("200 source records · more available (M)", workspace.LevelSummaryShort);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A list's first page of 100 listed: its next page could not be read with another session in its folder, and could
    /// once its session is back, when <paramref name="then"/> are listed.
    /// </summary>
    private static async Task Listed(MainWindow window, WorkspaceViewModel workspace, TemporarySession session, Func<Task> ready,
        string first, string label, string fullTail, int then)
    {
        Assert.Equal((100, true, label), (workspace.RungRows.Count, workspace.CanLoadMore, workspace.LoadMoreLabel));
        Assert.Equal(first, workspace.LevelSummaryShort);
        string[] listed = [.. workspace.RungRows.Select(row => row.Key)];
        string kept = Keep(session);
        Replace(session);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        await ready();
        Settle(window);

        // The rows listed stay, the page can be asked for again, and the rail and the inspector say what happened.
        Assert.Equal(listed, workspace.RungRows.Select(row => row.Key));
        Assert.Equal((true, label), (workspace.CanLoadMore, workspace.LoadMoreLabel));
        Assert.Equal(first + " · the next page could not be read", workspace.LevelSummaryShort);
        Assert.Equal($"{first} · the next page could not be read: {Another}{fullTail}", workspace.LevelSummary);
        Assert.Equal(string.Empty, workspace.EmptyReason);

        // With the session back in its folder, M reads the page it could not, and the failure is no longer said.
        Restore(session, kept);
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        await ready();
        Settle(window);
        Assert.Equal(then, workspace.RungRows.Count);
        Assert.DoesNotContain("could not be read", workspace.LevelSummary, StringComparison.Ordinal);
    }

    private static async Task<(MainWindow Window, WorkspaceViewModel Workspace)> Opened(TemporarySession session)
    {
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Settle(window);
        return (window, workspace);
    }

    /// <summary>A copy of the session's files, to put back.</summary>
    private static string Keep(TemporarySession session)
    {
        string kept = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.NextPage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kept);
        foreach (string file in Directory.EnumerateFiles(session.Path))
        {
            File.Copy(file, Path.Combine(kept, Path.GetFileName(file)));
        }

        return kept;
    }

    /// <summary>Writes another session over the shown one's folder, as a package made again at the same name is.</summary>
    private static void Replace(TemporarySession session)
    {
        string other = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.NextPage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        SessionStore written = SessionStore.Open(LocalOwnedDirectory.Open(other), Guid.NewGuid(), "replacing-tests");
        Publish(written, [Lifecycle(1, ObservationKind.Inventory, 700, 1) with { SessionRelativeTicks = 100 }]);
        written.ReleaseSegmentReaders();
        Restore(session, other);
    }

    /// <summary>Empties the session's folder and moves into it what <paramref name="from"/> holds.</summary>
    private static void Restore(TemporarySession session, string from)
    {
        session.Store.ReleaseSegmentReaders();
        SharedSessionStores.Open(session.Path).ReleaseSegmentReaders();
        foreach (string file in Directory.EnumerateFiles(session.Path))
        {
            File.Delete(file);
        }

        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Move(file, Path.Combine(session.Path, Path.GetFileName(file)));
        }

        Directory.Delete(from);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    private static void Settle(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
