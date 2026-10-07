using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Every word the window shows can be read whole (§6.8): a text too long for its place wraps, or ends in an ellipsis whose
/// tooltip holds it whole. One cut off silently - by its own box, or by a card or panel it sits in - loses what it said,
/// as the empty rung's step once read "Show source records (E".
/// </summary>
public sealed class LegibleTextTests
{
    [AvaloniaFact(DisplayName = "§6.8: at the minimum window, with the rail at its narrowest, no text is cut off: it wraps, or ends in an ellipsis its tooltip completes")]
    public async Task NoTextIsCutOff()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 40).SelectMany(index => new[]
            {
                Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
                Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (2 * index)))
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
            }),
        ]);

        foreach (bool narrowRail in new[] { false, true })
        {
            var window = new MainWindow { Width = 1080, Height = 700 };
            window.Show();
            if (narrowRail)
            {
                ColumnDefinition column = window.GetControl<Grid>("WindowGrid").ColumnDefinitions[0];
                column.Width = new GridLength(column.MinWidth);
            }

            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            var cut = new List<string>();
            string rail = narrowRail ? "narrowest rail" : "rail as it opens";
            cut.AddRange(CutOff(window, $"{rail}, machine"));

            // Down every rung to a channel, whose rung has no rows and offers its records' step beside the reason, then to
            // the records themselves.
            Channel channel = workspace.Snapshot.Channels.Single();
            ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
            foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
            {
                workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
                Assert.True(workspace.Descend());
                cut.AddRange(CutOff(window, $"{rail}, {workspace.LevelBadge}"));
            }

            Assert.True(workspace.OffersEvidenceStep);
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            workspace.SelectedRung = workspace.RungRows[3];
            cut.AddRange(CutOff(window, $"{rail}, evidence"));
            window.Close();
            Assert.True(cut.Count == 0, string.Join(Environment.NewLine, cut));
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: the content, record and channel windows cut off no text at their minimum size, however long a name")]
    public async Task NoSecondaryWindowCutsOffText()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with
            {
                ResourceName = @"C:\Program Files\Contoso Long Product Name\bin\client-with-a-long-name.exe", SessionRelativeTicks = 100,
            },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Program Files\Fabrikam\server.exe", SessionRelativeTicks = 200 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 2_400, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ];
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 4_096),
            [Content(rows[2], Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("GET /index.html HTTP/1.1\r\nHost: example\r\n", 60))), 4_096)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, resolveOwners: true);
        SessionEvidenceRecord sent = page.Records.Single(record => record.Observation.Kind == ObservationKind.Send);
        var cut = new List<string>();

        using (var content = new SessionContentWindow(session.Path, page.SessionId, sent))
        {
            await ShowAtItsMinimum(content);
            cut.AddRange(CutOff(content, "content, before its bytes"));
            content.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Show the kept bytes")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pause();
            cut.AddRange(CutOff(content, "content, with its bytes"));
        }

        using (var raw = new SessionRawRecordWindow(session.Path, page.SessionId, sent))
        {
            await ShowAtItsMinimum(raw);
            cut.AddRange(CutOff(raw, "raw record"));
        }

        // The channel browser names each channel by the processes it joins, so its rows hold the longest names.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using (var browser = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null,
            id => snapshot.Processes.FirstOrDefault(process => process.Id == id)?.NameWithPid))
        {
            browser.Width = browser.MinWidth;
            browser.Height = browser.MinHeight;
            _ = browser.ShowDialog<Channel?>(owner);
            await Pause();
            Assert.Contains(browser.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("client-with-a-long-name.exe", StringComparison.Ordinal) == true);
            cut.AddRange(CutOff(browser, "channels"));
            browser.Close();
        }

        owner.Close();
        Assert.True(cut.Count == 0, string.Join(Environment.NewLine, cut));
    }

    [AvaloniaFact(DisplayName = "§6.8: the investigation window and its dialogs cut off no text at their smallest, and each page keeps two of its sessions, lanes or notes in view")]
    public async Task TheInvestigationWindowKeepsItsPagesLegible()
    {
        // A session in a long folder, three of one host that ran at once, and one moved away: the longest name, the overlaps
        // and a missing session each take their room above the pages.
        using var root = new TemporaryDirectory();
        string alpha = Datagrams(root.Path, "alpha-capture-of-the-build-server-during-the-nightly-integration-run-with-every-service-and-its-migrations", 4, "lab-1");
        string delta = Datagrams(root.Path, "delta", 2, "lab-3");
        string workspace = Path.Combine(root.Path, "a-long-investigation-name-for-the-nightly-build-failure" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, alpha, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6, "lab-1"), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "gamma", 3, "lab-2"), Committed).SessionId;
        Guid d = InvestigationWorkspace.Add(workspace, delta, Committed).SessionId;
        Guid e = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "epsilon", 1, "lab-1"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Committed);
        InvestigationWorkspace.Align(workspace, e, 0, a, 0, 1_000, 0, null, Committed);
        InvestigationWorkspace.Align(workspace, c, 2_000_000_000, a, 5_000_000_000, 500_000, 50, null, Committed);
        InvestigationWorkspace.Align(workspace, d, 0, a, 1_000_000_000, 500_000, 50, null, Committed);
        Directory.Delete(delta, recursive: true);
        InvestigationWorkspace.AddNote(workspace, "The build server's integration run starts its database migration here, which the nightly job waits on",
            new WorkspaceNoteAnchor(c, 2_000_000_000), Committed);
        InvestigationWorkspace.AddNote(workspace, "Nothing failed before the migration began.", null, Committed);
        InvestigationWorkspace.SetPanes(workspace, 0.3712, WorkspacePane.Timeline, Committed);

        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Until(() => window.View is not null);
            Assert.Equal(3, window.View!.Overlaps!.Count);
            var cut = new List<string>();
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            string[] pages = ["sessions", "candidate joins", "timeline", "notes"];
            for (int page = 0; page < pages.Length; page++)
            {
                tabs.SelectedIndex = page;
                await Until(() => page != 2 || window.Timeline is not null);
                await Pause();
                cut.AddRange(CutOff(window, $"investigation, {pages[page]}"));
            }

            // Each page's list or chart keeps two whole sessions, lanes or notes in view; the rest scroll. What naming a session
            // and grouping hosts can say stands with the sessions.
            tabs.SelectedIndex = 0;
            await Pause();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                && text.Text?.StartsWith("Each session is named by its identity", StringComparison.Ordinal) == true);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                && text.Text?.StartsWith("Its sessions open with the timeline filling the column", StringComparison.Ordinal) == true);
            AssertKeepsTwoRows(Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one"));
            tabs.SelectedIndex = 3;
            await Pause();
            AssertKeepsTwoRows(Named<ListBox>(window, "Notes on this investigation"));
            tabs.SelectedIndex = 2;
            await Until(() => window.Timeline is not null);
            await Pause();
            InvestigationTimelineControl chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            double lanes = chart.GetVisualAncestors().OfType<ScrollViewer>().First().Viewport.Height;
            Assert.True(lanes >= InvestigationTimelineControl.AxisHeight + (2 * InvestigationTimelineControl.LaneHeight),
                $"The timeline shows {lanes:0} px of its lanes at the minimum size.");
            Assert.False(string.IsNullOrEmpty(window.ColumnReadout));
            Assert.Contains(window.TimelineSentences, sentence => sentence.Contains(": 1 record, from ", StringComparison.Ordinal));

            // Every dialog the window opens names the long session, at its own fixed width.
            tabs.SelectedIndex = 0;
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            window.SelectNote(0);
            await Pause();
            foreach ((string name, Window? dialog) in new (string, Window?)[]
            {
                ("align", window.AlignDialogForSelected()),
                ("one host", window.HostDialogForSelected()),
                ("compare", window.CompareDialog()),
                ("add a note", window.NoteDialog(reword: false)),
                ("reword a note", window.NoteDialog(reword: true)),
                ("translations", window.TranslationsDialog()),
                ("views", window.ViewsDialog()),
                ("package", new InvestigationPackageWindow(InvestigationPackage.Preview(workspace))),
            })
            {
                Assert.NotNull(dialog);
                dialog!.Show(window);
                await Pause();
                cut.AddRange(CutOff(dialog, name));
                dialog.Close();
            }

            window.Close();
            Assert.True(cut.Count == 0, string.Join(Environment.NewLine, cut));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: the investigation's timeline labels both ends of its axis and never runs two labels together, and a lane's label keeps to its lane")]
    public void TheInvestigationTimelineKeepsItsLabelsApart()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4, "lab-1"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6, "lab-2"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 2_000_000_000, a, 5_000_000_000, 500_000, 50, null, Committed);
        double gap = 8;

        // A whole investigation's seconds are short; a zoom to a few microseconds of it gives each label its full precision.
        foreach (TimeRange? zoom in new TimeRange?[] { null, new TimeRange(1_000, 1_031) })
        {
            (InvestigationTimelineView view, IReadOnlyList<string> labels, _) =
                InvestigationRows.Timeline(workspace, CultureInfo.CurrentCulture, 160, zoom, CancellationToken.None);
            var chart = new InvestigationTimelineControl();
            chart.Show(view, labels);
            for (double width = InvestigationTimelineControl.LabelWidth + 40; width <= 1_200; width += 20)
            {
                chart.Measure(new Size(width, 400));
                chart.Arrange(new Rect(0, 0, width, 400));
                IReadOnlyList<(string Label, Rect Where)> shown = chart.AxisLabels();
                double first = InvestigationTimelineControl.LabelWidth;
                double last = width - 12;
                if (shown.Count == 0)
                {
                    Assert.True(last - first < 200, $"At {width} px, the axis has no label.");
                    continue;
                }

                Assert.Equal(first + 3, shown[0].Where.Left, 3);
                for (int index = 1; index < shown.Count; index++)
                {
                    Assert.True(shown[index].Where.Left >= shown[index - 1].Where.Right + gap - 0.01,
                        $"At {width} px, '{shown[index - 1].Label}' and '{shown[index].Label}' run together.");
                }

                Assert.True(shown[^1].Where.Right <= last - 3 + 0.01, $"At {width} px, '{shown[^1].Label}' runs past the axis's end.");
                if (last - first >= 200)
                {
                    Assert.True(shown.Count >= 2, $"At {width} px, the axis's end is not labelled.");
                    Assert.Equal(last - 3, shown[^1].Where.Right, 3);
                }
            }
        }

        // A label too long for its lane - why a session has no place - ends within the lane rather than in the next one.
        (InvestigationTimelineView whole, IReadOnlyList<string> named, _) =
            InvestigationRows.Timeline(workspace, CultureInfo.CurrentCulture, 160, null, CancellationToken.None);
        string reason = "Session 0000aaaa · host 0000bbbb\nnot placed: " + string.Concat(Enumerable.Repeat("the session could not be read there; ", 12));
        var lanes = new InvestigationTimelineControl();
        lanes.Show(whole, [reason, .. named.Skip(1)]);
        var unbounded = new FormattedText(reason, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.Black)
        {
            MaxTextWidth = InvestigationTimelineControl.LabelWidth - 18,
        };
        Assert.True(unbounded.Height > InvestigationTimelineControl.LaneHeight);
        Assert.True(lanes.LaneLabel(0, Brushes.Black).Height <= InvestigationTimelineControl.LaneHeight - 8 + 0.01);
    }

    /// <summary>The list shows its first two rows whole.</summary>
    private static void AssertKeepsTwoRows(ListBox list)
    {
        double rows = list.ContainerFromIndex(0)!.Bounds.Height + list.ContainerFromIndex(1)!.Bounds.Height;
        double viewport = list.GetVisualDescendants().OfType<ScrollViewer>().First().Viewport.Height;
        Assert.True(viewport >= rows, $"{AutomationProperties.GetName(list)} shows {viewport:0} px of the {rows:0} its first two rows need.");
    }

    /// <summary>
    /// The one control of its kind named <paramref name="name"/>: given that name, or heard by it, as a button is by the
    /// label it shows.
    /// </summary>
    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>A session of <paramref name="records"/> datagrams, captured on <paramref name="host"/>.</summary>
    private static string Datagrams(string root, string name, int records, string host)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "legible-text-tests");
        _ = Publish(
            store,
            [
                .. Enumerable.Range(0, records).Select(index =>
                    Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                        .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), host));
        store.ReleaseSegmentReaders();
        return directory;
    }

    [AvaloniaFact(DisplayName = "§6.8: every prompt the windows build is as tall as what it says and cuts off no text, with the longest folder it can name")]
    public async Task EveryPromptFitsWhatItSays()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);

        // Packages saved several folders deep, each folder named as a person might name it.
        using var root = new TemporaryDirectory();
        string deep = Directory.CreateDirectory(Path.Combine(root.Path, "packages-shared-with-the-vendor-for-the-nightly-build-failure",
            "the-build-server-and-its-database-during-the-integration-run", "reviewed-and-checked-before-sharing-on-2026-10-06")).FullName;
        OriginalEvidencePackageResult original = OriginalEvidencePackage.Create(session.Store, Path.Combine(deep, "an-exact-copy-of-the-build-server-capture"));
        RedactedSessionPackageResult redacted = RedactedSessionPackage.Create(session.Store,
            Path.Combine(deep, "a-redacted-package-of-the-build-server-capture"), DateTimeOffset.UnixEpoch);
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, session.Path, Committed);
        InvestigationPackageResult investigation = InvestigationPackage.Create(workspace, Path.Combine(deep, "the-investigation-with-its-sessions"));

        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var cut = new List<string>();
        foreach ((string name, Window prompt) in new (string, Window)[]
        {
            ("redacted report", MainWindow.RedactedSharePrompt()),
            ("original package", MainWindow.OriginalPackagePrompt(OriginalEvidencePackage.Preview(session.Store))),
            ("original package saved", MainWindow.OriginalResultPrompt(original)),
            ("redacted package", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store))),
            ("redacted package of a time scope", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store),
                new TimeRange(1_000, 2_000))),
            ("redacted package over the bound", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store)
                with { ObservationRows = RedactedSessionPackage.MaximumRows + 1 })),
            ("redacted package saved", MainWindow.RedactedPackageResultPrompt(redacted)),
            ("stop Explore", MainWindow.StopExplorePrompt()),
            ("support bundle", MainWindow.SupportBundlePrompt(1)),
            ("investigation package saved", InvestigationWindow.PackageResultPrompt(investigation, offersOpen: true)),
        })
        {
            _ = prompt.ShowDialog<bool>(owner);
            await Pause();
            cut.AddRange(CutOff(prompt, name));

            // A prompt sized for one length of words leaves a band empty below its buttons when they are shorter, and runs
            // them past its foot when they are longer: each is as tall as what it says.
            double says = Assert.IsAssignableFrom<Control>(prompt.Content).DesiredSize.Height;
            if (Math.Abs(prompt.Bounds.Height - says) > 1)
            {
                cut.Add($"{name}: {prompt.Bounds.Height:0} px tall for {says:0} px of words and buttons.");
            }

            prompt.Close();
        }

        owner.Close();
        Assert.True(cut.Count == 0, string.Join(Environment.NewLine, cut));
    }

    private static async Task ShowAtItsMinimum(Window window)
    {
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        await Pause();
    }

    /// <summary>Lets a window read what it shows, on its own thread, and lay it out.</summary>
    private static async Task Pause()
    {
        for (int wait = 0; wait < 40; wait++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Each visible text the window cuts off where it stands: one its place leaves no room at all, one wider or taller than
    /// its own box shows that neither wraps nor ends in an ellipsis, one a clipping card, panel or the window itself cuts at
    /// any edge, and one that ends in an ellipsis with no tooltip to complete it.
    /// </summary>
    internal static IEnumerable<string> CutOff(Window window, string where)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = window.CaptureRenderedFrame();
        }

        foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
        {
            if (text.Bounds.Width < 1 || text.Bounds.Height < 1)
            {
                yield return $"{where}: '{text.Text}' has no room at all.";
                continue;
            }

            var whole = new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                FontFamily = text.FontFamily,
                FontWeight = text.FontWeight,
                FontStyle = text.FontStyle,
                LetterSpacing = text.LetterSpacing,
                LineHeight = text.LineHeight,
                Padding = text.Padding,
            };
            whole.Measure(Size.Infinity);

            // A text that wraps is whole when its box is as tall as its lines are at the box's own width; one kept shorter -
            // or so narrow that each line holds a letter or two - loses its last lines.
            var wrapped = new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                FontFamily = text.FontFamily,
                FontWeight = text.FontWeight,
                FontStyle = text.FontStyle,
                LetterSpacing = text.LetterSpacing,
                LineHeight = text.LineHeight,
                Padding = text.Padding,
                TextWrapping = text.TextWrapping,
            };
            wrapped.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            bool wider = text.TextWrapping == TextWrapping.NoWrap && whole.DesiredSize.Width > text.Bounds.Width + 1;
            bool shorter = text.TextWrapping != TextWrapping.NoWrap && wrapped.DesiredSize.Height > text.Bounds.Height + 1;
            bool completed = text.GetSelfAndVisualAncestors().OfType<Control>().Any(control => ToolTip.GetTip(control) is not null);
            if ((wider || shorter) && text.TextTrimming == TextTrimming.None)
            {
                yield return $"{where}: '{text.Text}' is cut off by its own box.";
            }
            else if ((wider || shorter) && !completed)
            {
                yield return $"{where}: '{text.Text}' ends in an ellipsis that no tooltip completes.";
            }
            else if (!wider && !shorter && ClippedBy(text, window) is { } clip)
            {
                yield return $"{where}: '{text.Text}' is cut off by {clip}.";
            }
        }
    }

    /// <summary>
    /// The ancestor that clips the text at any edge, in part or whole, when one does: a card, a panel, or the window itself,
    /// whose bottom edge a fixed-height prompt's last paragraph and buttons can run past. A view that scrolls holds a text
    /// past its edges in that direction a scroll away - the crumb trail its first crumbs, a list its rows - but one it cuts
    /// across its far edge sideways is still cut off where it stands.
    /// </summary>
    private static string? ClippedBy(TextBlock text, Window window)
    {
        if (text.TranslatePoint(new Point(0, 0), window) is not { } origin)
        {
            return null;
        }

        double right = origin.X + text.Bounds.Width;
        double bottom = origin.Y + text.Bounds.Height;
        bool sideways = true;
        bool upright = true;
        foreach (Control ancestor in text.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.GetType().Name == "ScrollContentPresenter" || ancestor.TranslatePoint(new Point(0, 0), window) is not { } corner)
            {
                continue;
            }

            double edge = corner.X + ancestor.Bounds.Width;
            double foot = corner.Y + ancestor.Bounds.Height;
            string name = $"{ancestor.GetType().Name} {ancestor.Name}".TrimEnd();
            if (ancestor is ScrollViewer viewer)
            {
                if (sideways && viewer.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                {
                    if (right > edge + 1 && origin.X < edge)
                    {
                        return name;
                    }

                    sideways = false;
                }

                upright &= viewer.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            }

            if (ancestor.ClipToBounds && ((sideways && (right > edge + 1 || origin.X < corner.X - 1))
                || (upright && (bottom > foot + 1 || origin.Y < corner.Y - 1))))
            {
                return name;
            }
        }

        return null;
    }
}
