using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §11.3 from the Desktop: an open session is shared as a redacted package, and a package opened in the window says it
/// is one wherever its names, IDs and records are read.
/// </summary>
public sealed class RedactedPackageWindowTests
{
    private static readonly Guid PrivateProvider = Guid.Parse("c0de5ec2-0000-4000-8000-00000000abcd");

    [AvaloniaFact(DisplayName = "11.3: the window packages the open session and opens the package as a labelled package")]
    public async Task TheWindowPackagesAndReopensASession()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100, ProviderId = PrivateProvider },
        ]);
        string destination = MainWindow.NewPackageDirectory(Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages"),
            DateTimeOffset.Now);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Button share = window.GetControl<Button>("SharePackageButton");
            Assert.False(share.IsEnabled);

            // While a capture is live, a package would hold an unfinished session: the action waits for the stop.
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Recording));
            Dispatch();
            Assert.False(share.IsEnabled);
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Complete), forceOverview: true);
            Dispatch();
            Assert.True(share.IsEnabled);

            RedactedSessionPackageResult? result = await window.WriteRedactedPackageAsync(session.Path, destination);
            Dispatch();
            Assert.NotNull(result);
            Assert.True(Directory.Exists(destination));
            Assert.Equal("Redacted session package saved", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains(destination, window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Equal("Share redacted session…", share.Content);

            Assert.True(await window.OpenSessionAsync(destination));
            Dispatch();
            var package = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.StartsWith("Redacted package", package.Snapshot.Title, StringComparison.Ordinal);
            Assert.NotNull(package.Snapshot.Redaction);
            Assert.StartsWith(OverviewWorkspace.RedactedDisclosure, package.WorkspaceDisclosure, StringComparison.Ordinal);
            Assert.Equal("Redacted session package open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains("pseudonyms", window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);

            // A package is shared as it is: none is built from a package, and its exact copy is never called unredacted.
            Assert.False(share.IsEnabled);
            Assert.Contains("already a redacted package", Assert.IsType<string>(ToolTip.GetTip(share)), StringComparison.Ordinal);
            Button copy = window.GetControl<Button>("ShareOriginalButton");
            Assert.True(copy.IsEnabled);
            Assert.Equal("Share this package…", copy.Content);
            Assert.Equal("Save an exact copy of this redacted package to share", AutomationProperties.GetHelpText(copy));
            OriginalEvidencePackagePreview preview = OriginalEvidencePackage.Preview(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination)));
            IReadOnlyList<string> disclosure = MainWindow.OriginalPackageDisclosure(preview);
            Assert.DoesNotContain(disclosure, paragraph => paragraph.Contains("nredacted", StringComparison.Ordinal));
            Assert.Contains(OriginalEvidencePackage.RedactedContents, disclosure);
            Assert.Equal(RedactedSessionPackage.Warning, disclosure[^1]);
            Assert.Equal("Share this redacted package?", MainWindow.OriginalPackagePrompt(preview).Title);
            await package.LayoutReady;
            Dispatch();
            Save(window, "redacted-package-1456x939.png");

            // In the evidence inspector a pseudonymized provider is called one; a public provider keeps its name.
            Assert.True(package.ShowEvidence());
            await package.EvidenceReady;
            Dispatch();
            string[] sources = [.. package.RungRows.Select(row =>
            {
                package.SelectedRung = row;
                return package.SelectedEvidenceFields.Single(field => field.Label == "Source").Value;
            })];
            Assert.Contains(sources, source => source.StartsWith("Microsoft-Windows-Kernel-Network", StringComparison.Ordinal));
            Assert.Contains(sources, source => source.StartsWith("provider-", StringComparison.Ordinal)
                && source.Contains("(pseudonym)", StringComparison.Ordinal));
            Assert.Equal("Open synthetic record (Enter)", package.OriginalRecordLabel);
            Assert.Equal("Redacted package", window.GetControl<TextBlock>("HealthStateText").Text);
            Assert.DoesNotContain(sources, source => source.Contains(PrivateProvider.ToString("D"), StringComparison.Ordinal));
            Dispatch();
            Save(window, "redacted-package-evidence-1456x939.png");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R18: an interval package opened in the window says what it holds in icat's words")]
    public async Task AnIntervalPackageSaysWhatItHolds()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 1_000 },
            Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 500_000 },
            Transfer(9_000, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 900_000 },
        ], coverage: TransportLedger(tcp: true, udp: false));
        string destination = MainWindow.NewPackageDirectory(Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages"),
            DateTimeOffset.Now);
        RedactedSessionPackage.Create(session.Store, destination, Committed, new TimeRange(4_000, 6_000));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(destination));
            Dispatch();
            SessionStore opened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination));
            SessionRedaction redaction = SessionRedaction.Read(opened.Root, opened.Current!)!;
            opened.ReleaseSegmentReaders();

            // The window states the package as icat session's notes do: what it is, the part of its source it holds, and
            // the warning, in one statement.
            Assert.Equal(new RedactedSessionInterval { StartTicks = 4_000, EndTicks = 6_000, LifecycleRowsOutside = 1 }, redaction.Interval);
            Assert.Equal("Redacted session package open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(redaction.Statement(System.Globalization.CultureInfo.CurrentCulture),
                window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Contains(SessionRedaction.Holds(redaction.Interval!, System.Globalization.CultureInfo.CurrentCulture),
                window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);

            // And beneath its views, wherever the status card has moved on, after what a package is.
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.StartsWith(OverviewWorkspace.RedactedDisclosure + " "
                + SessionRedaction.Holds(redaction.Interval!, System.Globalization.CultureInfo.CurrentCulture) + " ",
                workspace.WorkspaceDisclosure, StringComparison.Ordinal);

            // Its health strip says what its ledger says within the interval, and that outside it nothing is known; the
            // time scope of the whole of it says its coverage is unknown, and why.
            Assert.Equal("No loss reported within this package's interval · coverage unknown outside it",
                window.GetControl<TextBlock>("HealthLossText").Text);
            Assert.Contains(SessionCoverage.OutsideItsEpochs, workspace.ScopeCoverage, StringComparison.Ordinal);
            Assert.True(workspace.ScopeCoverageLimited);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "11.3: sharing offers the time scope first, in its package's words, and a session over the bound only an interval")]
    public async Task SharingOffersTheTimeScope()
    {
        using var session = new TemporarySession();
        Publish(session.Store, IntervalSource(), coverage: TransportLedger(tcp: true, udp: false));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
            var scope = new TimeRange(4_000, 6_000);

            // With a time scope it is the first choice, and what the package keeps is said as its readers will say it.
            Window prompt = MainWindow.RedactedPackagePrompt(overview, scope);
            Task<MainWindow.RedactedPackageScope?> chosen = prompt.ShowDialog<MainWindow.RedactedPackageScope?>(owner);
            Dispatch();
            (RadioButton part, RadioButton whole) = Choices(prompt);
            Assert.True(part.IsVisible && part.IsChecked == true && whole.IsVisible && whole.IsEnabled && whole.IsChecked != true);
            Assert.Equal($"Only its time scope, from {SessionRedaction.Seconds(4_000, culture)} to {SessionRedaction.Seconds(6_000, culture)}",
                part.Content);
            TextBlock kept = Kept(prompt);
            Assert.EndsWith(SessionRedaction.Holds(new() { StartTicks = 4_000, EndTicks = 6_000, LifecycleRowsOutside = 0 }, culture),
                kept.Text, StringComparison.Ordinal);
            whole.IsChecked = true;
            Dispatch();
            Assert.StartsWith($"Kept: every record, {overview.ObservationRows:N0} in all", kept.Text, StringComparison.Ordinal);
            part.IsChecked = true;
            Dispatch();
            Assert.DoesNotContain(prompt.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsVisible
                && text.Text?.Contains("an interval at a time", StringComparison.Ordinal) == true);
            Proceed(prompt).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(scope, (await chosen)!.Interval);

            // Choosing the whole session shares all of it, scope or none.
            Window instead = MainWindow.RedactedPackagePrompt(overview, scope);
            Task<MainWindow.RedactedPackageScope?> entire = instead.ShowDialog<MainWindow.RedactedPackageScope?>(owner);
            Dispatch();
            Choices(instead).Whole.IsChecked = true;
            Dispatch();
            Proceed(instead).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null((await entire)!.Interval);

            // Without one the whole session is what is shared, and no choice is offered.
            Window plain = MainWindow.RedactedPackagePrompt(overview);
            Task<MainWindow.RedactedPackageScope?> all = plain.ShowDialog<MainWindow.RedactedPackageScope?>(owner);
            Dispatch();
            Assert.All(new[] { Choices(plain).Part, Choices(plain).Whole }, choice => Assert.False(choice.IsVisible));
            Proceed(plain).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null((await all)!.Interval);

            // A session over the bound is shared an interval at a time: without a scope nothing can go ahead, and it says why.
            SessionOverviewBundle large = overview with { ObservationRows = RedactedSessionPackage.MaximumRows + 1 };
            Window refused = MainWindow.RedactedPackagePrompt(large);
            _ = refused.ShowDialog<MainWindow.RedactedPackageScope?>(owner);
            Dispatch();
            Assert.False(Proceed(refused).IsEnabled);
            Assert.False(Choices(refused).Whole.IsEnabled);
            Assert.False(Kept(refused).IsVisible);
            Assert.Contains(refused.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsVisible
                && text.Text?.Contains("so it is shared an interval at a time", StringComparison.Ordinal) == true);
            refused.Close();
            Window scoped = MainWindow.RedactedPackagePrompt(large, scope);
            _ = scoped.ShowDialog<MainWindow.RedactedPackageScope?>(owner);
            Dispatch();
            Assert.True(Proceed(scoped).IsEnabled && Choices(scoped).Part.IsChecked == true && Kept(scoped).IsVisible);
            Assert.False(Choices(scoped).Whole.IsEnabled);
            scoped.Close();
        }
        finally
        {
            owner.Close();
        }

        static (RadioButton Part, RadioButton Whole) Choices(Window prompt)
        {
            RadioButton[] choices = [.. prompt.GetLogicalDescendants().OfType<RadioButton>()];
            return (choices[0], choices[1]);
        }

        static TextBlock Kept(Window prompt) => prompt.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.Text?.StartsWith("Kept:", StringComparison.Ordinal) == true);

        static Button Proceed(Window prompt) => prompt.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Choose a folder…"));
    }

    [AvaloniaFact(DisplayName = "11.3: the window packages its time scope, and says what the package holds")]
    public async Task TheWindowPackagesItsTimeScope()
    {
        using var session = new TemporarySession();
        Publish(session.Store, IntervalSource(), coverage: TransportLedger(tcp: true, udp: false));
        string destination = MainWindow.NewPackageDirectory(Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages"),
            DateTimeOffset.Now);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Complete), forceOverview: true);
            Dispatch();
            RedactedSessionPackageResult result = Assert.IsType<RedactedSessionPackageResult>(
                await window.WriteRedactedPackageAsync(session.Path, destination, new TimeRange(4_000, 6_000)));
            var held = new RedactedSessionInterval { StartTicks = 4_000, EndTicks = 6_000, LifecycleRowsOutside = 1 };
            Assert.Equal(held, result.Source.Interval);
            SessionStore package = SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination));
            Assert.Equal(held, SessionRedaction.Read(package.Root, package.Current!)!.Interval);
            package.ReleaseSegmentReaders();

            // What it saved says what it holds, as the package will once opened.
            Assert.Contains(MainWindow.RedactedPackageResultPrompt(result).GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text?.EndsWith(SessionRedaction.Holds(held, System.Globalization.CultureInfo.CurrentCulture),
                    StringComparison.Ordinal) == true);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    /// <summary>A client's creation, its sends at ticks 5,000 and 9,000, and another process's send.</summary>
    private static ObservationRowV1[] IntervalSource() =>
    [
        Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 1_000 },
        Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 500_000 },
        Transfer(9_000, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 900_000 },
        Transfer(9_500, ObservationKind.Send, AccountingSide.SendSide, 32, 200, 4)
            .Between("127.0.0.1:50001", "127.0.0.1:8081") with { SessionRelativeTicks = 950_000 },
    ];

    [AvaloniaFact(DisplayName = "11.3: a new package folder never reuses an existing name")]
    public void PackageFoldersAreNumberedRatherThanReused()
    {
        string parent = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages", Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 9, 24, 18, 30, 5, TimeSpan.Zero);
        try
        {
            string first = MainWindow.NewPackageDirectory(parent, now);
            Assert.Equal(Path.Combine(parent, "intercat-redacted-session-20260924-183005"), first);
            Directory.CreateDirectory(first);
            Assert.Equal(first + "-2", MainWindow.NewPackageDirectory(parent, now));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static CaptureUiUpdate Update(TemporarySession session, CaptureUiPhase phase) => new(
        phase, phase == CaptureUiPhase.Recording ? "Recording" : "Saved session open", "A published generation.",
        SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
