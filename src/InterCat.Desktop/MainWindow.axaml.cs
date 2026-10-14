using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.CaptureBroker;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Settings;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

public sealed partial class MainWindow : Window, IDisposable, IChoosingWindow
{
    private WorkspaceViewModel workspace;
    private CancellationTokenSource? captureStop;
    private Task? captureTask;
    private int captureRunId;
    private Guid? displayedSessionId;
    private long displayedGeneration = -1;
    private string? currentSessionPath;
    private bool openingSession;
    private bool openingRecord;
    private bool exporting;

    /// <summary>The package being made, while one is; its cancellation is its share button's second meaning.</summary>
    private CancellationTokenSource? packaging;

    /// <summary>Whether the package being made is the original, unredacted copy rather than a redacted session.</summary>
    private bool packagingOriginal;

    /// <summary>
    /// The package's work off the window's thread while one is made, and what the question asked before a close says of
    /// it: a close that stops the package waits the work out, so its unsaved folder is removed before the window goes.
    /// </summary>
    private (Task Work, CloseStop Stop)? packageWork;

    // A package was asked for and waits for the shown session's files to be checked, so a second ask is the same one.
    private bool awaitingFilesCheck;

    /// <summary>A session folder picker is open, so a second one is not started behind it.</summary>
    private bool choosingSession;
    private bool closed;

    /// <summary>A support bundle is being saved, so a second click does not start another.</summary>
    private bool savingSupport;

    /// <summary>Where the window asks for a file or a folder: the platform's pickers, or a test's answers.</summary>
    internal PathChoices Choices { get; set; }

    /// <summary>The demo is being made or opened, so a second click does not start another.</summary>
    private bool exploringDemo;

    /// <summary>Where the demo is kept and made (§14 M5); InterCat's own folder beside its sessions unless a test says.</summary>
    internal string? DemoRoot { get; set; }

    private readonly List<InvestigationWindow> investigations = [];

    /// <summary>
    /// The investigations shown beside this window, one window each. They are its peers, not windows it owns, so either
    /// can be in front: a session opened from one is shown here, and this window comes in front to show it.
    /// </summary>
    internal IReadOnlyList<InvestigationWindow> Investigations => investigations;

    // The investigation a session is being opened from, while it is; then the one that keeps the shown session's pins
    // (§26.3), with the pins it last kept and the writes of them in order. A session opened on its own keeps none there.
    private string? openingFromInvestigation;
    private (string Workspace, Guid Session)? layoutHome;
    private IReadOnlyDictionary<string, GraphPoint>? keptPins;

    /// <summary>The process lanes last kept pinned in the shown session's investigation, in the order pinned (§6.2).</summary>
    private IReadOnlyList<ProcessInstanceId>? keptLanes;

    /// <summary>The view settings last kept in the shown session's investigation, or null when none were kept or read yet.</summary>
    private ViewSettings? keptSettings;

    // The main pane a person let fill the column, if any, the split the two shared before, and whether the window is laying
    // them out - setting the toggles to show it, or putting back what an investigation kept - rather than a person.
    private MainPane? expandedPane;
    private (GridLength Graph, GridLength Timeline)? sharedSplit;
    private bool layingPanes;

    // Whether the splitter between the panes, or a column's edge, is being dragged, which is kept once it is let go, and
    // whether a split moved otherwise is waiting to be kept once every row or column it moved has its size.
    private bool draggingSplit;
    private bool splitToKeep;

    // Whether the window is fitting a column's width to itself - the rail following its width, or a narrowed window taking
    // room back for the main panes - which is no person's choice and is not kept.
    private bool fittingColumns;

    /// <summary>
    /// The panes as the shown session's investigation last kept them, or put them back - equal halves with both shown and
    /// columns as a window lays them out when it keeps none - or null when that is not known, so the next change is written
    /// whatever it is.
    /// </summary>
    private (double GraphShare, WorkspacePane? Expanded, double? RailWidth, double? InspectorWidth)? keptPanes;

    // What the card says the shown session's investigation could not keep - its layout and view settings, and the
    // window's panes - while it says so: said once, however often a change fails to be written, and taken back once one
    // is written.
    private string? layoutUnkept;
    private string? panesUnkept;

    /// <summary>
    /// The least height a split leaves each main pane (§6.1's collapse floor): the graph its header and a legible plot, the
    /// timeline its header, a lane with its axis, and the minimap. Only letting the other fill the column hides one.
    /// </summary>
    private const double GraphPaneFloor = 160;

    /// <summary>The timeline's least height, by <see cref="GraphPaneFloor"/>'s rule.</summary>
    private const double TimelinePaneFloor = 180;

    // Every write to the shown session's investigation - its pins and view settings, and the window's panes - one after
    // another.
    private Task investigationWritten = Task.CompletedTask;

    /// <summary>
    /// What a member keeps in its investigation beside its pins (§26.3): what its rows are ranked by and whether per
    /// second, the evidence policy its records are counted under, whether each timeline lane has its own scale, how its
    /// processes are grouped, whether InterCat's own processes are set aside (§19.5), and whether its times are read on the
    /// wall clock (§6.2).
    /// </summary>
    private readonly record struct ViewSettings(RankingMetric RankBy, bool PerSecond, EvidencePolicy Policy, bool ScalesEachLane,
        LaneGrouping Grouping, bool CollectorsAside = false, bool WallClock = false)
    {
        /// <summary>What a session opened on its own starts with, and an investigation that keeps nothing of it puts back.</summary>
        public static ViewSettings Default =>
            new(RankingMetric.Records, false, EvidencePolicy.IncludeCorrelated, false, LaneGrouping.Executable);

        /// <summary>
        /// What the shown workspace is set to now, grouped as its person chose (<paramref name="grouping"/>), and with
        /// InterCat's own processes set aside as they chose (<paramref name="collectorsAside"/>).
        /// </summary>
        public static ViewSettings Of(WorkspaceViewModel workspace, LaneGrouping grouping, bool collectorsAside) =>
            new(workspace.RankBy, workspace.PerSecond, workspace.EvidencePolicy, workspace.ScalesEachLane, grouping, collectorsAside,
                workspace.ReadsWallClock);
    }

    // Whether the ranked table owns the keyboard as far as the user is concerned (FollowKeyboardOwner).
    private bool railOwnsKeyboard;

    // The control that last had the keyboard in each pane F6 visits, which it gives the keyboard back to (FocusPane).
    private readonly Dictionary<Control, InputElement> paneKeyboard = [];

    // The control of the window that last had the keyboard, which gets it back once it can take it again (KeepKeyboard).
    private InputElement? lastKeyboard;
    private bool closingPrompt;

    /// <summary>
    /// The newest published generation of the displayed session, kept aside while the user inspects evidence so rows
    /// and selection stay still. It is applied on F5 or when the user leaves the evidence rung.
    /// </summary>
    private CaptureUiUpdate? heldUpdate;

    /// <summary>
    /// Whether the view follows each new publication of a live capture. Pausing the view never stops recording (§6.2);
    /// a paused view keeps its generation and offers the newest one, as the evidence rung does.
    /// </summary>
    private bool followLatest = true;

    private CaptureUiPhase phase = CaptureUiPhase.Complete;
    private SessionOverviewBundle? displayedOverview;

    /// <summary>
    /// The evidence policy the window projects and reads a session under (§6.8): correlated evidence until a person
    /// chooses to count a reused PID's candidates too, and again for each session opened or captured after. A capture's
    /// thread reads it as each live publication is projected.
    /// </summary>
    private volatile EvidencePolicy evidencePolicy = EvidencePolicy.IncludeCorrelated;

    /// <summary>
    /// How the window groups a session's processes (§6.3): by executable, as projected, until a person chooses their terminal
    /// session, and again for each session opened or captured after, unless the investigation it is opened from keeps
    /// another grouping for it. Every publication of the session shown is grouped so where its processes offer it.
    /// </summary>
    private LaneGrouping laneGrouping = LaneGrouping.Executable;

    // Whether InterCat's own processes are set aside from the shown session's rows, graph and channels (§19.5's view
    // filter): while the session is open, and for each of its publications.
    private bool collectorsAside;
    private DateTimeOffset? lastPublicationUtc;
    private BrokerCaptureHealth? liveHealth;

    // §12.1 S5: the session's size as its newest generation measured it, with the wall-clock moment its capture began,
    // what it retains after a release and what its pins keep, and the limits and volume of a capture that records, from
    // which the window says when it stops.
    private (SessionSize Size, DateTimeOffset? Began, RetainedInterval? Retained, string? Pinned, IReadOnlyList<RetentionPin> Pins)? growth;
    private CaptureLimits? captureLimits;
    private RecordingVolume? recordingVolume;

    // The broker's latest live preview, and how many published chunks the displayed generation was derived from: the
    // preview draws the chunks after those (§12, §19.3).
    private BrokerCapturePreview? livePreview;

    /// <summary>How often the capture shown publishes, as its milestones say; null before they say or outside a capture.</summary>
    private TimeSpan? publishedEvery;
    private int displayedChunks;

    // A capture an earlier viewer left unfinished, offered in the rail (§3.1 step 6): where to look for one, the one
    // offered, what its card says, and the finish running for it, whose cancellation is the finish button's second meaning.
    private string? sessionRoot;
    private IReadOnlyList<RecentSessionRow> recentSessions = [];
    private bool activatedBefore;
    private InterruptedFollow? offeredFollow;
    private InterruptedCaptureOffer? offer;
    private CancellationTokenSource? finishingCapture;

    /// <summary>The finish's work off the window's thread while one runs, which a close that stops it waits.</summary>
    private (Task Work, CloseStop Stop)? finishWork;
    private readonly DispatcherTimer offerRecheck = new() { Interval = TimeSpan.FromSeconds(5) };

    // Which look at the session folder is the latest. The recheck's timer and the user's own actions each start one, and
    // only the latest one's answer is shown: an older one may have read a ticket the user has since forgotten.
    private int offerRefresh;

    // The application's settings (§26.3): where they are kept, when a file is in use, and what reading it found.
    private ApplicationSettingsStore? settingsStore;

    // Where the window stood when it last closed, as the settings file holds it, and where it stands while neither
    // maximized nor minimized, which it keeps as it closes (§26.3).
    private WindowPlacement? keptPlacement;
    private PixelPoint normalPosition;
    private Size normalSize;
    private ApplicationSettings settings = ApplicationSettings.Default;
    private string? settingsProblem;
    private string? appliedDetail;
    private readonly DispatcherTimer healthClock = new() { Interval = TimeSpan.FromSeconds(1) };

    // The rail's width as the window last set it, until the user resizes the rail by its edge (null once they have).
    private double? followedRailWidth = RailDesignWidth;
    private const double RailDesignWidth = 250;
    private const double RailWidestFollowed = 400;

    public MainWindow() : this(new WorkspaceViewModel(OverviewWorkspace.Empty(), "empty-workspace"))
    {
    }

    public MainWindow(WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        Choices = new PathChoices(this);
        FolderOpener = folder => Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));

        // The graph's and the timeline's hover cards are drawn by one layer above every pane (section 6.2).
        HoverLayer.Track(GraphSurface);
        HoverLayer.Track(TimelineSurface);

        // Window shortcuts are handled while the key tunnels down, because a focused list would otherwise
        // consume a letter key for type-ahead and the keyboard path would silently stop working (R15).
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, RequestContextMenuOnShiftF10, RoutingStrategies.Bubble);

        // A saved session's folder or an investigation file dragged onto the window opens where it is let go, as Open
        // saved session and the Investigation menu open them (§3.1).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OfferDrop);
        AddHandler(DragDrop.DragOverEvent, OfferDrop);
        AddHandler(DragDrop.DropEvent, OpenDropped);

        // §6.7: Ctrl+click on a ranked row adds it to the multi-selection or removes it, before the list would select it alone.
        RungList.AddHandler(PointerPressedEvent, OnRungListPointerPressed, RoutingStrategies.Tunnel);
        RungList.AddHandler(ContextRequestedEvent, OpenRowMenu, RoutingStrategies.Bubble);

        // The current rung is the last crumb. When a descent or a long channel name widens the trail, it scrolls so
        // that crumb stays in view instead of being clipped behind the level badge (section 3.2, position stated).
        CrumbScroller.ScrollChanged += (_, change) =>
        {
            if (change.ExtentDelta.X != 0 || change.ViewportDelta.X != 0)
            {
                FitCurrentCrumb();

                // ScrollToEnd means bottom-left in Avalonia; the trail's end is its right edge.
                CrumbScroller.Offset = new(Math.Max(0, CrumbScroller.Extent.Width - CrumbScroller.Viewport.Width), 0);
            }

            HideCutCrumbs();
        };
        // The minimap shows and moves the timeline's viewport; the timeline owns it (presentation only, §6.4).
        MinimapSurface.Timeline = TimelineSurface;

        // Each pane keeps its least height while both are shown, and the split a person moves is kept in the investigation
        // the shown session was opened from (§6.1, §26.3): a drag once it is let go, a key on the splitter as it moves it.
        LayPanes(null, null);
        PaneSplitter.AddHandler(Thumb.DragStartedEvent, (_, _) => draggingSplit = true, RoutingStrategies.Bubble, handledEventsToo: true);
        PaneSplitter.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            draggingSplit = false;
            KeepSplit();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        PanesGrid.RowDefinitions[0].PropertyChanged += PaneRowChanged;
        PanesGrid.RowDefinitions[2].PropertyChanged += PaneRowChanged;

        // The widths a person gives the rail and the inspector by their edges are kept there too, as the split is.
        foreach (GridSplitter edge in new[] { RailSplitter, InspectorSplitter })
        {
            edge.AddHandler(Thumb.DragStartedEvent, (_, _) => draggingSplit = true, RoutingStrategies.Bubble, handledEventsToo: true);
            edge.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
            {
                draggingSplit = false;
                KeepSplit();
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        WindowGrid.ColumnDefinitions[0].PropertyChanged += SideColumnChanged;
        WindowGrid.ColumnDefinitions[2].PropertyChanged += SideColumnChanged;
        workspace = viewModel;
        DataContext = workspace;
        workspace.PropertyChanged += OnWorkspaceChanged;

        // Every list and combo box names its items by their rows' accessible sentences, not their record fields (R15).
        foreach (ItemsControl items in this.GetLogicalDescendants().OfType<ItemsControl>())
        {
            Presentation.AccessibleItems.Name(items);
        }

        // The Keep choice's tooltip says what the chosen capture keeps from the first, so a trimmed label is always completed.
        CaptureKeepChosen(CaptureKeepSelector, null);

        // A ranked row in §6.7's multi-selection is marked where it is drawn, and says so to a screen reader; the rows are
        // not rebuilt, so the keyboard focus a Ctrl+Space set out from stays where it was. So is a ranked row, or a row of
        // the relationship table, standing for what the graph highlights for a chosen cell (§6.4, R15).
        RungList.ContainerPrepared += (_, prepared) => MarkSelectionShare(prepared.Container);
        RelationshipList.ContainerPrepared += (_, prepared) => MarkCellRelationship(prepared.Container);
        AddHandler(GotFocusEvent, FollowKeyboardOwner, RoutingStrategies.Bubble, handledEventsToo: true);

        Opened += (_, _) => StartExploringButton.Focus();
        // Back from a window or a dialog that took the keyboard from a control since hidden or disabled, it is not left on
        // nothing here; nor when a control goes from under it, hidden or rebuilt away. One that only waits disabled while
        // it works gets it back as its work ends. The session folder is read again then, for what changed in it since:
        // the first activation is the window's opening, whose folder is read as it takes its session root.
        Activated += (_, _) =>
        {
            KeepKeyboard();
            if (activatedBefore) FolderRead = ReadFolderAgainAsync();
            activatedBefore = true;
        };
        AddHandler(LostFocusEvent, (_, lost) =>
        {
            if (lost.Source is Visual source && (!source.IsEffectivelyVisible || !this.IsVisualAncestorOf(source)))
            {
                KeepKeyboard();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // A detail said aloud is shown from its first line, where a person reading a long one had scrolled its lines; a
        // recording's newer generation, restated in silence, leaves them where they are read (R15).
        CaptureDetail.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty
                && AutomationProperties.GetLiveSetting(CaptureDetail) != AutomationLiveSetting.Off)
            {
                CaptureDetailScroller.Offset = default;
            }
        };

        SizeChanged += (_, change) =>
        {
            FollowRailWidth(change.NewSize.Width);
            KeepMainPanesWidth(change.NewSize.Width);
            KeepNormalBounds();
        };
        PositionChanged += (_, _) => KeepNormalBounds();
        RailGrid.SizeChanged += (_, _) => FitRailActions();
        RankedTableHeader.SizeChanged += (_, _) => FitRailActions();
        UpdateThemeMenu();

        // The operating system's light or dark setting (§26.2) reaches the resources on its own; the canvases and the
        // legend's hues are redrawn from the new mode's tokens here.
        ThemeResources.ModeChanged += OnThemeModeChanged;
        healthClock.Tick += (_, _) => UpdateHealthStrip();
        healthClock.Start();
        offerRecheck.Tick += (_, _) => _ = RefreshInterruptedOfferAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true;
            KeepPlacement();

            // An investigation opens its sessions here, so none outlives the window that shows them.
            foreach (InvestigationWindow shown in investigations.ToArray())
            {
                shown.Close();
            }

            Dispose();
        };
    }

    /// <summary>
    /// Keyboard equivalents for the header actions. Every affordance on this window has one, so no
    /// behaviour is reachable by pointer alone (R15, section 6.7).
    /// </summary>
    /// <summary>The widest a crumb is drawn, as the crumb template states; a wider name ends in an ellipsis.</summary>
    private const double CrumbWidth = 220;

    /// <summary>The narrowest the current rung's crumb is drawn, however little room the header leaves the trail.</summary>
    private const double MinimumCrumbWidth = 48;

    /// <summary>
    /// Narrows the current rung's crumb to the trail when it is wider, so it ends in an ellipsis rather than losing its
    /// start when the trail scrolls to it: at the minimum width a channel's crumb read "nel: RPC calls to svcctl…"
    /// (section 3.2). Every other crumb keeps its own width, and the current one regains its own when there is room.
    /// </summary>
    private void FitCurrentCrumb()
    {
        int last = CrumbList.ItemCount - 1;
        for (int index = 0; index <= last; index++)
        {
            if (CrumbList.ContainerFromIndex(index) is not { } crumb
                || crumb.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is not { } text)
            {
                continue;
            }

            // What the crumb draws around its text - the item's padding - stays; the text gives up the rest.
            double around = Math.Max(0, crumb.Bounds.Width - text.Bounds.Width);
            text.MaxWidth = index == last && CrumbScroller.Viewport.Width > 0
                ? Math.Clamp(CrumbScroller.Viewport.Width - around, MinimumCrumbWidth, CrumbWidth)
                : CrumbWidth;
        }
    }

    /// <summary>
    /// Clears a crumb the trail's left edge cuts, so the sliver left of an earlier crumb does not read as a stray character
    /// beside the current position (section 3.2). The crumb keeps its place, its hit target and its accessible name; only
    /// what would be drawn of it goes. The current rung's crumb is never cleared.
    /// </summary>
    private void HideCutCrumbs()
    {
        double offset = CrumbScroller.Offset.X;
        int count = CrumbList.ItemCount;
        for (int index = 0; index < count; index++)
        {
            if (CrumbList.ContainerFromIndex(index) is not { } crumb)
            {
                continue;
            }

            bool cut = index < count - 1
                && crumb.TranslatePoint(default, CrumbList) is { } left
                && left.X < offset - 0.5;
            crumb.Opacity = cut ? 0 : 1;
        }
    }

    /// <summary>Whether the keyboard is on the first of the search's hits, above which is the search box.</summary>
    private bool OnFirstSearchHit() =>
        (FocusManager?.GetFocusedElement() is Control focused ? SearchResultsList.IndexFromContainer(focused)
            : SearchResultsList.SelectedIndex) <= 0;

    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        // F1 types nothing, so it lists the window's keys from wherever the keyboard is, a search box included (§6.7).
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = ShowKeysAsync();
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // F11 edits no text, so it gives a filled column back from wherever the keyboard is, a search box included (§6.1).
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None && ToggleFocusedPane())
        {
            e.Handled = true;
            return;
        }

        // Nor does F6, which moves the keyboard to the next pane from wherever it is, and Shift+F6 to the one before, as
        // Windows applications move it between their panes (§6.7).
        if (e.Key == Key.F6 && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            e.Handled = MovePaneKeyboard(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        // Nor do the other keys the sheet says act anywhere - Ctrl+R, Ctrl+E, Ctrl+O, F5, Alt+Left and Alt+Right - so
        // they act from wherever the keyboard is too, a search box included (§6.7).
        if (AnywhereKey(viewModel, e) is { } taken)
        {
            e.Handled = taken;
            return;
        }

        if (SearchBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape)
            {
                // Escape clears the search and leaves the box, even an empty one, for the table the user came from.
                viewModel.SearchText = string.Empty;
                FocusRail();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = OpenSelectedSearchHit();
            }
            else if (e.Key == Key.Down && viewModel.SearchResults.Count > 0)
            {
                SearchResultsList.ContainerFromItem(viewModel.SelectedSearchResult!)?.Focus();
                e.Handled = true;
            }

            return;
        }

        if (SearchResultsList.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter) e.Handled = OpenSelectedSearchHit();
            else if (e.Key == Key.Escape)
            {
                viewModel.SearchText = string.Empty;
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && OnFirstSearchHit())
            {
                // Up from the first hit gives the search box the keyboard back, its words kept to go on with, as Down
                // from the box gave the hits it (§6.7).
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
            }

            if (e.Handled || e.Key is Key.Enter or Key.Escape) return;
        }

        // In the recent sessions, Enter opens the selected one; it is not a descent in a session nobody opened yet.
        if (RecentSessionsList.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = OpenSelectedRecentSessionAsync();
            return;
        }

        if (e.Source is TextBox) return;

        if (HandleRungListSelectionKey(viewModel, e))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.T:
                viewModel.ShowTables = !viewModel.ShowTables;
                e.Handled = true;
                break;
            case Key.Enter when e.Source is Button or ComboBox:
                // A button or a list of choices the keyboard is on takes its own Enter, pressed or opened, as in every
                // dialog: a row chosen in the ranked table is not descended into from it (R15).
                break;
            case Key.Enter:
                if (CellRecordsList.IsKeyboardFocusWithin)
                {
                    // Enter on a chosen cell's record opens it in E, selected there (§6.4), rather than descending.
                    e.Handled = OpenSelectedCellRecord(viewModel);
                    break;
                }

                if (RelationshipList.IsKeyboardFocusWithin)
                {
                    // Enter on a relationship opens it as a double click on its edge does (R15): the row with the keyboard
                    // is the one opened when none is chosen yet, as on the ranked table.
                    if (viewModel.SelectedRelationship is null
                        && FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: RelationshipRow focused })
                    {
                        viewModel.SelectedRelationship = focused;
                    }

                    e.Handled = OpenSelectedRelationship();
                    break;
                }

                if (IntervalList.IsKeyboardFocusWithin)
                {
                    // An interval is a moment of the timeline, not a row to descend into: Enter lists its records, as E
                    // does with the analysis interval it sets (§6.4), and E's table then has the keyboard. It descended
                    // into the ranked table's chosen row, which the interval table does not show.
                    e.Handled = viewModel.ShowEvidence();
                    if (e.Handled) FocusRail();
                    break;
                }

                if (CrumbWithKeyboard() is { IsCurrent: false } crumb)
                {
                    // A crumb with the keyboard says "press Enter to return to this level"; the rung's table then has it.
                    viewModel.SelectedCrumb = crumb;
                    FocusRail();
                    e.Handled = true;
                    break;
                }

                ChooseRowWithKeyboard(viewModel);
                if (viewModel.HasSelectedEvidence)
                {
                    OpenOriginalRecord();
                    e.Handled = true;
                }
                else
                {
                    e.Handled = viewModel.Descend();
                }
                break;
            case Key.E when e.KeyModifiers == KeyModifiers.None:
            {
                // A list E rebuilds or hides - a table's rows, a chosen cell's records - would leave the keyboard on nothing,
                // so E's table takes it there, as after Enter; on a surface or a control that stays, it stays.
                bool rebuilt = RelationshipList.IsKeyboardFocusWithin || IntervalList.IsKeyboardFocusWithin
                    || CellRecordsList.IsKeyboardFocusWithin;
                e.Handled = viewModel.ShowEvidence();
                if (e.Handled && rebuilt) FocusRail();
                break;
            }
            case Key.C when e.KeyModifiers == KeyModifiers.None && viewModel.HasSelectedContent:
                OpenContent();
                e.Handled = true;
                break;
            case Key.O when e.KeyModifiers == KeyModifiers.None && viewModel.SelectedRung is { HasOtherEnd: true }:
                // An RPC call linked to another opens the call at its other end (operations-v1 §5c).
                e.Handled = viewModel.OpenOtherEnd();
                if (e.Handled) FocusRail();
                break;
            case Key.M when viewModel.CanLoadMore:
                _ = viewModel.LoadMoreAsync();
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                e.Handled = ToggleFollowLatest();
                break;
            case Key.P when e.KeyModifiers == KeyModifiers.None:
                // With the keyboard in the timeline, P pins the selected process's lane at the top of its group's lanes, or
                // unpins it (§6.2); anywhere else, or with no such lane, it pins the selected graph node.
                e.Handled = (TimelineSurface.IsKeyboardFocusWithin && viewModel.ToggleSelectedLanePin())
                    || viewModel.TogglePinSelectedGraphNode();
                break;
            case Key.L when e.KeyModifiers == KeyModifiers.None:
                e.Handled = viewModel.RelayoutGraph();
                break;
            case Key.Escape:
                // A drag in progress is cancelled where it began (§6.7); with none, Esc ascends as the ladder's back does.
                if (!(TimelineSurface.CancelDrag() || MinimapSurface.CancelDrag() || GraphSurface.CancelDrag()))
                {
                    _ = viewModel.Ascend();
                }

                e.Handled = true;
                break;
            case Key.Delete:
                e.Handled = viewModel.RemoveSelectedFilter();
                break;
            default:
                break;
        }

    }

    /// <summary>The keys sheet while it is open; a test reads it.</summary>
    internal KeysWindow? KeysSheet { get; private set; }

    private void ShowKeys(object? sender, RoutedEventArgs eventArgs) => _ = ShowKeysAsync();

    /// <summary>
    /// Lists every key the window takes, by where it acts (§6.7, R15), as F1 and the strip's Keys button do. The sheet is
    /// the window's own dialog, so once it closes the window takes the keyboard back to where it was, a row it was on not
    /// chosen by coming back to it.
    /// </summary>
    internal async Task ShowKeysAsync()
    {
        if (KeysSheet is not null || closed)
        {
            return;
        }

        KeysSheet = new KeysWindow();
        try
        {
            await KeysSheet.ShowDialog(this);
        }
        finally
        {
            KeysSheet = null;
        }
    }

    /// <summary>
    /// Gives the keyboard to the pane after the one holding it, or before it, among the panes F6 visits in its order
    /// (§6.7): the ranked table, the graph, the timeline and the inspector, or in place of the last three the relationship
    /// and interval tables while they are shown over them. A pane with nothing to take it is passed by, as is one out of
    /// sight, such as one the other fills the column over. From anywhere else - a header's button, the status bar - the
    /// next pane is the first and the one before the last.
    /// </summary>
    private bool MovePaneKeyboard(bool forward)
    {
        Control[] panes = workspace.ShowTables
            ? [Rail, RelationshipList, IntervalList]
            : [Rail, GraphPane, TimelinePane, Inspector];
        int holding = Array.FindIndex(panes, pane => pane.IsKeyboardFocusWithin);
        int others = holding < 0 ? panes.Length : panes.Length - 1;
        for (int step = 1; step <= others; step++)
        {
            int index = holding < 0
                ? (forward ? step - 1 : panes.Length - step)
                : (((holding + (forward ? step : -step)) % panes.Length) + panes.Length) % panes.Length;
            if (FocusPane(panes[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gives <paramref name="pane"/> the keyboard where a person works it: the ranked table's selected row or its first, or
    /// its rung's evidence step or the reason it has none when it has no rows; the graph's or the timeline's own surface; a
    /// table's selected row or its first; or else the control in the pane that last had the keyboard while it still can,
    /// or the pane's first list with rows or first control a Tab would reach, as the rail's recent sessions before any is
    /// open. False when it has none.
    /// </summary>
    private bool FocusPane(Control pane)
    {
        if (ReferenceEquals(pane, Rail) && (RungList is { IsEffectivelyVisible: true, ItemCount: > 0 }
            || EmptyReasonCard.IsEffectivelyVisible))
        {
            // The ranked table's own path, which waits for rows still being laid out.
            FocusRail();
            return true;
        }

        if (ReferenceEquals(pane, GraphPane)) return GraphSurface.Focus(NavigationMethod.Tab);
        if (ReferenceEquals(pane, TimelinePane)) return TimelineSurface.Focus(NavigationMethod.Tab);
        if (pane is ListBox table) return FocusRow(table);
        if (paneKeyboard.TryGetValue(pane, out InputElement? last) && pane.IsVisualAncestorOf(last) && last.IsEffectivelyVisible
            && last.IsEffectivelyEnabled && last.Focus(NavigationMethod.Tab))
        {
            return true;
        }

        InputElement? first = pane.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(element =>
            element.IsEffectivelyVisible
            && (element is ListBox { ItemCount: > 0 }
                || (element.Focusable && element.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(element))));
        return first is ListBox list ? FocusRow(list) : first?.Focus(NavigationMethod.Tab) == true;
    }

    /// <summary>
    /// Gives a list's <paramref name="row"/> the keyboard, or else its selected row, or its first when none is
    /// selected: a list takes no focus of its own, so a row must (see <see cref="FocusRail"/>). False for a list with
    /// no rows, or one out of sight.
    /// </summary>
    private static bool FocusRow(ListBox list, object? row = null)
    {
        if (list.ItemCount == 0 || !list.IsEffectivelyVisible
            || (row ?? list.SelectedItem ?? list.Items[0]) is not { } chosen)
        {
            return false;
        }

        list.ScrollIntoView(chosen);
        return list.ContainerFromItem(chosen) is Control container && container.Focus(NavigationMethod.Tab);
    }

    /// <summary>
    /// Gives the ranked table the keyboard, on its selected row or its first, once its rows are laid out. A list takes no
    /// focus of its own, so focusing the list itself left the keyboard where it was, or on nothing once the control that
    /// had it was hidden: the search box after Escape, a search hit after Enter.
    /// </summary>
    private void FocusRail()
    {
        railOwnsKeyboard = true;
        KeepRailKeyboard();
    }

    /// <summary>
    /// Gives the keyboard back to the ranked table's selected row, or its first, once rebuilt rows are laid out. A rung
    /// change rebuilds the table, and so do rows arriving for the same rung - a process's RPC channels, the next page of
    /// records - and the row that had the keyboard goes with them; while the table owns the keyboard, the new rows get
    /// it back, so the next arrow and Enter act on the rung the user is on (§3.2, R15).
    /// </summary>
    private void KeepRailKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || !railOwnsKeyboard) return;
        IInputElement? focused = FocusManager?.GetFocusedElement();
        if (focused is Visual current && RungList.IsVisualAncestorOf(current)) return;
        if (RungList.ItemCount == 0 || !RungList.IsEffectivelyVisible)
        {
            // A rung with no rows offers its evidence step beside the reason it has none. The step has the keyboard, so
            // Enter lists the rung's records, and a screen reader reads the reason beside it. A rung offering none, as E
            // with no record in its scope, gives it the reason itself rather than leave it on nothing.
            InputElement? step = EmptyEvidenceButton.IsEffectivelyVisible ? EmptyEvidenceButton
                : EmptyReasonCard.IsEffectivelyVisible ? EmptyReasonCard : null;
            if (step is not null && !ReferenceEquals(focused, step))
            {
                step.Focus(NavigationMethod.Directional);
            }

            return;
        }

        object? row = RungList.SelectedItem ?? RungList.Items[0];
        if (row is null) return;
        RungList.ScrollIntoView(row);
        if (RungList.ContainerFromItem(row) is Control container)
        {
            container.Focus(NavigationMethod.Directional);
        }
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Gives the keyboard, when nothing has it, to what the window offers now: back to the ranked table when it had it
    /// last; else Stop while a capture records with no view yet; else the control that last had it, where it can take it
    /// again, a row by its list's chosen row; else the ranked table once a session is shown; else Start exploring.
    /// Start exploring hides once a capture starts, Stop waits disabled once pressed and hides once the session is saved,
    /// the buttons that open a session wait disabled while it opens, an unfinished capture's card waits disabled while its
    /// finish stops and goes once it is forgotten, and Forward, Restore defaults and the newer generation's banner go once
    /// pressed, so each left the keyboard on nothing, and Enter and the arrows did nothing until Tab or F6 found a control.
    /// Stop gets it only before a view is shown, so a view rebuilt under the keyboard never leaves it where Enter stops the
    /// capture. Never while another window or a dialog is the one in use, and never from a control that has the keyboard.
    /// Posted, once what changed is laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || !IsActive || FocusManager?.GetFocusedElement() is { } focused && !ReferenceEquals(focused, this)) return;

        // Keep records waits disabled while the pin its dialog chose is written, and gets the keyboard back as that ends: the
        // window, active again as the dialog closed, handed it to the ranked table meanwhile.
        if (pinning && ReferenceEquals(lastKeyboard, KeepRecordsButton)) return;
        if (railOwnsKeyboard && !workspace.IsEmptyWorkspace)
        {
            KeepRailKeyboard();
        }
        else if (workspace.IsEmptyWorkspace && StopCaptureButton is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
        {
            StopCaptureButton.Focus(NavigationMethod.Tab);
        }
        else if (GiveBackLastKeyboard())
        {
            // A session that did not open, the demo's investigation closed, or a finish stopped gives back the control that
            // asked for it.
        }
        else if (!workspace.IsEmptyWorkspace)
        {
            FocusRail();
        }
        else if (StartExploringButton is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
        {
            StartExploringButton.Focus(NavigationMethod.Tab);
        }
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Gives the control that last had the keyboard it back, where it can take it again. A row of a list gives it to its
    /// list's chosen row, or its first, rather than to its own container: a list whose rows are restated - the interval
    /// table counting a lane chosen, the inspector's records as a newer generation arrives - hides each row's container and
    /// gives it to whichever row it lays out next, so the keyboard went to a row other than the chosen one.
    /// </summary>
    private bool GiveBackLastKeyboard()
    {
        if (lastKeyboard is ListBoxItem row && row.FindAncestorOfType<ListBox>() is { } list)
        {
            return this.IsVisualAncestorOf(list) && FocusRow(list);
        }

        return lastKeyboard is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } last
            && this.IsVisualAncestorOf(last) && last.Focus(NavigationMethod.Tab);
    }

    /// <summary>
    /// Follows who owns the keyboard as far as the user is concerned: the ranked table does once focus moves into it, and
    /// stops only when focus moves to another control. A row rebuilt away under the focus, which leaves focus nowhere or on
    /// the window itself, does not take the keyboard from the table.
    /// </summary>
    private void FollowKeyboardOwner(object? sender, GotFocusEventArgs focus)
    {
        if (focus.Source is not Visual target || ReferenceEquals(target, this)) return;
        lastKeyboard = target as InputElement;
        railOwnsKeyboard = ReferenceEquals(target, RungList) || RungList.IsVisualAncestorOf(target)
            || ReferenceEquals(target, EmptyEvidenceButton) || ReferenceEquals(target, EmptyReasonCard);

        // F6 gives a pane the keyboard back where it last was, as a person left it there.
        Control[] remembering = [Rail, Inspector, RelationshipList, IntervalList];
        if (target is InputElement element && remembering.FirstOrDefault(pane => pane.IsVisualAncestorOf(target)) is { } holding)
        {
            paneKeyboard[holding] = element;
        }
    }

    /// <summary>
    /// Makes the ranked row that has the keyboard the selected one when no row is, before Enter acts on the selection. A
    /// rung's table takes the keyboard on its first row with nothing selected, and each row says "Press Enter", yet Enter
    /// did nothing there until an arrow had selected a row. A selection or multi-selection the user made stays as it is.
    /// </summary>
    private void ChooseRowWithKeyboard(WorkspaceViewModel viewModel)
    {
        if (viewModel.SelectedRung is not null || viewModel.HasMultiSelection || viewModel.HasSelectedEvidence) return;
        if (FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: RungRow row } item && RungList.IsVisualAncestorOf(item))
        {
            viewModel.SelectedRung = row;
        }
    }

    /// <summary>The breadcrumb's crumb that has the keyboard, or null when the keyboard is elsewhere.</summary>
    private CrumbRow? CrumbWithKeyboard() =>
        FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: CrumbRow crumb } item && CrumbList.IsVisualAncestorOf(item)
            ? crumb
            : null;

    /// <summary>
    /// The pointer equivalent of Enter. Descending and ascending never need a menu or a mode, so both
    /// gestures exist at every rung (section 3.2).
    /// </summary>
    private void DescendFromRow(object? sender, TappedEventArgs eventArgs)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
            return;
        }

        if (viewModel.HasSelectedEvidence)
        {
            OpenOriginalRecord();
        }
        else
        {
            _ = viewModel.Descend();
        }
    }

    private void OpenSearchHit(object? sender, TappedEventArgs eventArgs) => _ = OpenSelectedSearchHit();

    /// <summary>The pointer equivalent of Enter on a relationship, and of a double click on its edge (§6.7, R15).</summary>
    private void OpenRelationshipRow(object? sender, TappedEventArgs eventArgs) => eventArgs.Handled = OpenSelectedRelationship();

    /// <summary>
    /// Opens the relationship chosen in the table as a double click on its edge does, and gives the keyboard to the rung it
    /// opened: its rows, or the step that lists its records when it has none.
    /// </summary>
    private bool OpenSelectedRelationship()
    {
        if (DataContext is not WorkspaceViewModel viewModel || !viewModel.OpenSelectedRelationship()) return false;
        FocusRail();
        return true;
    }

    private bool OpenSelectedSearchHit()
    {
        if (DataContext is not WorkspaceViewModel viewModel) return false;

        // A moment gone to gives the keyboard to the timeline, where it is: [ and ] step on from the cell it chose, the
        // arrows pan around it, and the timeline says where it is (§6.7, R15). Any other hit gives it to the rung opened.
        bool moment = viewModel.SelectedSearchResult?.Hit.Kind == SearchHitKind.Moment;
        if (!viewModel.OpenSearchResult()) return false;
        if (moment) TimelineSurface.Focus(NavigationMethod.Directional);
        else FocusRail();
        return true;
    }

    private void AscendOrClear(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            _ = viewModel.Ascend();
        }
    }

    private void GoForward(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            _ = viewModel.GoForward();
        }
    }

    private void KeepVisibleRange(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            _ = viewModel.KeepVisibleRange();
        }
    }

    private void ToggleTables(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.ShowTables = !viewModel.ShowTables;
        }
    }

    private void ClearTimelineProcessFocus(object? sender, RoutedEventArgs eventArgs) =>
        workspace.ClearProcessLaneFocus();

    private void StartExploring(object? sender, RoutedEventArgs eventArgs) => StartChosenCapture();

    /// <summary>
    /// Starts the capture the Keep choice names: Explore, or the messages of processes the person now chooses. While
    /// something holds the session shown, nothing starts, and the card says what and until when.
    /// </summary>
    private void StartChosenCapture()
    {
        if (HeldBy(start: true) is { } held)
        {
            Say(CaptureDetail, held, aloud: true);
            return;
        }

        if (KeepChoice.Content)
        {
            ChooseContentCapture();
        }
        else
        {
            BeginCapture();
        }
    }

    /// <summary>The pointer equivalent of E: one step to the current rung's source records (section 3.2).</summary>
    private void ShowSourceRecords(object? sender, RoutedEventArgs eventArgs) => _ = workspace.ShowEvidence();

    /// <summary>The visible equivalent of Enter on a multi-selection (§6.7): its records, the set their filter.</summary>
    private void ShowChosenRecords(object? sender, RoutedEventArgs eventArgs)
    {
        if (workspace.ShowChosenRecords())
        {
            FocusRail();
        }
    }

    /// <summary>
    /// Opens the focused ranked row's menu for the context-menu key and Shift+F10. They raise their request on the row
    /// itself, above the element the menu is attached to, so without this the request never reached the menu and only a
    /// right-click, which starts inside the row, opened it (R15: the menu equivalent of Ctrl+click has a keyboard path).
    /// </summary>
    private void OpenRowMenu(object? sender, ContextRequestedEventArgs request)
    {
        if (request.Handled || request.Source is not ListBoxItem row || row.DataContext is RungRow rung && !OffersMenu(rung)) return;
        if (row.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.ContextMenu is not null) is
            { ContextMenu: { } menu } owner)
        {
            menu.Open(owner);
            request.Handled = true;
        }
    }

    /// <summary>
    /// Shift+F10 is Windows' context-menu key for keyboards without one. Where the platform raises no context request for
    /// it, as the headless one does not, the window raises it on the focused control as the key would; where it does, the
    /// request it raised has handled the key and this does nothing.
    /// </summary>
    private void RequestContextMenuOnShiftF10(object? sender, KeyEventArgs key)
    {
        if (key.Handled || key.Key != Key.F10 || key.KeyModifiers != KeyModifiers.Shift || key.Source is not Control focused) return;
        var request = new ContextRequestedEventArgs();
        focused.RaiseEvent(request);
        key.Handled = request.Handled;
    }

    /// <summary>
    /// Keeps a ranked row's menu closed where it has nothing to offer. A record stands for no process a selection holds and
    /// links no call, so its menu opened empty, and a call's or a channel's offered to add it to the selection, which added
    /// nothing; the selection's item is now shown only on a row that stands for processes.
    /// </summary>
    private void KeepEmptyRowMenuClosed(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        // The menu is told its row only once it is open, so the row is found by the menu it carries.
        if (RungList.GetRealizedContainers()
                .SelectMany(container => container.GetVisualDescendants().OfType<Control>())
                .FirstOrDefault(control => ReferenceEquals(control.ContextMenu, sender))?.DataContext is RungRow row
            && !OffersMenu(row))
        {
            eventArgs.Cancel = true;
        }
    }

    /// <summary>Whether a ranked row's menu has something to offer: the selection, or the call at its other end.</summary>
    private static bool OffersMenu(RungRow row) => row.JoinsSelection || row.HasOtherEnd;

    /// <summary>The menu equivalent of Ctrl+click on a ranked row (§6.7, R15).</summary>
    private void ToggleRowInSelection(object? sender, RoutedEventArgs eventArgs) =>
        workspace.ToggleRungInSelection((sender as StyledElement)?.DataContext as RungRow);

    /// <summary>The row menu's way to the call at an RPC call's other end, as O is the keyboard's.</summary>
    private void OpenOtherEndOfRow(object? sender, RoutedEventArgs eventArgs)
    {
        if ((sender as StyledElement)?.DataContext is RungRow row && workspace.OpenOtherEnd(row))
        {
            FocusRail();
        }
    }

    /// <summary>
    /// §6.7's Ctrl+click on a ranked row: adds it to the multi-selection or removes it, never selecting it alone. A row that
    /// joins no selection - a channel, a call, a record - is left to the list, whose Ctrl+click chooses it, where the click
    /// was taken and did nothing.
    /// </summary>
    private void OnRungListPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !eventArgs.GetCurrentPoint(RungList).Properties.IsLeftButtonPressed
            || (eventArgs.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext
                is not RungRow { JoinsSelection: true } row)
        {
            return;
        }

        workspace.ToggleRungInSelection(row);
        eventArgs.Handled = true;
    }

    /// <summary>
    /// The ranked table's keyboard multi-selection, as Windows lists have it: Ctrl+Up and Ctrl+Down already move the
    /// list's focus without selecting, and Ctrl+Space adds the focused row to the multi-selection or removes it.
    /// </summary>
    private bool HandleRungListSelectionKey(WorkspaceViewModel viewModel, KeyEventArgs eventArgs)
    {
        if (!RungList.IsKeyboardFocusWithin || eventArgs.Key != Key.Space || !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return false;
        }

        ListBoxItem? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as ListBoxItem;
        if ((focused?.DataContext as RungRow ?? viewModel.SelectedRung) is not { JoinsSelection: true } row)
        {
            // A row that joins no selection is left to the list, as its Ctrl+click is.
            return false;
        }

        viewModel.ToggleRungInSelection(row);
        return true;
    }

    private void LoadMoreRecords(object? sender, RoutedEventArgs eventArgs) => _ = workspace.LoadMoreAsync();

    /// <summary>The pointer equivalent of P: pins the selected graph node where it is drawn, or releases its pin.</summary>
    private void TogglePinNode(object? sender, RoutedEventArgs eventArgs) => _ = workspace.TogglePinSelectedGraphNode();

    /// <summary>
    /// The pointer equivalent of P in the timeline: pins the selected process's lane at the top of its group's lanes, or
    /// unpins it (§6.2).
    /// </summary>
    private void ToggleLanePin(object? sender, RoutedEventArgs eventArgs) => _ = workspace.ToggleSelectedLanePin();

    /// <summary>The pointer equivalent of L: lays the graph out afresh, keeping pinned nodes where they are.</summary>
    private void RelayoutGraph(object? sender, RoutedEventArgs eventArgs) => _ = workspace.RelayoutGraph();

    private void SelectParent(object? sender, RoutedEventArgs eventArgs) => _ = workspace.SelectParent();

    private void SelectChildren(object? sender, RoutedEventArgs eventArgs) => _ = workspace.SelectChildren();

    private void ExportView(object? sender, RoutedEventArgs eventArgs) => ExportView();

    private void ShareRedactedView(object? sender, RoutedEventArgs eventArgs) => ExportView(redacted: true);

    /// <summary>
    /// Exports the applied view (§6.4, §6.7 Ctrl+E): the rung's ranked rows or its evidence scope's records, named by
    /// session, generation, rung, filters and interval. The user chooses the file; nothing is written anywhere else.
    /// </summary>
    private async void ExportView(bool redacted = false)
    {
        if (exporting || !workspace.CanExport)
        {
            // Ctrl+E asks whatever the rung shows, so it is told why there is nothing, as the button's tip says.
            if (!closed && !workspace.CanExport) CaptureDetail.Text = workspace.ExportTip;
            return;
        }

        exporting = true;
        try
        {
            if (redacted && !await ConfirmRedactedShareAsync()) return;
            ExportContext context = workspace.DescribeExport(DateTimeOffset.UtcNow);
            string? path = await Choices.SaveFileAsync(new()
            {
                Title = redacted ? "Save redacted sharing report" : "Export this view",
                SuggestedFileName = redacted
                    ? RedactedShareExport.SuggestedName("json")
                    : WorkspaceExport.SuggestedName(context, "json"),
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new("JSON, self-describing") { Patterns = ["*.json"] },
                    new("CSV, one row per line") { Patterns = ["*.csv"] },
                ],
            });
            if (path is null || closed) return;
            ExportFormat format = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Csv : ExportFormat.Json;
            SessionExportResult written = await WriteExportAsync(path, format, redacted);
            if (!closed)
            {
                string what = Spoken.Count(written.Rows, written.Context.Rung == DetailLevel.Evidence ? "record" : "row");
                CaptureDetail.Text = written.Context.Complete
                    ? $"{(redacted ? "Saved redacted report with" : "Exported")} {what} (complete) to {path}."
                    : $"{(redacted ? "Saved redacted report with" : "Exported the first")} {what} of the scope to {path}; the file says what it leaves out.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            if (!closed) CaptureDetail.Text = "Could not export this view: " + exception.Message;
        }
        finally
        {
            exporting = false;
        }
    }

    /// <summary>Writes the applied view to a chosen path: ranked rows as shown, or the evidence scope read in one pass.</summary>
    internal async Task<SessionExportResult> WriteExportAsync(string path, ExportFormat format, bool redacted = false)
    {
        SessionExportResult result = await workspace.ExportAsync(format, DateTimeOffset.UtcNow,
            redacted: redacted);
        await ExportFileWriter.WriteAsync(path, result.Content, overwrite: true);
        return result;
    }

    private async Task<bool> ConfirmRedactedShareAsync() => await RedactedSharePrompt().ShowDialog<bool>(this);

    /// <summary>What a redacted report keeps and leaves out, asked before its file is chosen; true to choose it.</summary>
    internal static Window RedactedSharePrompt()
    {
        var prompt = new Window
        {
            Title = "Share a redacted report?", Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var cancel = new Button { Content = "Cancel" };
        var proceed = new Button { Content = "Choose report file" };
        cancel.Click += (_, _) => prompt.Close(false);
        proceed.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => cancel.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "This creates a metadata-only, pseudonymized report; it cannot reopen a session.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Included: relative times, counts, sizes, status, quality, what the capture covered, and random relationship tokens consistent only within this file.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Omitted: capture and session IDs, process and resource names, addresses, ports, original files, raw record locators, and content bytes.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "This is not anonymous: timing and workload patterns can still identify a system. Review the saved file before sharing.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, proceed } },
            },
        };
        return prompt;
    }

    /// <summary>
    /// Saves a support bundle (§20.6): what it holds and leaves out is said first, then its file is chosen. It holds the
    /// session shown, if any, by its folder's name, and nothing a record holds (P16).
    /// </summary>
    private async void SaveSupportBundle(object? sender, RoutedEventArgs eventArgs)
    {
        if (savingSupport)
        {
            return;
        }

        savingSupport = true;
        try
        {
            string[] sessions = currentSessionPath is { } shown ? [shown] : [];
            if (!await SupportBundlePrompt(sessions.Length).ShowDialog<bool>(this) || closed)
            {
                return;
            }

            string? path = await Choices.SaveFileAsync(new()
            {
                Title = "Save support bundle",
                SuggestedFileName = SupportBundle.SuggestedName,
                DefaultExtension = "json",
                FileTypeChoices = [new("JSON support bundle") { Patterns = ["*.json"] }],
            });
            if (path is null || closed)
            {
                return;
            }

            await WriteSupportBundleAsync(path, sessions);
            if (!closed)
            {
                CaptureDetail.Text = $"Support bundle saved to {path}. It holds no name, address or content; review it before "
                    + "sending it.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed) CaptureDetail.Text = "Could not save the support bundle: " + exception.Message;
        }
        finally
        {
            savingSupport = false;
        }
    }

    /// <summary>Writes the support bundle of <paramref name="sessions"/> to <paramref name="path"/>, off the UI thread.</summary>
    internal static Task WriteSupportBundleAsync(string path, IReadOnlyList<string> sessions) => Task.Run(() =>
        File.WriteAllText(path, SupportBundle.Serialize(SupportBundle.Make(sessions, capabilities: null))));

    /// <summary>What a support bundle holds and leaves out, said before its file is chosen; true to choose it.</summary>
    internal static Window SupportBundlePrompt(int sessions)
    {
        var prompt = new Window
        {
            Title = "Save a support bundle?", Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var cancel = new Button { Content = "Cancel" };
        var proceed = new Button { Content = "Choose bundle file" };
        cancel.Click += (_, _) => prompt.Close(false);
        proceed.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => cancel.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "A support bundle helps someone see why a capture or a view went wrong.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                // In the words icat support lists them in (R18).
                new TextBlock { Text = "Holds: " + string.Join("; ", SupportBundle.Holds(sessions, capabilities: false)) + ".",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Leaves out: " + string.Join("; ", SupportBundle.LeftOut) + ".",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Made here, it holds no capability report: icat support adds this machine's.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, proceed } },
            },
        };
        return prompt;
    }

    private void ExploreDemo(object? sender, RoutedEventArgs eventArgs) => _ = ExploreDemoAsync();

    /// <summary>
    /// Opens InterCat's generated demo investigation (§14 M5) beside this window, making it the first time in a folder of
    /// InterCat's own, and says what it is; null when it could not be, which the card then says.
    /// </summary>
    internal async Task<InvestigationWindow?> ExploreDemoAsync()
    {
        if (exploringDemo || closed)
        {
            return null;
        }

        exploringDemo = true;
        ExploreDemoButton.IsEnabled = false;
        ExploreDemoItem.IsEnabled = false;
        try
        {
            string root = DemoRoot ?? DemoPlace.Root();
            string workspace = await Task.Run(() => DemoPlace.Ensure(root));
            if (closed)
            {
                return null;
            }

            CaptureDetail.Text = DemoInvestigation.Disclosure + " Its investigation is open beside this window; open either "
                + "of its sessions from there.";
            return ShowInvestigation(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!closed) CaptureDetail.Text = "The demo could not be opened: " + exception.Message;
            return null;
        }
        finally
        {
            exploringDemo = false;
            ExploreDemoButton.IsEnabled = true;
            ExploreDemoItem.IsEnabled = true;
        }
    }

    private void OpenOriginalRecord(object? sender, RoutedEventArgs eventArgs) => OpenOriginalRecord();

    private void UpdateHeldGeneration(object? sender, RoutedEventArgs eventArgs) => ApplyHeldUpdate();

    /// <summary>
    /// Opens the selected record's original journal envelope. It is found by its stable raw identity, so it stays
    /// reachable while a live capture publishes newer generations.
    /// </summary>
    private async void OpenOriginalRecord()
    {
        if (openingRecord || workspace.SelectedEvidence is not { } record || currentSessionPath is null
            || displayedSessionId is not { } sessionId) return;
        openingRecord = true;
        try
        {
            using var window = new SessionRawRecordWindow(currentSessionPath, sessionId, record);
            await window.ShowDialog(this);
        }
        catch (InvalidOperationException exception)
        {
            if (!closed) CaptureDetail.Text = "Could not open the original record: " + exception.Message;
        }
        finally
        {
            openingRecord = false;
        }
    }

    private void OpenContent(object? sender, RoutedEventArgs eventArgs) => OpenContent();

    /// <summary>
    /// Opens the selected record's kept content (§3.7). Its bytes stay hidden until the person asks in the window, and
    /// the record is found by its stable raw identity, as the original record is.
    /// </summary>
    private async void OpenContent()
    {
        if (openingRecord || workspace.SelectedEvidence is not { Content: not null } record || currentSessionPath is null
            || displayedSessionId is not { } sessionId) return;
        openingRecord = true;
        try
        {
            using var window = new SessionContentWindow(currentSessionPath, sessionId, record);
            await window.ShowDialog(this);
        }
        catch (InvalidOperationException exception)
        {
            if (!closed) CaptureDetail.Text = "Could not open the record's content: " + exception.Message;
        }
        finally
        {
            openingRecord = false;
        }
    }

    private async void BrowseChannels(object? sender, RoutedEventArgs eventArgs)
    {
        if (currentSessionPath is null || displayedSessionId is not { } sessionId
            || displayedGeneration < 1) return;
        if (workspace.IsEvidenceRung) return;
        ProcessInstanceId? processScope = ChannelDiscoveryScope(workspace.CaptureNavigation());
        WorkspaceSnapshot processes = workspace.WholeSnapshot;
        using var browser = new SessionChannelWindow(currentSessionPath, sessionId, displayedGeneration, processScope,
            id => processes.Processes.FirstOrDefault(process => process.Id == id)?.NameWithPid);
        try
        {
            // A chosen channel opens at the evidence rung of this workspace, so its records sit in the ladder with
            // the breadcrumb, filter bar and Esc back to where the user was.
            if (await browser.ShowDialog<Channel?>(this) is { } chosen && !closed)
            {
                _ = workspace.ShowChannelEvidence(chosen);
            }
        }
        catch (InvalidOperationException exception)
        {
            if (!closed) CaptureDetail.Text = "Could not open channel discovery: " + exception.Message;
        }
    }

    private void StopCapture(object? sender, RoutedEventArgs eventArgs)
    {
        StopCaptureButton.IsEnabled = false;
        captureStop?.Cancel();
        KeepKeyboard();
    }

    /// <summary>
    /// Whether a saved session can be opened now, as Open saved session opens one: while the button is offered - a
    /// capture under way hides it, and an open or the finishing of an unfinished capture disables it - while no folder
    /// is being chosen, and while nothing holds the session shown.
    /// </summary>
    private bool OpensSavedSessionNow =>
        OpenSavedSessionButton is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
        && !choosingSession && HeldBy(start: false) is null;

    /// <summary>
    /// Why the window can neither show another session nor start a capture now, as the card says it; null when it can. A
    /// capture under way, a session being opened, a package of the session shown being saved and an unfinished capture
    /// being finished each hold the session shown until they end, or their own button stops them: what replaced the
    /// session would otherwise have the package's progress and end, or the finish's session, written over it.
    /// </summary>
    private string? HeldBy(bool start)
    {
        string act = start ? "start exploring" : "open another session";
        return captureTask is { IsCompleted: false }
            ? phase == CaptureUiPhase.Finishing ? $"The capture is being saved; {act} once it is."
                : start ? "A capture is already under way; Stop ends it."
                : $"A capture is under way; {act} once it has stopped."
            : openingSession ? $"A session is being opened; {act} once it is open."
            : packaging is not null
                ? $"A package of the session shown is being saved; {act} once it is saved, or cancel it with Cancel packaging."
            : finishingCapture is not null
                ? $"An unfinished capture is being finished; {act} once it has finished, or stop it with Stop finishing."
            : null;
    }

    private async void OpenSavedSession(object? sender, RoutedEventArgs eventArgs)
    {
        if (!OpensSavedSessionNow)
        {
            // What holds the session shown says why nothing opens; a folder already being chosen is in front.
            if (!choosingSession && HeldBy(start: false) is { } held) Say(CaptureDetail, held, aloud: true);
            return;
        }

        string? folder;
        choosingSession = true;
        try
        {
            // The picker starts where InterCat saves sessions, so the user's own captures are the first thing shown.
            Avalonia.Platform.Storage.IStorageFolder? saved = sessionRoot is { } root && Directory.Exists(root)
                ? await Choices.FolderAtAsync(root)
                : null;
            folder = await Choices.OpenFolderAsync(new()
            {
                Title = "Open an InterCat session directory", AllowMultiple = false, SuggestedStartLocation = saved,
            });
        }
        finally
        {
            choosingSession = false;
        }

        if (folder is not null && !closed) _ = await OpenSessionAsync(folder);
    }

    /// <summary>
    /// Takes what a drag over the window carries - files or folders, never text - while a saved session could be
    /// opened, and nothing while a capture, an open or a package is under way, as Open saved session waits for them;
    /// the pointer says which before anything is let go.
    /// </summary>
    private void OfferDrop(object? sender, DragEventArgs e)
    {
        e.DragEffects = OpensSavedSessionNow && e.DataTransfer.TryGetFiles() is { Length: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Opens what was dropped on the window: a saved session's folder, as Open saved session opens the one chosen, or
    /// an investigation file, as the Investigation menu does. Anything else, or several at once, opens nothing, and the
    /// card says why.
    /// </summary>
    private async void OpenDropped(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (closed || !OpensSavedSessionNow || e.DataTransfer.TryGetFiles() is not { Length: > 0 } items) return;
        string? path = items is [var item] ? item.TryGetLocalPath() : null;
        if (path is not null && Directory.Exists(path))
        {
            _ = await OpenSessionAsync(path);
        }
        else if (path is not null && File.Exists(path)
            && path.EndsWith(InvestigationWorkspace.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowInvestigation(path);
        }
        else
        {
            Say(CaptureStatus, "Nothing was opened", aloud: true);
            Say(CaptureDetail, NotOpened(items) + " The current workspace is unchanged.", aloud: true);
        }
    }

    /// <summary>Why a drop opened nothing: several things at once, or one that InterCat does not open.</summary>
    private static string NotOpened(IStorageItem[] items) => items is [var one]
        ? $"{one.Name} cannot be opened: InterCat opens a saved session's folder, or an investigation file "
            + $"({InvestigationWorkspace.Extension}), on this computer."
        : $"{items.Length} items were dropped at once; InterCat opens one saved session's folder or investigation file "
            + "at a time.";

    /// <summary>Opens a folder in the system's file manager, true once it did; a test opens it in its place.</summary>
    internal Func<string, Task<bool>> FolderOpener { get; set; }

    /// <summary>
    /// Opens the folder of the session shown in the system's file manager (§3.1), where a person copies, moves or
    /// shares it whole. One gone since it was opened says so, as does one the system would not open.
    /// </summary>
    private async void OpenSessionFolder(object? sender, RoutedEventArgs eventArgs)
    {
        if (CaptureSessionPath.Text is not { Length: > 0 } folder) return;
        string? why = null;
        try
        {
            if (!Directory.Exists(folder)) why = "it is no longer there, moved or deleted";
            else if (!await FolderOpener(folder)) why = "the system did not open it";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            why = exception.Message.TrimEnd('.');
        }

        if (why is null || closed) return;
        Say(CaptureStatus, "The session's folder was not opened", aloud: true);
        Say(CaptureDetail, $"{folder} could not be opened: {why}.", aloud: true);
    }

    /// <summary>Copies the whole path of the session shown, which its line in the card may shorten (§3.1).</summary>
    private async void CopySessionPath(object? sender, RoutedEventArgs eventArgs)
    {
        if (CaptureSessionPath.Text is not { Length: > 0 } folder || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(folder);
        if (!closed) Say(CaptureDetail, "The session's path is copied: " + folder, aloud: true);
    }

    private async void OpenInvestigation(object? sender, RoutedEventArgs eventArgs)
    {
        string? file = await Choices.OpenFileAsync(new()
        {
            Title = "Open an InterCat investigation",
            AllowMultiple = false,
            FileTypeFilter = [InvestigationFiles],
        });
        if (file is not null && !closed) _ = ShowInvestigation(file);
    }

    private async void NewInvestigation(object? sender, RoutedEventArgs eventArgs)
    {
        string? file = await Choices.SaveFileAsync(new()
        {
            Title = "Start an InterCat investigation",
            SuggestedFileName = "investigation" + InvestigationWorkspace.Extension,
            DefaultExtension = InvestigationWorkspace.Extension.TrimStart('.'),
            FileTypeChoices = [InvestigationFiles],
            ShowOverwritePrompt = false,
        });
        if (file is null || closed) return;
        _ = ShowInvestigation(file, create: true);
    }

    private static readonly Avalonia.Platform.Storage.FilePickerFileType InvestigationFiles = new("InterCat investigation")
    {
        Patterns = ["*" + InvestigationWorkspace.Extension],
    };

    /// <summary>
    /// Shows an investigation in a window of its own, beside the session this window shows, making it first when asked
    /// and none is there; a file that already exists is opened, never written over. Opening a member opens it here. An
    /// investigation already shown is brought in front rather than shown twice, so two windows never write one file.
    /// </summary>
    internal InvestigationWindow ShowInvestigation(string path, bool create = false)
    {
        string full = Path.GetFullPath(path);
        if (investigations.FirstOrDefault(shown => string.Equals(shown.WorkspacePath, full,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) is { } open)
        {
            Forward(open);
            return open;
        }

        string? made = null;
        if (create && !File.Exists(full))
        {
            try
            {
                InvestigationWorkspace.Create(full, DateTimeOffset.UtcNow);
                made = "A new investigation: add the sessions it covers.";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                made = "The investigation could not be made: " + exception.Message;
            }
        }

        var window = new InvestigationWindow(full,
            (session, member) => FromInvestigationAsync(full, () => OpenSessionOrSayWhyNotAsync(session, null, member)), made,
            (session, member, interval) => FromInvestigationAsync(full, () => OpenSessionAtAsync(session, interval, member)),
            other => ShowInvestigation(other));
        investigations.Add(window);
        window.Closed += (_, _) => investigations.Remove(window);

        // A peer, not a window this one owns, which Windows would keep in front of it: a session opened from the investigation
        // is shown here, and this window must be able to come in front to show it. It opens centred over this window, as an
        // owned one would, on this window's screen.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        double scale = RenderScaling;
        PixelRect here = new(Position, PixelSize.FromSize(ClientSize, scale));
        PixelSize size = PixelSize.FromSize(new Size(window.Width, window.Height), scale);
        window.Position = here.Center - new PixelPoint(size.Width / 2, size.Height / 2);
        window.Show();
        return window;
    }

    /// <summary>Brings a window in front, restored when minimized, so what it now shows is seen and has the keyboard.</summary>
    private static void Forward(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>Opens a member from its investigation, which then keeps its pins and view settings (§26.3).</summary>
    private async Task<string?> FromInvestigationAsync(string investigation, Func<Task<string?>> open)
    {
        openingFromInvestigation = investigation;
        string? refused;
        try
        {
            refused = await open();
        }
        finally
        {
            openingFromInvestigation = null;
        }

        // The session is shown here, so this window comes in front of the investigation's to show it, the keyboard on its
        // ranked table, as a session opened here has it, rather than on whatever control of this window last had it.
        if (refused is null && !closed)
        {
            Forward(this);
            FocusRail();
        }

        return refused;
    }

    /// <summary>
    /// What member <paramref name="sessionId"/> keeps in <paramref name="investigation"/>: its pins and pinned lanes, none
    /// when it keeps none, its view settings, the defaults where it keeps none, and the window's panes the investigation
    /// keeps for all its sessions, if any; null when the file cannot be read or does not name the session, which then keeps
    /// them only while it is open.
    /// </summary>
    private static (Dictionary<string, GraphPoint> Pins, IReadOnlyList<ProcessInstanceId> Lanes, ViewSettings Settings,
        WorkspacePanes? Panes)? LayoutKeptIn(string investigation, Guid sessionId)
    {
        try
        {
            InvestigationWorkspaceFile file = InvestigationWorkspace.Read(investigation);
            if (file.Members.All(member => member.SessionId != sessionId))
            {
                return null;
            }

            WorkspaceLayout? layout = InvestigationWorkspace.LayoutOf(file, sessionId);
            return ((layout?.Pins ?? []).ToDictionary(pin => pin.Key, pin => new GraphPoint(pin.X, pin.Y), StringComparer.Ordinal),
                [.. (layout?.PinnedLanes ?? []).Select(lane => new ProcessInstanceId(lane))],
                layout is null ? ViewSettings.Default : new(layout.RankBy ?? RankingMetric.Records, layout.PerSecond,
                    layout.EvidencePolicy ?? EvidencePolicy.IncludeCorrelated, layout.ScalesEachLane,
                    layout.Grouping ?? LaneGrouping.Executable, layout.CollectorsAside, layout.WallClock),
                file.Panes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What an investigation put back of a session's pins, pinned lanes and view settings, and of the window's panes, ending
    /// its notice in the words its window and `icat workspace show` say them in: ", which put back 2 nodes pinned on its
    /// graph and the timeline filling the column."
    /// </summary>
    private static string PutBack(int pins, int lanes, ViewSettings settings, WorkspacePanes? panes) =>
        WorkspaceLayout.Series(
        [
            .. WorkspaceLayout.Parts(pins, lanes, settings.Grouping, settings.RankBy, settings.PerSecond, settings.Policy,
                settings.ScalesEachLane, CultureInfo.CurrentCulture, settings.CollectorsAside, settings.WallClock),
            .. panes is null ? [] : WorkspacePanes.Parts(panes.GraphShare, panes.Expanded, CultureInfo.CurrentCulture,
                panes.RailWidth, panes.InspectorWidth),
        ]) is { Length: > 0 } restored
            ? $", which put back {restored}."
            : ".";

    /// <summary>
    /// Keeps the shown session's pins, pinned lanes and view settings in the investigation it was opened from, when they
    /// changed: one write after another, off the UI thread, and a write that fails is said beside the session's status
    /// rather than lost silently.
    /// </summary>
    private void KeepLayout((string Workspace, Guid Session) home)
    {
        IReadOnlyDictionary<string, GraphPoint> pins = workspace.GraphPins;
        ProcessInstanceId[] lanes = [.. workspace.PinnedLanes];
        ViewSettings settings = ViewSettings.Of(workspace, laneGrouping, collectorsAside);
        if (keptPins is { } kept && kept.Count == pins.Count
            && pins.All(pin => kept.TryGetValue(pin.Key, out GraphPoint at) && at == pin.Value)
            && keptLanes is { } lanesKept && lanesKept.SequenceEqual(lanes)
            && keptSettings == settings)
        {
            return;
        }

        keptPins = pins;
        keptLanes = lanes;
        keptSettings = settings;
        WorkspacePin[] layout = [.. pins.Select(pin => new WorkspacePin { Key = pin.Key, X = pin.Value.X, Y = pin.Value.Y })];
        Guid[] pinnedLanes = [.. lanes.Select(lane => lane.Value)];
        Task<WorkspaceLayout?> written = WriteToInvestigation(() => InvestigationWorkspace.SetLayout(home.Workspace, home.Session,
            layout, DateTimeOffset.UtcNow, settings.RankBy, settings.PerSecond, settings.Policy, settings.ScalesEachLane,
            pinnedLanes, settings.Grouping, settings.CollectorsAside, settings.WallClock));
        _ = SayWhatIsKeptAsync(written, home);
    }

    /// <summary>
    /// Writes to the shown session's investigation once every write to it before has ended, off the UI thread: two writes
    /// at once would each find the file changed since it was read, and refuse.
    /// </summary>
    private Task<T> WriteToInvestigation<T>(Func<T> write)
    {
        Task previous = investigationWritten;
        Task<T> written = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException)
            {
                // The earlier write said why it failed; this one tries afresh.
            }

            return write();
        });
        investigationWritten = written;
        return written;
    }

    /// <summary>
    /// Keeps the window's panes as a person left them in the investigation the shown session was opened from, when they
    /// differ from what it keeps (§6.1, §26.3), and has each window showing that investigation say so; a write that fails is
    /// said beside the session's status.
    /// </summary>
    private void KeepPanes((string Workspace, Guid Session) home)
    {
        (double share, WorkspacePane? expanded, double? rail, double? inspector) = PanesNow();
        (double GraphShare, WorkspacePane? Expanded, double? RailWidth, double? InspectorWidth) keeping = (
            WorkspacePanes.Kept(share), expanded,
            rail is { } ranked ? WorkspacePanes.KeptWidth(ranked) : null,
            inspector is { } inspecting && WorkspacePanes.KeptWidth(inspecting) != WorkspacePanes.InspectorDesign
                ? WorkspacePanes.KeptWidth(inspecting)
                : null);
        if (keptPanes == keeping)
        {
            return;
        }

        keptPanes = keeping;
        Task<WorkspacePanes?> written = WriteToInvestigation(() => InvestigationWorkspace.SetPanes(home.Workspace,
            keeping.GraphShare, keeping.Expanded, DateTimeOffset.UtcNow, keeping.RailWidth, keeping.InspectorWidth));
        _ = SayPanesKeptAsync(written, home.Workspace);
    }

    private async Task SayPanesKeptAsync(Task<WorkspacePanes?> written, string investigation)
    {
        WorkspacePanes? panes;
        try
        {
            panes = await written;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed)
            {
                keptPanes = null;
                SayUnkept(ref panesUnkept,
                    " The window's panes could not be kept in the investigation: " + InvestigationTrouble.Why(investigation, exception));
            }

            return;
        }

        if (!closed) SayUnkept(ref panesUnkept, null);
        foreach (InvestigationWindow shown in investigations)
        {
            shown.PanesKept(investigation, panes);
        }
    }

    /// <summary>
    /// The panes as a person left them: the graph's share of the height the two share - the split they return to, while one
    /// fills the column - the pane filling it, if one does, and the widths a person gave the rail and the inspector by their
    /// edges: none for a rail that follows the window.
    /// </summary>
    private (double GraphShare, WorkspacePane? Expanded, double? RailWidth, double? InspectorWidth) PanesNow()
    {
        RowDefinitions rows = PanesGrid.RowDefinitions;
        (GridLength graph, GridLength timeline) = expandedPane is null
            ? (rows[0].Height, rows[2].Height)
            : sharedSplit ?? (GridLength.Star, GridLength.Star);

        // A drag of the splitter leaves both rows shares of the height, in its pixels; one row set in pixels and the other
        // shared is no split it makes, and reads as equal halves.
        double share = graph.GridUnitType == timeline.GridUnitType && graph.Value + timeline.Value > 0
            && graph.GridUnitType is GridUnitType.Star or GridUnitType.Pixel
            ? graph.Value / (graph.Value + timeline.Value)
            : WorkspacePanes.EqualShare;
        return (share, expandedPane switch
        {
            MainPane.Graph => WorkspacePane.Graph,
            MainPane.Timeline => WorkspacePane.Timeline,
            _ => null,
        }, followedRailWidth is null ? WindowGrid.ColumnDefinitions[0].Width.Value : null, WindowGrid.ColumnDefinitions[2].Width.Value);
    }

    /// <summary>
    /// A column beside the main panes changed its width. A width a person set by any means but a drag - which is kept once it
    /// is let go - is kept once the columns have their widths; one the window fit to itself is not kept.
    /// </summary>
    private void SideColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ColumnDefinition.WidthProperty || layingPanes || fittingColumns)
        {
            return;
        }

        // A rail whose width a person changed no longer follows the window, from the change on.
        if (ReferenceEquals(sender, WindowGrid.ColumnDefinitions[0]))
        {
            followedRailWidth = null;
        }

        if (draggingSplit || splitToKeep)
        {
            return;
        }

        splitToKeep = true;
        Dispatcher.UIThread.Post(KeepSplit, DispatcherPriority.Background);
    }

    /// <summary>
    /// A row of the panes changed its height. A split a person moved by any means but a drag - which is kept once it is let
    /// go - is kept once every row it moved has its height.
    /// </summary>
    private void PaneRowChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != RowDefinition.HeightProperty || layingPanes || draggingSplit || splitToKeep)
        {
            return;
        }

        splitToKeep = true;
        Dispatcher.UIThread.Post(KeepSplit, DispatcherPriority.Background);
    }

    /// <summary>Keeps the split a person left in the shown session's investigation, when it was opened from one.</summary>
    private void KeepSplit()
    {
        splitToKeep = false;
        if (layoutHome is { } home)
        {
            KeepPanes(home);
        }
    }

    /// <summary>
    /// Once a layout is written, has each window showing its investigation say what it now keeps of the session; a write
    /// that failed is said beside the session's status instead.
    /// </summary>
    private async Task SayWhatIsKeptAsync(Task<WorkspaceLayout?> written, (string Workspace, Guid Session) home)
    {
        WorkspaceLayout? layout;
        try
        {
            layout = await written;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed)
            {
                keptPins = null;
                keptLanes = null;
                keptSettings = null;
                SayUnkept(ref layoutUnkept,
                    " The pins and view settings could not be kept in the investigation: " + InvestigationTrouble.Why(home.Workspace, exception));
            }

            return;
        }

        if (!closed) SayUnkept(ref layoutUnkept, null);
        foreach (InvestigationWindow shown in investigations)
        {
            shown.LayoutKept(home.Workspace, home.Session, layout);
        }
    }

    /// <summary>
    /// Says beside the session's status what its investigation could not keep, in place of what was said of it before,
    /// so a change that fails again and again is said once; null takes it back, once a change is kept there again.
    /// </summary>
    private void SayUnkept(ref string? said, string? note)
    {
        string detail = CaptureDetail.Text ?? string.Empty;
        if (said is not null) detail = detail.Replace(said, string.Empty, StringComparison.Ordinal);
        said = note;
        CaptureDetail.Text = detail + note;
    }

    /// <summary>
    /// Completes once every change kept in the shown session's investigation - its layout and the window's panes - is
    /// written there.
    /// </summary>
    internal Task InvestigationWritten => investigationWritten;

    /// <summary>
    /// Opens a session - or keeps it, when it is the one shown - with its timeline zoomed to <paramref name="interval"/> of
    /// its own time, with a column of it either side, and that interval selected, so its records are what this window shows.
    /// With <paramref name="expected"/>, only that session is opened or kept, as <see cref="OpenSessionAsync"/> says. Null
    /// once it is shown, else why it is not.
    /// </summary>
    internal async Task<string?> OpenSessionAtAsync(string path, TimeRange interval, Guid? expected = null)
    {
        string full = Path.GetFullPath(path);
        bool shown = currentSessionPath is { } current
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)), Path.TrimEndingDirectorySeparator(full),
                StringComparison.OrdinalIgnoreCase)
            && (expected is null || displayedSessionId == expected);
        if (!shown && await OpenSessionOrSayWhyNotAsync(full, null, expected) is { } refused)
        {
            return refused;
        }

        if (closed)
        {
            return "InterCat's window closed.";
        }

        // A column may reach past the session's own records, before or after its capture: only what the session holds of it
        // is selected, and nothing when it holds none of it - the session is then shown whole.
        TimeRange extent = workspace.Snapshot.Extent;
        if (interval.StartTicks >= extent.EndTicks || interval.EndTicks <= extent.StartTicks)
        {
            return null;
        }

        TimeRange held = new(Math.Max(interval.StartTicks, extent.StartTicks), Math.Min(interval.EndTicks, extent.EndTicks));
        long span = Math.Max(held.EndTicks - held.StartTicks, 1);
        TimelineSurface.SetViewport(new TimeRange(held.StartTicks - span, held.EndTicks + span));
        workspace.SelectInterval(held);
        return null;
    }

    /// <summary>
    /// Opens a published session directory as the workspace. A redacted package says so where the status is read, so
    /// nobody mistakes its pseudonyms for the source machine's names and IDs. False when the directory could not open;
    /// the current workspace is then unchanged. With <paramref name="expected"/> - an investigation's member, a saved
    /// session the rail listed - a directory that now holds another session, or none, is refused rather than opened as it.
    /// </summary>
    internal async Task<bool> OpenSessionAsync(string path, (string Headline, string Detail)? status = null,
        Guid? expected = null) => await OpenSessionOrSayWhyNotAsync(path, status, expected) is null;

    /// <summary>How the window opens a session's store, off its thread; a test holds an open partway, as a large one's is.</summary>
    internal Func<string, Guid?, SessionStore> SessionOpener { get; set; } = SharedSessionStores.Open;

    /// <summary>
    /// Opens a session as <see cref="OpenSessionAsync"/> does: null once it is open, else why it is not, as said where the
    /// status is read - what holds the session shown, among the reasons.
    /// </summary>
    private async Task<string?> OpenSessionOrSayWhyNotAsync(string path, (string Headline, string Detail)? status,
        Guid? expected)
    {
        if (HeldBy(start: false) is { } held)
        {
            Say(CaptureDetail, held, aloud: true);
            return held;
        }

        openingSession = true;
        StartExploringButton.IsEnabled = false;
        OpenSavedSessionButton.IsEnabled = false;
        try
        {
            CaptureStatus.Text = "Opening saved session";
            string? investigation = openingFromInvestigation;
            (SessionStore store, SessionOverviewBundle overview) = await Task.Run(() =>
            {
                SessionStore opened = SessionOpener(path, expected);

                // A session opened is a workspace of its own, which counts correlated evidence until its person chooses,
                // or as its investigation kept it when it is opened from one (§26.3).
                EvidencePolicy policy = investigation is not null && LayoutKeptIn(investigation, opened.SessionId) is { } kept
                    ? kept.Settings.Policy
                    : EvidencePolicy.IncludeCorrelated;
                try
                {
                    return (opened, SessionOverviewProjector.Project(opened, policy));
                }
                catch (InvalidDataException) when (!opened.VerifyContents().Verified)
                {
                    // The first view read a file that fails its own checksum, and hashing what the generation names
                    // found files that changed after they were published. The hashing forgot them, so this projection's
                    // lease falls back to the last complete generation, as hashing at open once did.
                    return (opened, SessionOverviewProjector.Project(opened, policy));
                }
            });
            if (closed) return string.Empty;
            evidencePolicy = overview.Policy;
            int run = ++captureRunId;
            heldUpdate = null;
            CaptureSummary.Text = string.Empty;
            (string headline, string detail) = overview.Redaction is { } redaction
                ? ("Redacted session package open", redaction.Statement(CultureInfo.CurrentCulture))
                : status ?? ("Saved session open",
                    // What the graph draws is said in its own header (GraphScope), so the card keeps one line and the
                    // smallest window's ranked list its room (§6.1).
                    string.Create(CultureInfo.CurrentCulture, $"Published generation {overview.Generation:N0}."));

            // Opening on the last complete generation is stated, never passed off as the newest (S7).
            if (store.RollbackReason is { } fallback)
            {
                detail = string.Create(CultureInfo.CurrentCulture,
                    $"Its newest generation could not be verified, so generation {overview.Generation:N0}, the last complete "
                    + $"one, is shown: {fallback}. Nothing on disk was changed. {detail}");
            }

            ApplyCaptureUpdate(new(CaptureUiPhase.Complete, headline, detail, SessionPath: path, Overview: overview),
                forceOverview: true);
            SessionFilesChecked = VerifySessionFilesAsync(path, store, run);

            // The session opened is where the keyboard goes, not back to the control that opened it.
            lastKeyboard = null;
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            CaptureStatus.Text = "Could not open this session";
            CaptureDetail.Text = exception.Message + " The current workspace is unchanged.";
            return exception.Message;
        }
        finally
        {
            openingSession = false;
            if (!closed)
            {
                StartExploringButton.IsEnabled = true;
                OpenSavedSessionButton.IsEnabled = true;
                UpdateEvidenceAction();
                KeepKeyboard();
            }
        }
    }

    /// <summary>Completes when the latest saved session's files have been hashed and any fallback has been opened.</summary>
    internal Task SessionFilesChecked { get; private set; } = Task.CompletedTask;

    /// <summary>How a saved session's files are hashed after its first view; a test holds the hashing back with it.</summary>
    internal Func<SessionStore, StoreContentReport> SessionFilesVerifier { get; set; } = store => store.VerifyContents();

    /// <summary>
    /// Hashes a saved session's files after its first view (store-v1 §6). Until then every byte a query read was checked
    /// against its own file's checksums; this binds the files to what their generation recorded. A file that changed
    /// after it was published fails its generation over to the last complete one, which is opened in its place, with
    /// the reason stated. Nothing is shown while the files check out.
    /// </summary>
    private async Task VerifySessionFilesAsync(string path, SessionStore store, int run)
    {
        StoreContentReport report;
        try
        {
            Func<SessionStore, StoreContentReport> verify = SessionFilesVerifier;
            report = await Task.Run(() => verify(store));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            // The session could not be leased just now. The next query's lease measures what it must again.
            return;
        }

        // Another open or a capture that began meanwhile supersedes this session's view, and a finish under way ends by
        // opening its own: whatever holds the session shown keeps it as it is, and its next lease measures again. A package
        // asked for waits for this check before it begins.
        if (closed || run != captureRunId || report.Verified || HeldBy(start: false) is not null)
        {
            return;
        }

        if (!await OpenSessionAsync(path) && !closed && run == captureRunId)
        {
            CaptureDetail.Text = "Some of this session's files changed after they were published ("
                + string.Join("; ", report.Problems) + "), and no earlier generation is complete. What is shown was "
                + "read before that was found, from files that no longer hold what was recorded.";
        }
    }

    /// <summary>The themes the menu offers, "follow the system" first (§26.2).</summary>
    private static readonly (ThemeMode? Mode, string Label)[] ThemeChoices =
    [
        (null, "Follow the system"),
        (ThemeMode.Dark, "Dark"),
        (ThemeMode.Light, "Light"),
        (ThemeMode.HighContrastDark, "High contrast, dark"),
        (ThemeMode.HighContrastLight, "High contrast, light"),
    ];

    /// <summary>
    /// Takes the application's settings (§26.3): the theme the user chose, where it is kept, and what reading the file
    /// found. Without a store a choice lasts until InterCat closes, and the menu says so.
    /// </summary>
    internal void UseSettings(ApplicationSettingsStore? store, ApplicationSettings loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        settingsStore = store;
        settings = loaded;
        settingsProblem = null;
        keptPlacement = loaded.Placement;
        if (loaded.Placement is { } placement) Place(placement);
        UpdateThemeMenu();
    }

    /// <summary>
    /// Opens the window where it last closed and as large, maximized if it was, when a screen still shows its title
    /// bar (§26.3); else where the system opens a window, maximized if it was.
    /// </summary>
    private void Place(WindowPlacement kept)
    {
        IEnumerable<(PixelRect, double)> screens = Screens.All.Select(screen => (screen.WorkingArea, screen.Scaling));
        if (WindowPlacing.Restore(kept, screens, new Size(MinWidth, MinHeight)) is ({ } position, { } size))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = normalPosition = position;
            (Width, Height) = (size.Width, size.Height);
            normalSize = size;
        }

        if (kept.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Notes where the window stands while neither maximized nor minimized, to keep as it closes.</summary>
    private void KeepNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        normalPosition = Position;
        normalSize = ClientSize;
    }

    /// <summary>
    /// Keeps where the window closed in the settings file, so it opens there next time (§26.3), unless that is where
    /// the file already places it. A file that cannot be written leaves it to open where the system places it.
    /// </summary>
    private void KeepPlacement()
    {
        if (settingsStore is not { } store || normalSize.Width <= 0 || normalSize.Height <= 0) return;
        var placement = new WindowPlacement(normalPosition.X, normalPosition.Y, normalSize.Width, normalSize.Height,
            WindowState == WindowState.Maximized);
        if (placement == keptPlacement) return;
        try
        {
            if (store.SavePlacement(placement)) keptPlacement = placement;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Where the window stood is a convenience: without it, the window opens where the system places it.
        }
    }

    /// <summary>
    /// Applies a theme, or the system's when null, and keeps the choice in the settings file. A file that cannot be
    /// written leaves the theme applied for this run, and the menu says why.
    /// </summary>
    internal void ChooseTheme(ThemeMode? choice)
    {
        (Avalonia.Application.Current as App)?.ChooseTheme(choice);
        settingsProblem = null;
        settings = settings with { Theme = choice };
        if (settingsStore is { } store)
        {
            try
            {
                settings = store.SaveTheme(choice);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException)
            {
                settingsProblem = "This choice lasts until InterCat closes: " + exception.Message;
            }
        }

        UpdateThemeMenu();
    }

    private void UpdateThemeMenu()
    {
        if (ThemeButton.Flyout is not MenuFlyout menu) return;
        menu.Items.Clear();
        foreach ((ThemeMode? mode, string label) in ThemeChoices)
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "theme",
                IsChecked = settings.Theme == mode,
            };
            item.Click += (_, _) => ChooseTheme(mode);
            Avalonia.Automation.AutomationProperties.SetName(item, label + " theme");
            _ = menu.Items.Add(item);
        }

        _ = menu.Items.Add(new Separator());
        string kept = settingsStore is { } store
            ? "Kept in " + store.FilePath
            : "No settings file is in use: a choice lasts until InterCat closes.";
        foreach (string line in settings.Notices.Prepend(kept).Append(settingsProblem).OfType<string>())
        {
            _ = menu.Items.Add(new MenuItem { Header = line, IsEnabled = false });
        }
    }

    /// <summary>
    /// Takes the user's session folder: lists the sessions saved in it while none is open, and offers to finish the
    /// newest capture an earlier viewer left unfinished there (§3.1). The application calls it once at launch; a test
    /// calls it with a folder of its own. A capture whose session is already whole is let go without a word.
    /// </summary>
    internal async Task UseSessionRootAsync(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        sessionRoot = root;
        await RefreshRecentSessionsAsync();
        await RefreshInterruptedOfferAsync();
    }

    /// <summary>
    /// Reads the saved sessions again. A list that has not changed is left as it is, scrolled where it was; one that
    /// has keeps the keyboard on the row it was on while that is still listed, else on the newest, and gives it where
    /// the window does once none is.
    /// </summary>
    private async Task RefreshRecentSessionsAsync()
    {
        if (closed || sessionRoot is not { } root) return;
        IReadOnlyList<RecentSessionRow> rows = await Task.Run(() => RecentSessions.Find(
            root, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture));
        if (closed) return;
        if (!rows.SequenceEqual(recentSessions))
        {
            string? keyboardOn = FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: RecentSessionRow row }
                ? row.Path
                : null;
            recentSessions = rows;
            RecentSessionsList.ItemsSource = rows;

            // Once none is listed, the row's going gives the keyboard where the window gives what a control leaves.
            if (keyboardOn is not null)
            {
                RecentSessionRow? still = rows.FirstOrDefault(listed => listed.Path == keyboardOn);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!closed) _ = FocusRow(RecentSessionsList, still);
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        UpdateBeforeSessionVisibility();
    }

    /// <summary>Completes once the session folder, read again as the window last came back into use, is read.</summary>
    internal Task FolderRead { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads the session folder again as the window comes back into use, so the saved sessions, while it lists them,
    /// and an unfinished capture's card say what the folder holds now: a session moved, deleted, saved or finished
    /// elsewhere meanwhile is found so here.
    /// </summary>
    private async Task ReadFolderAgainAsync()
    {
        if (closed || sessionRoot is null) return;
        if (BeforeSession) await RefreshRecentSessionsAsync();
        await RefreshInterruptedOfferAsync();
    }

    /// <summary>The least room the rail's list keeps beside its actions: a row or two of ranked rows or saved sessions.</summary>
    internal const double RailListRoom = 120;

    /// <summary>The least height the rail's actions keep, however little the list leaves them.</summary>
    internal const double RailActionsFloor = 200;

    /// <summary>
    /// How tall the rail's actions may be: what the rail leaves below its heading and ranked table's header, less the list's
    /// least room, and never less than <see cref="RailActionsFloor"/>. Beyond it they scroll rather than run past the window.
    /// </summary>
    internal static double RailActionsHeight(double rail, double above) =>
        Math.Max(RailActionsFloor, rail - above - RailListRoom);

    private void FitRailActions()
    {
        double above = RailGrid.RowDefinitions[0].ActualHeight + RailGrid.RowDefinitions[1].ActualHeight;
        RailActions.MaxHeight = RailActionsHeight(RailGrid.Bounds.Height, above) - RailActions.Margin.Top;
    }

    /// <summary>The saved sessions show only while no session is open and no capture is running, where the ranked table
    /// will be.</summary>
    /// <summary>
    /// What the rail offers only before any session is shown and while no capture runs: the saved sessions, the card's
    /// heading and what exploring records, and the demo to try (§14 M5). Once a session is shown the ranked list needs the
    /// rail's height - three rows at the smallest window - and the demo waits in the Investigation menu.
    /// </summary>
    private void UpdateBeforeSessionVisibility()
    {
        bool first = BeforeSession;
        RecentSessionsPanel.IsVisible = recentSessions.Count > 0 && first;
        CaptureEyebrow.IsVisible = first;
        CaptureIntro.IsVisible = first;
        ExploreDemoButton.IsVisible = first;
    }

    /// <summary>Whether no session is shown and no capture runs, when the rail offers the saved sessions.</summary>
    private bool BeforeSession => workspace.IsEmptyWorkspace
        && phase is not (CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing);

    private void OpenRecentSession(object? sender, TappedEventArgs eventArgs) => _ = OpenSelectedRecentSessionAsync();

    private void OpenCellRecord(object? sender, TappedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            OpenSelectedCellRecord(viewModel);
        }
    }

    /// <summary>
    /// Opens in E the chosen cell's record the keyboard is on, or else the one selected, selected there (§6.4); E's table
    /// then has the keyboard, on that record once its page arrives. False when there is none.
    /// </summary>
    internal bool OpenSelectedCellRecord(WorkspaceViewModel viewModel)
    {
        CellRecordRow? row = (FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as CellRecordRow
            ?? CellRecordsList.SelectedItem as CellRecordRow;
        if (row is null || !viewModel.OpenCellRecord(row))
        {
            return false;
        }

        FocusRail();
        return true;
    }

    /// <summary>
    /// Opens the recent session the keyboard is on, or else the one selected: Tab reaches a row without selecting it. The
    /// row opens the session it describes, never another written into its folder since: a session gone from its folder
    /// since the rail was read is said gone, and the list read again.
    /// </summary>
    internal async Task OpenSelectedRecentSessionAsync()
    {
        RecentSessionRow? row = (FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as RecentSessionRow
            ?? RecentSessionsList.SelectedItem as RecentSessionRow;
        if (row is null)
        {
            return;
        }

        // The rail listed what each folder held when it was read: a session moved, emptied, deleted or written over since
        // is said gone as its row named it, and the list is read again, so it lists what the folders hold now. Any other
        // reason it did not open stands as the open said it, beside the row it named.
        string? refused = await OpenSessionOrSayWhyNotAsync(row.Path, null, row.SessionId);
        if (closed || refused is not (SessionStore.SessionGone or SessionStore.AnotherSession))
        {
            return;
        }

        CaptureDetail.Text = $"{row.Title}, {row.Detail}, is no longer in its folder: its files were moved or deleted"
            + (refused is SessionStore.AnotherSession ? "; another session is there now." : ".")
            + " The current workspace is unchanged.";

        // The keyboard that chose the row stays with the saved sessions, on the newest still listed, or goes where the
        // window gives it once none is.
        await RefreshRecentSessionsAsync();
    }

    /// <summary>How the window looks for an unfinished capture in the session folder; a test holds one look back with it.</summary>
    internal Func<string, InterruptedFollow?> InterruptedCaptureFinder { get; set; } = FindInterruptedCapture;

    /// <summary>
    /// How the window finishes an unfinished capture's session from its evidence, off its thread; a test holds one
    /// partway, as a long capture's is.
    /// </summary>
    internal Func<LiveFollowTicket, IProgress<FollowStep>, CancellationToken, InterruptedFollowResult> CaptureFinisher
    {
        get;
        set;
    } = (ticket, progress, cancellation) => InterruptedFollow.Finish(ticket, progress, cancellationToken: cancellation);

    /// <summary>Looks again for an unfinished capture, as the recheck's timer does, and shows what the latest look found.</summary>
    internal async Task RefreshInterruptedOfferAsync()
    {
        if (closed || sessionRoot is not { } root || finishingCapture is not null) return;
        int refresh = ++offerRefresh;
        Func<string, InterruptedFollow?> find = InterruptedCaptureFinder;
        InterruptedFollow? found;
        try
        {
            found = await Task.Run(() => find(root));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The session folder cannot be read just now: nothing is offered, and nothing is lost.
            found = null;
        }

        if (refresh == offerRefresh && !closed && finishingCapture is null) ShowInterruptedOffer(found);
    }

    /// <summary>The newest capture with something to offer. One whose session is already whole loses its ticket.</summary>
    private static InterruptedFollow? FindInterruptedCapture(string root)
    {
        foreach (LiveFollowTicket ticket in LiveFollowTicket.FindInterrupted(root))
        {
            InterruptedFollow follow = InterruptedFollow.Assess(ticket);
            if (follow.State != InterruptedFollowState.Complete) return follow;
            _ = LiveFollowTicket.Remove(ticket.SessionDirectory);
        }

        return null;
    }

    private void ShowInterruptedOffer(InterruptedFollow? follow)
    {
        offeredFollow = follow;
        offer = follow is null
            ? null
            : InterruptedCaptureOffer.For(follow, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        UnfinishedCaptureCard.IsVisible = offer is not null
            && phase is not (CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing);
        KeepKeyboard();
        if (offer is null)
        {
            offerRecheck.Stop();
            return;
        }

        UnfinishedCaptureHeadline.Text = offer.Headline;
        UnfinishedCaptureDetail.Text = offer.Detail;
        FinishCaptureButton.IsVisible = offer.Action != InterruptedCaptureAction.None;
        FinishCaptureButton.Content = offer.ActionLabel;
        FinishCaptureButton.IsEnabled = true;
        Avalonia.Automation.AutomationProperties.SetName(FinishCaptureButton, offer.ActionLabel + ": " + offer.Headline);
        ForgetCaptureButton.Content = offer.ForgetLabel;
        ForgetCaptureButton.IsEnabled = true;
        if (offer.Rechecks) offerRecheck.Start();
        else offerRecheck.Stop();
    }

    private void FinishInterruptedCapture(object? sender, RoutedEventArgs eventArgs) => _ = ActOnInterruptedCaptureAsync();

    private void ForgetInterruptedCapture(object? sender, RoutedEventArgs eventArgs) => _ = ForgetInterruptedCaptureAsync();

    /// <summary>
    /// The card's action: finish the session from the evidence the broker kept, or open what was saved when the evidence
    /// is gone. While a finish runs the same button stops it; the session keeps what it derived either way.
    /// </summary>
    internal async Task ActOnInterruptedCaptureAsync()
    {
        if (finishingCapture is { } running)
        {
            FinishCaptureButton.IsEnabled = false;
            await running.CancelAsync();
            return;
        }

        if (offeredFollow is not { } follow || offer is not { } shown || openingSession
            || captureTask is { IsCompleted: false })
        {
            return;
        }

        if (shown.Action == InterruptedCaptureAction.Open)
        {
            // What was saved opens as the session the card described, never one written into its folder since.
            string? refused = await OpenSessionOrSayWhyNotAsync(
                follow.Ticket.SessionDirectory, ("Partial session open", shown.Detail), follow.SessionId);
            if (closed) return;
            if (refused is null)
            {
                // Nothing further can be derived for it: once seen, it is not offered again.
                _ = LiveFollowTicket.Remove(follow.Ticket.SessionDirectory);
            }
            else if (refused is SessionStore.SessionGone or SessionStore.AnotherSession)
            {
                CaptureDetail.Text = InterruptedCaptureOffer.SessionGone(follow, refused is SessionStore.AnotherSession,
                        DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture)
                    + " The current workspace is unchanged.";
            }

            // The card is read again either way, so it offers what is left of the capture now, or goes.
            await RefreshInterruptedOfferAsync();
            return;
        }

        if (shown.Action != InterruptedCaptureAction.Finish) return;
        using var finishing = new CancellationTokenSource();
        finishingCapture = finishing;
        offerRecheck.Stop();
        ShowFinishing(follow, step: null);
        var progress = new Progress<FollowStep>(step =>
        {
            if (!closed && finishingCapture == finishing) ShowFinishing(follow, step);
        });
        try
        {
            Task<InterruptedFollowResult> work = Task.Run(() =>
            {
                InterruptedFollowResult finished = CaptureFinisher(follow.Ticket, progress, finishing.Token);

                // A finished session is complete, so its derivation checkpoint lets it reopen without reading every
                // record; one that is not published only costs that reopen time (derivation-checkpoint-v1).
                if (finished.Completed)
                {
                    _ = SessionCheckpoints.Publish(finished.Session, DateTimeOffset.UtcNow, finishing.Token);
                }

                return finished;
            });
            finishWork = (work, CloseStop.Finish(follow.Ticket.SessionDirectory));
            InterruptedFollowResult result = await work;

            // The finish's store becomes the session's shared store, so opening the session hashes nothing again.
            SharedSessionStores.Registry.Adopt(follow.Ticket.SessionDirectory, result.Session);
            EndFinishing();
            if (closed) return;
            UnfinishedCaptureCard.IsVisible = false;
            _ = await OpenSessionAsync(
                follow.Ticket.SessionDirectory, InterruptedCaptureOffer.Finished(result, CultureInfo.CurrentCulture));
            await RefreshInterruptedOfferAsync();
        }
        catch (OperationCanceledException)
        {
            EndFinishing();
            await RefreshInterruptedOfferAsync();
            if (UnfinishedCaptureCard.IsVisible)
            {
                UnfinishedCaptureDetail.Text = "Finishing stopped, and what it derived is kept. " + UnfinishedCaptureDetail.Text;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            EndFinishing();
            if (closed) return;
            await SayWhyFinishingDidNotGoAsync(follow, exception.Message);
        }
    }

    /// <summary>
    /// Reads the unfinished capture's card again once finishing it did not go, so it offers what can be done now, and
    /// says why: on the card while it offers the capture as it did, and where the status is read once it no longer
    /// offers it, as when another window finished or forgot it. A card that offers the capture otherwise now - one
    /// whose evidence went, which can no longer be finished - says what changed itself.
    /// </summary>
    private async Task SayWhyFinishingDidNotGoAsync(InterruptedFollow follow, string reason)
    {
        await RefreshInterruptedOfferAsync();
        if (closed) return;
        if (offeredFollow is not { } now || !string.Equals(
                now.Ticket.SessionDirectory, follow.Ticket.SessionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            CaptureStatus.Text = "Could not finish this capture";
            CaptureDetail.Text = reason;
        }
        else if (now.State == follow.State)
        {
            UnfinishedCaptureDetail.Text = reason + " Your session keeps what was derived before this.";
        }
    }

    /// <summary>Forgets the offered capture: its ticket goes, and the session so far and the evidence stay.</summary>
    internal async Task ForgetInterruptedCaptureAsync()
    {
        if (offeredFollow is not { } follow || finishingCapture is not null) return;
        if (!LiveFollowTicket.Remove(follow.Ticket.SessionDirectory))
        {
            UnfinishedCaptureDetail.Text = "Another InterCat window is following this capture, so it is not forgotten here. "
                + offer?.Detail;
            return;
        }

        await RefreshInterruptedOfferAsync();
    }

    private void ShowFinishing(InterruptedFollow follow, FollowStep? step)
    {
        (string headline, string detail) = InterruptedCaptureOffer.Finishing(
            follow, step, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        UnfinishedCaptureHeadline.Text = headline;
        UnfinishedCaptureDetail.Text = detail;
        FinishCaptureButton.Content = "Stop finishing";
        Avalonia.Automation.AutomationProperties.SetName(
            FinishCaptureButton, "Stop finishing; the session keeps what is derived");
        ForgetCaptureButton.IsEnabled = false;
        StartExploringButton.IsEnabled = false;
        OpenSavedSessionButton.IsEnabled = false;
    }

    private void EndFinishing()
    {
        finishingCapture = null;
        finishWork = null;
        if (closed) return;
        StartExploringButton.IsEnabled = true;
        OpenSavedSessionButton.IsEnabled = true;
        FinishCaptureButton.IsEnabled = true;
        ForgetCaptureButton.IsEnabled = true;
    }

    /// <summary>
    /// Shares the open session as a redacted package (§11.3): what it keeps and leaves out is stated first, the user
    /// chooses where the new folder goes, and the finished package - verified before it appears - can be opened here to
    /// review what a recipient will see. While a package is being made, the same button cancels it.
    /// </summary>
    private async void SharePackage(object? sender, RoutedEventArgs eventArgs)
    {
        if (packaging is { } running)
        {
            running.Cancel();
            return;
        }

        if (!await FilesCheckedFirstAsync() || currentSessionPath is not { } source
            || displayedOverview is not { } overview || IsLive || exporting)
        {
            return;
        }

        if (await ConfirmRedactedPackageAsync(overview, workspace.ScopeInterval) is not { } chosen) return;
        string? parent = await Choices.OpenFolderAsync(new()
        {
            Title = "Choose where to save the redacted session package",
            AllowMultiple = false,
        });
        if (parent is null || closed) return;
        string destination = NewPackageDirectory(parent, DateTimeOffset.Now);
        RedactedSessionPackageResult? result = await WriteRedactedPackageAsync(source, destination, chosen.Interval);
        if (result is not null && !closed && await ShowPackageResultAsync(result))
        {
            _ = await OpenSessionAsync(result.Directory);
        }
    }

    /// <summary>
    /// Shares the open session as an original evidence package (§11.3): an exact, unredacted copy that reopens as the same
    /// session. What it holds is stated first, from the files themselves; the user chooses where the new folder goes; and
    /// the finished package, verified before it appears, can be opened here. While it is made, the same button cancels it.
    /// </summary>
    private async void ShareOriginal(object? sender, RoutedEventArgs eventArgs)
    {
        if (packaging is { } running)
        {
            running.Cancel();
            return;
        }

        if (!await FilesCheckedFirstAsync() || currentSessionPath is not { } source
            || displayedOverview is not { } shown || IsLive || exporting)
        {
            return;
        }

        // The shown session is what is packaged: a folder that now holds another is refused, never packaged as it.
        Guid sessionId = shown.SessionId;
        OriginalEvidencePackagePreview preview;
        try
        {
            preview = await Task.Run(() => OriginalEvidencePackage.Preview(SharedSessionStores.Open(source, sessionId)));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            CaptureDetail.Text = "Could not read the session to package it: " + exception.Message;
            return;
        }

        if (closed || !await ConfirmOriginalPackageAsync(preview)) return;
        string? parent = await Choices.OpenFolderAsync(new()
        {
            Title = preview.Redacted
                ? "Choose where to save a copy of this redacted package"
                : "Choose where to save the original, unredacted session",
            AllowMultiple = false,
        });
        if (parent is null || closed) return;
        string destination = NewPackageDirectory(parent, DateTimeOffset.Now,
            preview.Redacted ? "intercat-redacted-session" : "intercat-original-session");
        OriginalEvidencePackageResult? result = await WriteOriginalPackageAsync(source, sessionId, destination,
            preview.Redacted);
        if (result is not null && !closed && await ShowOriginalResultAsync(result))
        {
            _ = await OpenSessionAsync(result.Directory);
        }
    }

    /// <summary>How the window makes an exact copy of a session, off its thread; a test holds one partway.</summary>
    internal Func<SessionStore, string, IProgress<OriginalPackageProgress>, CancellationToken,
        OriginalEvidencePackageResult> OriginalPackager { get; set; } = OriginalEvidencePackage.Create;

    /// <summary>How the window makes a redacted package, off its thread; a test holds one partway.</summary>
    internal Func<SessionStore, string, DateTimeOffset, TimeRange?, IProgress<RedactedPackageProgress>,
        CancellationToken, RedactedSessionPackageResult> RedactedPackager { get; set; } = RedactedSessionPackage.Create;

    /// <summary>
    /// Runs a package's work off the window's thread, kept with what the question asked before a close says of it: a
    /// close that stops the package waits until the work has ended and its unsaved folder is removed.
    /// </summary>
    private Task<T> PackageWork<T>(Func<T> make, CloseStop stop, CancellationToken cancellation)
    {
        Task<T> work = Task.Run(make, cancellation);
        packageWork = (work, stop);
        return work;
    }

    /// <summary>
    /// Waits, before a package of the shown session is asked for, until its files are checked against what was published
    /// (store-v1 §6), saying so meanwhile: a file changed since is found first, and the session opened again from its last
    /// complete generation, so what is packaged is what the check left shown. A second ask while one waits is the same
    /// ask. False when it is, or when the window closed meanwhile.
    /// </summary>
    private async Task<bool> FilesCheckedFirstAsync()
    {
        if (SessionFilesChecked.IsCompleted) return !closed;
        if (awaitingFilesCheck) return false;
        awaitingFilesCheck = true;
        try
        {
            Say(CaptureDetail, "The session's files are being checked against what was published; the package is asked "
                + "for once they are.", aloud: true);
            await SessionFilesChecked;
        }
        finally
        {
            awaitingFilesCheck = false;
        }

        return !closed;
    }

    /// <summary>
    /// Makes the original package of session <paramref name="sessionId"/> off the UI thread, stating its progress on the
    /// capture card, and returns null when it was cancelled or refused - the card then says which, and the source session
    /// is unchanged either way. A folder that now holds another session is refused rather than packaged.
    /// </summary>
    internal async Task<OriginalEvidencePackageResult?> WriteOriginalPackageAsync(string source, Guid sessionId,
        string destination, bool redacted = false)
    {
        if (packaging is not null) return null;
        using var cancellation = new CancellationTokenSource();
        packaging = cancellation;
        packagingOriginal = true;
        UpdateEvidenceAction();
        string headline = CaptureStatus.Text ?? string.Empty;
        CaptureStatus.Text = redacted ? "Copying this redacted package" : "Saving the original session";
        CaptureDetail.Text = "Copying and checking the session's files…";
        var progress = new Progress<OriginalPackageProgress>(update =>
        {
            if (closed || packaging != cancellation) return;
            string stage = update.Stage == OriginalPackageStage.Copying
                ? "Copying and checking the session's files"
                : "Reopening and verifying the package";
            // The percent is shown, and said aloud by no screen reader, which would say every one of them (R15).
            Say(CaptureDetail, update.Total > 0 ? $"{stage}… {update.Done * 100 / update.Total}%" : stage + "…",
                aloud: false);
        });
        try
        {
            // Each file is hashed as it is copied, so the session's shared store, which listed its files, is enough.
            OriginalEvidencePackageResult result = await PackageWork(() => OriginalPackager(
                    SharedSessionStores.Open(source, sessionId), destination, progress, cancellation.Token),
                CloseStop.Package(destination, copy: true), cancellation.Token);
            if (!closed)
            {
                CaptureStatus.Text = result.Source.Redacted ? "Package copy saved" : "Original session saved";
                CaptureDetail.Text = $"Saved an exact copy of generation {result.Source.Generation:N0} to {result.Directory}. "
                    + "It was reopened and checked before it was published. "
                    + (result.Source.Redacted
                        ? "It is the redacted package as it was: its pseudonyms, never the original values."
                        : "It is unredacted.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                CaptureStatus.Text = headline;
                CaptureDetail.Text = "Copying cancelled. Nothing was saved, and the session is unchanged.";
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                CaptureStatus.Text = headline;
                CaptureDetail.Text = (redacted ? "Could not copy the package: " : "Could not save the original session: ")
                    + exception.Message;
            }

            return null;
        }
        finally
        {
            packaging = null;
            packageWork = null;
            packagingOriginal = false;
            if (!closed) UpdateEvidenceAction();
        }
    }

    private async Task<bool> ConfirmOriginalPackageAsync(OriginalEvidencePackagePreview preview) =>
        await OriginalPackagePrompt(preview).ShowDialog<bool>(this);

    /// <summary>
    /// The confirmation an original package needs: what it holds, stated whole. It is as tall as what it states, so its
    /// actions are never cut off, and it starts on Cancel, so a reflexive Enter saves nothing.
    /// </summary>
    internal static Window OriginalPackagePrompt(OriginalEvidencePackagePreview preview)
    {
        var prompt = new Window
        {
            Title = preview.Redacted ? "Share this redacted package?" : "Share the original, unredacted session?", Width = 600,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var cancel = new Button { Name = "CancelOriginalPackage", Content = "Cancel" };
        var proceed = new Button { Name = "SaveOriginalPackage", Content = preview.Redacted ? "Save a copy…" : "Save unredacted copy…" };
        cancel.Click += (_, _) => prompt.Close(false);
        proceed.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => cancel.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        var content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        foreach (string text in OriginalPackageDisclosure(preview))
        {
            TextBlock paragraph = Paragraph(text);
            if (text == OriginalEvidencePackage.WarningFor(preview))
            {
                paragraph.FontWeight = Avalonia.Media.FontWeight.SemiBold;
            }

            content.Children.Add(paragraph);
        }

        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, proceed },
        });
        prompt.Content = content;
        return prompt;
    }

    /// <summary>What the confirmation states before an original package is saved: what it is, holds, and exposes (§11.3).</summary>
    internal static IReadOnlyList<string> OriginalPackageDisclosure(OriginalEvidencePackagePreview preview)
    {
        int journals = preview.Journals.Count;
        string journalKind = preview.Redacted ? "journal" : "raw journal";
        string recordKind = preview.Redacted ? "synthetic records" : "admitted records";

        // A redacted package's copy is that package as it is: it is never called original or unredacted.
        var paragraphs = new List<string>
        {
            preview.Redacted
                ? "This saves an exact copy of this redacted package in a new folder, which opens in InterCat as the same "
                    + "package. The package you have open is not changed."
                : "This saves an exact copy of this session's evidence in a new folder, which opens in InterCat as the same "
                    + "session. The session you have open is not changed.",
            string.Create(CultureInfo.CurrentCulture,
                $"It holds generation {preview.Generation:N0}: {Spoken.Count(preview.Rows, "record")}, {journals:N0} {journalKind} "
                + $"{(journals == 1 ? "file" : "files")} of {recordKind}, and {preview.Files.Count:N0} files in all "
                + $"({RecentSessions.Size(preview.Bytes, CultureInfo.CurrentCulture)})."),
            preview.Redacted ? OriginalEvidencePackage.RedactedContents : "Unredacted: " + OriginalEvidencePackage.Contents,
            preview.HostId is { } host
                ? preview.Redacted
                    ? $"Hosts: one, the package's own pseudonymous host, identified as {host:N}."
                    : $"Hosts: one, the capture's own, identified as {host:N}."
                : "Hosts: no journal names the capture's host.",
            OriginalEvidencePackage.WarningFor(preview),
        };
        return paragraphs;
    }

    private async Task<bool> ShowOriginalResultAsync(OriginalEvidencePackageResult result) =>
        await OriginalResultPrompt(result).ShowDialog<bool>(this);

    /// <summary>Says where the copy is and what verified it; true when the user wants to open it here.</summary>
    internal static Window OriginalResultPrompt(OriginalEvidencePackageResult result)
    {
        var prompt = new Window
        {
            Title = result.Source.Redacted ? "Package copy saved" : "Original session saved", Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var done = new Button { Content = "Done" };
        var open = new Button { Content = "Open it here" };
        done.Click += (_, _) => prompt.Close(false);
        open.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => done.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                Paragraph(string.Create(CultureInfo.CurrentCulture,
                    $"Saved an exact copy of generation {result.Source.Generation:N0} ({result.Source.Files.Count:N0} files, "
                    + $"{RecentSessions.Size(result.Source.Bytes, CultureInfo.CurrentCulture)}) to {result.Directory}.")),
                Paragraph("Each file was checked against the digest its generation recorded as it was copied, and the "
                    + "package was reopened and hashed as a recipient would open it before it was saved."),
                Paragraph(OriginalEvidencePackage.WarningFor(result.Source)),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { done, open } },
            },
        };
        return prompt;
    }

    /// <summary>A new folder name under the chosen one: dated, and numbered rather than reusing an existing name.</summary>
    internal static string NewPackageDirectory(string parent, DateTimeOffset now, string name = "intercat-redacted-session")
    {
        string stem = Path.Combine(parent, $"{name}-{now:yyyyMMdd-HHmmss}");
        string candidate = stem;
        for (int attempt = 2; Directory.Exists(candidate) || File.Exists(candidate); attempt++)
        {
            candidate = $"{stem}-{attempt}";
        }

        return candidate;
    }

    /// <summary>
    /// Makes the package off the UI thread, stating its progress on the capture card, and returns null when it was
    /// cancelled or refused - the card then says which, and the source session is unchanged either way.
    /// </summary>
    internal async Task<RedactedSessionPackageResult?> WriteRedactedPackageAsync(string source, string destination,
        TimeRange? interval = null)
    {
        if (packaging is not null) return null;
        using var cancellation = new CancellationTokenSource();
        packaging = cancellation;
        UpdateEvidenceAction();
        string headline = CaptureStatus.Text ?? string.Empty;
        CaptureStatus.Text = "Creating redacted session package";
        CaptureDetail.Text = "Reading the session's rows…";
        var progress = new Progress<RedactedPackageProgress>(update =>
        {
            if (closed || packaging != cancellation) return;
            string stage = update.Stage switch
            {
                RedactedPackageStage.Selecting => "Finding the time scope's records and their processes",
                RedactedPackageStage.Inspecting => "Reading the session's rows",
                RedactedPackageStage.Writing => "Writing pseudonymized rows",
                _ => "Reopening and verifying the package",
            };
            // The percent is shown, and said aloud by no screen reader, which would say every one of them (R15).
            Say(CaptureDetail, update.Total > 0 ? $"{stage}… {update.Done * 100 / update.Total}%" : stage + "…",
                aloud: false);
        });
        try
        {
            RedactedSessionPackageResult result = await PackageWork(() => RedactedPackager(
                    SessionStore.OpenExisting(LocalOwnedDirectory.Open(source)), destination, DateTimeOffset.UtcNow,
                    interval, progress, cancellation.Token),
                CloseStop.Package(destination, copy: false), cancellation.Token);
            if (!closed)
            {
                CaptureStatus.Text = "Redacted session package saved";
                CaptureDetail.Text = $"Saved {Spoken.Count(result.Counts.Rows, "record")} to {result.Directory}. It was reopened and "
                    + "checked before it was published. Review it before sharing it.";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                CaptureStatus.Text = headline;
                CaptureDetail.Text = "Packaging cancelled. Nothing was saved, and the session is unchanged.";
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                CaptureStatus.Text = headline;
                CaptureDetail.Text = "Could not create the redacted package: " + exception.Message;
            }

            return null;
        }
        finally
        {
            packaging = null;
            packageWork = null;
            if (!closed) UpdateEvidenceAction();
        }
    }

    private async Task<RedactedPackageScope?> ConfirmRedactedPackageAsync(SessionOverviewBundle overview, TimeRange? scope) =>
        await RedactedPackagePrompt(overview, scope).ShowDialog<RedactedPackageScope?>(this);

    /// <summary>What a person chose to share: the whole session, or the interval of its time scope (redacted-session-v1 §11).</summary>
    internal sealed record RedactedPackageScope(TimeRange? Interval);

    /// <summary>
    /// What a redacted session package keeps, replaces and leaves out, asked before its folder is chosen. With a time scope
    /// - a brushed or kept range, or the view zoomed in - it offers that interval first, in the words its readers will
    /// use; a session above the row bound can be shared only an interval at a time, and says so.
    /// </summary>
    internal static Window RedactedPackagePrompt(SessionOverviewBundle overview, TimeRange? scope = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var prompt = new Window
        {
            Title = "Share a redacted session package?", Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        bool tooLarge = overview.ObservationRows > RedactedSessionPackage.MaximumRows;
        var part = new RadioButton
        {
            GroupName = "package-scope", IsChecked = scope is not null, IsVisible = scope is not null,
            Content = scope is { } range
                ? $"Only its time scope, from {SessionRedaction.Seconds(range.StartTicks, CultureInfo.CurrentCulture)} to "
                    + SessionRedaction.Seconds(range.EndTicks, CultureInfo.CurrentCulture)
                : string.Empty,
        };
        var whole = new RadioButton
        {
            GroupName = "package-scope", Content = "The whole session", IsChecked = scope is null && !tooLarge,
            IsEnabled = !tooLarge, IsVisible = scope is not null || tooLarge,
        };
        TextBlock kept = Paragraph(string.Empty);
        var cancel = new Button { Content = "Cancel" };
        var proceed = new Button { Content = "Choose a folder…" };
        void Describe()
        {
            bool interval = part.IsChecked == true && scope is not null;
            kept.Text = interval
                ? "Kept: every record in that interval, with its time, size, status and quality, and coverage and loss there. "
                    + SessionRedaction.Holds(new() { StartTicks = scope!.Value.StartTicks, EndTicks = scope.Value.EndTicks,
                        LifecycleRowsOutside = 0 }, CultureInfo.CurrentCulture)
                : $"Kept: every record, {overview.ObservationRows:N0} in all, with its time, size, status and quality; "
                    + $"every process, {overview.Nodes.Count:N0} in all, and how they relate; coverage and loss.";
            // A session over the bound with no interval chosen keeps nothing yet, and says what to do instead.
            proceed.IsEnabled = interval || !tooLarge;
            kept.IsVisible = proceed.IsEnabled;
        }

        part.IsCheckedChanged += (_, _) => Describe();
        whole.IsCheckedChanged += (_, _) => Describe();
        Describe();
        cancel.Click += (_, _) => prompt.Close(null);
        proceed.Click += (_, _) => prompt.Close(new RedactedPackageScope(part.IsChecked == true ? scope : null));
        prompt.Opened += (_, _) => cancel.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(null);
                key.Handled = true;
            }
        };
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                Paragraph("This saves a new session folder that opens in InterCat, for someone else to explore. The "
                    + "session you have open is not changed."),
                new StackPanel { Spacing = 6, IsVisible = part.IsVisible || whole.IsVisible, Children = { part, whole } },
                new TextBlock
                {
                    Text = $"This session holds {overview.ObservationRows:N0} records, and a package holds at most "
                        + $"{RedactedSessionPackage.MaximumRows:N0}, so it is shared an interval at a time: brush or zoom to "
                        + "one in the timeline first.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = tooLarge, Classes = { "caution" },
                },
                kept,
                Paragraph("Replaced by random pseudonyms: executable, pipe and resource names; process and thread IDs; "
                    + "addresses and ports; activity and interface identifiers; third-party providers."),
                Paragraph("Left out: the original journal with any record bodies and extended data, process start and "
                    + "exit clock times, and this session's own identities. Each record's original entry becomes a "
                    + "synthetic one."),
                Paragraph("This is not anonymous: timing, sizes and the shape of the workload can still identify a "
                    + "system. The package is checked before it is saved; review it before sharing it."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, proceed } },
            },
        };
        return prompt;
    }

    private async Task<bool> ShowPackageResultAsync(RedactedSessionPackageResult result) =>
        await RedactedPackageResultPrompt(result).ShowDialog<bool>(this);

    /// <summary>Says where the package is and what verified it; true when the user wants to open it here to review.</summary>
    internal static Window RedactedPackageResultPrompt(RedactedSessionPackageResult result)
    {
        var prompt = new Window
        {
            Title = "Redacted session package saved", Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var done = new Button { Content = "Done" };
        var open = new Button { Content = "Open it here to review" };
        done.Click += (_, _) => prompt.Close(false);
        open.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => open.Focus();
        prompt.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape)
            {
                prompt.Close(false);
                key.Handled = true;
            }
        };
        string journals = result.Source.SourceJournals == 1 ? "journal file" : "journal files";
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                Paragraph($"Saved {Spoken.Count(result.Counts.Rows, "record")} to {result.Directory}."
                    + (result.Source.Interval is { } interval
                        ? " " + SessionRedaction.Holds(interval, CultureInfo.CurrentCulture)
                        : string.Empty)
                    + (result.Source.ReleasedBeforeNanoseconds is { } released
                        ? " " + SessionRedaction.Released(released, CultureInfo.CurrentCulture)
                        : string.Empty)),
                Paragraph($"Before it was saved, the package was reopened as a recipient would open it, every value "
                    + $"was checked against the pseudonyms it issued, and {CountText.Of(result.FilesVerified, "file")} "
                    + $"{CountText.Agree(result.FilesVerified, "was", "were")} searched "
                    + "for this session's identities and names. None was found."),
                Paragraph($"Left behind: {result.Source.SourceJournals:N0} original {journals} "
                    + $"({RecentSessions.Size(result.Source.SourceJournalBytes, CultureInfo.CurrentCulture)})"
                    + (result.Source.SourceContentChunks > 0
                        ? $", and the message content the capture kept ({RecentSessions.Size(result.Source.SourceContentBytes, CultureInfo.CurrentCulture)})"
                        : string.Empty)
                    + ". Opening the package here shows what a recipient will see."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { done, open } },
            },
        };
        return prompt;
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    /// <summary>
    /// Asks what a content capture keeps - the person's own processes, its limits, the consent to see what is kept - and
    /// starts it, to be reviewed as the broker prepares it before anything is recorded (§11.1, ADR-049).
    /// </summary>
    private async void ChooseContentCapture()
    {
        if (openingSession || finishingCapture is not null || captureTask is { IsCompleted: false })
        {
            return;
        }

        IReadOnlyList<SeenProcess> running = await Task.Run(BrokerContentReview.Running);
        var chooser = new ContentCaptureWindow(running, BrokerContentReview.Running);
        if (await chooser.ShowDialog<bool>(this) && !closed && chooser.Chosen is { } chosen)
        {
            BeginCapture(chosen);
        }
    }

    /// <summary>Starts a content capture already chosen, as the chooser's Review does; a test uses it.</summary>
    internal void BeginContentCapture(ContentCaptureChoice chosen) => BeginCapture(chosen);

    /// <summary>Shows a content capture's review as the broker prepared it; true starts it.</summary>
    private Task<bool> ReviewContent(BrokerEffectiveCaptureSummary summary, ContentCaptureChoice chosen) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
            !closed && await new ContentCaptureReviewWindow(summary, chosen.Request.Mechanism, chosen.Seen).ShowDialog<bool>(this));

    /// <summary>
    /// How the window runs a capture off its thread, saying each phase it reaches; a test runs one that records until it
    /// is stopped, and saves when the test lets it.
    /// </summary>
    internal Func<Action<CaptureUiUpdate>, CancellationToken, CaptureRunOptions, Task> CaptureRunner { get; set; } =
        DesktopCaptureRunner.RunAsync;

    private async void BeginCapture(ContentCaptureChoice? content = null)
    {
        if (HeldBy(start: true) is not null)
        {
            return;
        }

        captureStop?.Dispose();
        captureStop = new CancellationTokenSource();
        int run = ++captureRunId;
        ForgetDisplayedSession();
        evidencePolicy = EvidencePolicy.IncludeCorrelated;
        laneGrouping = LaneGrouping.Executable;
        collectorsAside = false;
        ApplyCaptureUpdate(new(CaptureUiPhase.Starting, content is null ? "Preparing Explore" : "Preparing the content capture",
            "Windows may ask for administrator approval to record system-wide events."));
        try
        {
            // Each publication is projected under the policy the window holds when it is projected, so a person's choice
            // made while recording holds from the next one on; the Keep choice says how long it records and what it keeps.
            // A content capture records for the length it was given, keeps every record, and is reviewed before it starts.
            CaptureRunOptions options = content is null
                ? KeepChoice.Options(() => evidencePolicy)
                : new()
                {
                    MaximumDurationSeconds = content.MaximumDurationSeconds,
                    EvidencePolicy = () => evidencePolicy,
                    Content = content,
                    Review = summary => ReviewContent(summary, content),
                };
            StartedWith = options;
            captureTask = Task.Run(() => CaptureRunner(update => ReceiveCaptureUpdate(run, update), captureStop.Token,
                options));
            await captureTask;
        }
        catch (Exception exception)
        {
            ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture interrupted",
                exception.Message + " Any published evidence is retained in the session shown below."));
        }
    }

    /// <summary>What the latest capture this window started was told: how long it records and what its session keeps.</summary>
    internal CaptureRunOptions? StartedWith { get; private set; }

    /// <summary>The latest capture this window started, which closing the window waits for while it runs; null before one.</summary>
    internal Task? CaptureRun => captureTask;

    /// <summary>What the next capture keeps, as the Explore card's Keep selector holds it; every record of ten minutes by default.</summary>
    internal CaptureKeepChoice KeepChoice => CaptureKeepSelector?.SelectedItem as CaptureKeepChoice ?? CaptureKeepChoice.All[0];

    /// <summary>Says beneath the selector what a choice that keeps a window records and keeps; the default needs no words.</summary>
    private void CaptureKeepChosen(object? sender, SelectionChangedEventArgs? eventArgs)
    {
        // The markup's first selection arrives while the window is still being built, before its named parts are.
        if (CaptureKeepNote is null || CaptureKeepSelector is null)
        {
            return;
        }

        CaptureKeepChoice choice = KeepChoice;
        CaptureKeepNote.Text = Explained(choice) ? "This capture " + choice.Explanation : string.Empty;
        CaptureKeepNote.IsVisible = Explained(choice);
        ToolTip.SetTip(CaptureKeepSelector, choice.Label + ": " + choice.Explanation);
        NameStart(unavailable: phase == CaptureUiPhase.Unavailable);
    }

    /// <summary>Whether the card says beneath the Keep choice what it does: every choice but the default does.</summary>
    private static bool Explained(CaptureKeepChoice choice) => choice.Keep is not null || choice.Content;

    /// <summary>
    /// Names the card's start action for the Keep choice: starting Explore, retrying it once it could not run, or choosing
    /// the processes whose messages a content capture keeps.
    /// </summary>
    private void NameStart(bool unavailable)
    {
        bool content = KeepChoice.Content;
        StartExploringButton.Content = content ? "Choose processes…"
            : unavailable && StartedWith?.Content is null ? "Retry exploring (Ctrl+R)" : "Start exploring (Ctrl+R)";
        AutomationProperties.SetHelpText(StartExploringButton, content
            ? "Choose your own processes whose HTTP messages a capture keeps (Ctrl+R); you review it before anything is recorded"
            : "Start the default Explore capture");
    }

    /// <summary>
    /// A new capture is a new session: the one shown so far stops being the one its updates are compared with, so its
    /// generations, hold, follow state and live preview are forgotten. Its view stays until the capture records.
    /// </summary>
    internal void ForgetDisplayedSession()
    {
        displayedSessionId = null;
        displayedGeneration = -1;
        currentSessionPath = null;
        heldUpdate = null;
        followLatest = true;
        displayedOverview = null;
        lastPublicationUtc = null;
        livePreview = null;
        displayedChunks = 0;
        growth = null;
        UpdateHeldBanner();
        UpdateEvidenceAction();
        CaptureSummary.Text = string.Empty;
        CaptureSessionPath.Text = string.Empty;
        NameWindow();
        UpdateSessionGrowth();
    }

    /// <summary>A picker the system could not show chose nothing: said where the card says what an action did.</summary>
    void IChoosingWindow.PickerFailed(string why)
    {
        if (closed) return;
        Say(CaptureStatus, "Nothing could be chosen", aloud: true);
        Say(CaptureDetail, why + " Nothing was opened or saved, and the current workspace is unchanged.", aloud: true);
    }

    /// <summary>
    /// Acts on a key that acts anywhere and types nothing - Ctrl+R, Ctrl+E, Ctrl+O, F5, Alt+Left, Alt+Right - as its
    /// action does: whether the key was taken, or null for any other key.
    /// </summary>
    private bool? AnywhereKey(WorkspaceViewModel viewModel, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.R:
                    // As Start exploring does; what holds the session shown says why nothing starts.
                    if (StartExploringButton.IsEnabled || HeldBy(start: true) is not null) StartChosenCapture();
                    return true;
                case Key.E:
                    ExportView();
                    return true;
                case Key.O:
                    // As Open saved session does, while it is offered: a capture under way hides it until it ends.
                    OpenSavedSession(OpenSavedSessionButton, new RoutedEventArgs());
                    return true;
                default:
                    break;
            }
        }

        return e.Key switch
        {
            Key.F5 => ApplyHeldUpdate(),
            Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => viewModel.Ascend(),
            Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => viewModel.GoForward(),
            _ => null,
        };
    }

    /// <summary>What the window is called while it shows no session, nor records one.</summary>
    internal const string ProductTitle = "InterCat · IPC Explorer";

    /// <summary>
    /// Names the window by what it shows, as switching between windows lists it and a screen reader says it when the
    /// window comes to the front: the session's folder, and whether a capture is starting, recording or saving it, as
    /// an investigation's window is named by its file.
    /// </summary>
    private void NameWindow()
    {
        string? session = string.IsNullOrEmpty(CaptureSessionPath.Text) ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(CaptureSessionPath.Text));
        Title = (phase, session) switch
        {
            (CaptureUiPhase.Starting, _) => "InterCat · Starting a capture",
            (CaptureUiPhase.Recording, { } name) => $"InterCat · Recording {name}",
            (CaptureUiPhase.Recording, null) => "InterCat · Recording",
            (CaptureUiPhase.Finishing, { } name) => $"InterCat · Saving {name}",
            (_, { } name) => $"InterCat · {name}",
            _ => ProductTitle,
        };
    }

    private void ReceiveCaptureUpdate(int run, CaptureUiUpdate update) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!closed && run == captureRunId)
            {
                ApplyCaptureUpdate(update);

                // A capture that ends has saved a session: the next time none is open, it is listed.
                if (update.Phase == CaptureUiPhase.Complete)
                {
                    _ = RefreshRecentSessionsAsync();
                }
            }
        });

    /// <summary>
    /// Whether the capture card says aloud what <paramref name="update"/> brings: what changes the capture's state, and
    /// what a person's action did, but never a recording's newer generation. That restates the card's count and chunks
    /// every second or two, with any notice its view appends - a rolling capture's release clipping a view that reached
    /// into what it gave up - which a screen reader said aloud each time, burying what else there was to hear (R15).
    /// </summary>
    private bool SaysAloud(CaptureUiUpdate update) => update.Overview is null || !IsLive;

    /// <summary>
    /// Writes a line of the capture card, which a screen reader announces as it changes, in silence unless
    /// <paramref name="aloud"/>.
    /// </summary>
    private static void Say(TextBlock line, string? text, bool aloud)
    {
        if (!aloud) AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Off);
        line.Text = text;
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
    }

    /// <summary>
    /// Shows one capture or open-session update. A newer generation of the displayed session replaces the workspace,
    /// except while the user reads evidence, when it is held and offered instead.
    /// </summary>
    internal void ApplyCaptureUpdate(CaptureUiUpdate update, bool forceOverview = false)
    {
        phase = update.Phase;

        // A view paused while a capture recorded follows it again once the capture has ended: nothing more is published
        // to hold the view from, so the saved session is shown, unless the person is reading records of the view shown.
        if (!IsLive) followLatest = true;

        bool aloud = SaysAloud(update);
        Say(CaptureStatus, update.Headline, aloud);
        bool unavailable = update.Phase == CaptureUiPhase.Unavailable;

        // Retrying is offered for the capture that could not run, and why it could not is on hover.
        NameStart(unavailable);
        ToolTip.SetTip(StartExploringButton, unavailable ? update.Detail : null);
        // An unavailable capture is a warning in words: caution ink, never a mechanism's hue (§6.6).
        CaptureStatus.Classes.Set("caution", unavailable);

        // A repeated detail (a live-counter update) keeps any navigation notice the last refresh appended to it.
        if (!string.Equals(update.Detail, appliedDetail, StringComparison.Ordinal))
        {
            appliedDetail = update.Detail;
            Say(CaptureDetail, update.Detail, aloud);
        }

        CaptureLatency.Text = Freshness(update.Milestones);
        publishedEvery = update.Milestones?.PublicationInterval;
        if (update.Summary is not null) CaptureSummary.Text = update.Summary;
        if (update.SessionPath is not null) CaptureSessionPath.Text = update.SessionPath;
        NameWindow();
        bool busy = update.Phase is CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StartExploringButton.IsEnabled = !busy;
        OpenSavedSessionButton.IsEnabled = !busy;
        InvestigationButton.IsEnabled = !busy;

        // While a capture runs the card holds only what can be done now - stop it, pause the view - and its state. The
        // actions that wait for it to end, and the words about starting one, give the ranked list the rail's height.
        StartExploringButton.IsVisible = !busy;
        CaptureKeepRow.IsVisible = !busy;
        CaptureKeepNote.IsVisible = !busy && Explained(KeepChoice);
        OpenSavedSessionButton.IsVisible = !busy;
        InvestigationButton.IsVisible = !busy;
        UnfinishedCaptureCard.IsVisible = !busy && offer is not null;
        StopCaptureButton.IsVisible = update.Phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StopCaptureButton.IsEnabled = update.Phase == CaptureUiPhase.Recording
            && captureStop?.IsCancellationRequested != true;
        KeepKeyboard();
        FollowButton.IsVisible = IsLive;
        liveHealth = IsLive ? update.LiveHealth : null;
        livePreview = update.Phase == CaptureUiPhase.Recording ? update.LivePreview : null;

        // The newest generation measures the session, whether it is shown or held while the user reads records.
        captureLimits = update.Limits;
        recordingVolume = update.Volume;
        if (update.Overview is { Size: { } measured } newest)
        {
            growth = (measured, newest.Began, newest.RetainedSince, newest.Pinned, newest.Pins);
        }

        if (!forceOverview && displayedOverview is null && update.Overview is null)
        {
            ShowAwaitingCapture(update.Phase);
        }

        if (update.Overview is not null && !forceOverview && IsLive)
        {
            lastPublicationUtc = DateTimeOffset.UtcNow;
        }

        if (update.Overview is { } overview
            && (forceOverview || overview.SessionId != displayedSessionId
                || overview.Generation > displayedGeneration))
        {
            if (!forceOverview && overview.SessionId == displayedSessionId
                && (workspace.HoldsGeneration || !followLatest))
            {
                // The user is reading records of this generation, or paused the view: keep it still and offer the newer one.
                heldUpdate = update;
                UpdateHeldBanner();
                UpdateHealthStrip();
                return;
            }

            ReplaceWorkspace(update, overview, forceOverview);
        }

        UpdateLivePreview();
        UpdateHealthStrip();
        ToolTip.SetTip(HealthStateText, unavailable ? update.Detail : null);
        UpdateEvidenceAction();
        UpdateBeforeSessionVisibility();
    }

    /// <summary>
    /// Hands the live preview to a workspace that follows the recording. A paused or held view shows an older generation
    /// whose gap to the preview is not previewed, so it shows none rather than a misleading edge.
    /// </summary>
    private void UpdateLivePreview() => workspace.ShowLivePreview(
        phase == CaptureUiPhase.Recording && followLatest && heldUpdate is null && !workspace.HoldsGeneration
            ? livePreview : null,
        displayedChunks, publishedEvery);

    /// <summary>
    /// A capture that records but has published nothing yet has no view of its own: the session shown before it started
    /// is not this capture's, so it gives way to an empty workspace that says what is happening and when the first view
    /// comes (§3.1, §6.8), rather than "No capture is running". Starting keeps whatever was shown, so a declined approval
    /// or a failed start loses nothing.
    /// </summary>
    private void ShowAwaitingCapture(CaptureUiPhase capturePhase)
    {
        if (capturePhase == CaptureUiPhase.Recording && !workspace.IsEmptyWorkspace)
        {
            workspace.PropertyChanged -= OnWorkspaceChanged;
            workspace.Dispose();
            workspace = new WorkspaceViewModel(OverviewWorkspace.Empty(), "empty-workspace");
            workspace.PropertyChanged += OnWorkspaceChanged;
            DataContext = workspace;
            UpdateBeforeSessionVisibility();
            currentSessionPath = null;
            UpdateEvidenceAction();
            GraphSurface.InvalidateVisual();
            TimelineSurface.RefreshLaneLayout();
            TimelineSurface.InvalidateVisual();
            MinimapSurface.InvalidateVisual();
        }

        (string? title, string? note) = capturePhase switch
        {
            CaptureUiPhase.Starting => ("Starting a capture",
                "Starting a capture. The first view appears once the broker publishes evidence."),
            CaptureUiPhase.Recording => ("Recording · first view pending",
                "Recording. The first view appears once the broker's first chunk is published and derived, usually "
                + "within two seconds. The health strip counts the records admitted so far."),
            CaptureUiPhase.Finishing => ("Stopping · deriving what was published",
                "Stopping. InterCat is deriving whatever the broker published."),
            _ => (null, null),
        };
        workspace.SetAwaitingCapture(title, note);
    }

    /// <summary>Whether a live capture is being followed, as opposed to a saved session or nothing.</summary>
    private bool IsLive => phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;

    private void ToggleFollow(object? sender, RoutedEventArgs eventArgs) => _ = ToggleFollowLatest();

    /// <summary>
    /// Pauses or resumes following a live capture's publications. Resuming shows the newest generation at once, unless
    /// the user is reading evidence, which keeps its own hold until they leave it. False when nothing is live.
    /// </summary>
    private bool ToggleFollowLatest()
    {
        if (!IsLive)
        {
            return false;
        }

        followLatest = !followLatest;
        if (followLatest && !workspace.HoldsGeneration)
        {
            _ = ApplyHeldUpdate();
        }

        UpdateLivePreview();
        UpdateHeldBanner();
        UpdateHealthStrip();
        return true;
    }

    private void CountCandidates(object? sender, RoutedEventArgs eventArgs) =>
        _ = ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates);

    private void GraphExpandChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!layingPanes)
        {
            ExpandPane(GraphExpandToggle.IsChecked == true ? MainPane.Graph : null);
        }
    }

    private void TimelineExpandChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!layingPanes)
        {
            ExpandPane(TimelineExpandToggle.IsChecked == true ? MainPane.Timeline : null);
        }
    }

    /// <summary>
    /// F11 (§6.1): with a pane filling the column, gives both their places back, wherever the keyboard is; otherwise lets
    /// the pane holding the keyboard fill it. With the keyboard in neither pane, nothing is hidden.
    /// </summary>
    private bool ToggleFocusedPane()
    {
        if (expandedPane is not null)
        {
            ExpandPane(null);
            return true;
        }

        MainPane? holding = GraphPane.IsKeyboardFocusWithin ? MainPane.Graph
            : TimelinePane.IsKeyboardFocusWithin ? MainPane.Timeline
            : null;
        if (holding is { } pane)
        {
            ExpandPane(pane);
        }

        return holding is not null;
    }

    /// <summary>The main pane filling the column by a person's command (§6.1), or null when the two share it.</summary>
    internal MainPane? ExpandedPane => expandedPane;

    /// <summary>
    /// Lets <paramref name="pane"/> fill the column, the other pane hidden one command away, or with null gives both their
    /// places back at the split the person left - never by itself, only when asked (§6.1's collapse floor). A session shown
    /// from an investigation keeps it there, as it keeps the split (§26.3).
    /// </summary>
    internal void ExpandPane(MainPane? pane)
    {
        if (pane == expandedPane)
        {
            return;
        }

        RowDefinitions rows = PanesGrid.RowDefinitions;
        LayPanes(pane, expandedPane is null ? (rows[0].Height, rows[2].Height) : sharedSplit);
        if (layoutHome is { } home)
        {
            KeepPanes(home);
        }
    }

    /// <summary>
    /// Lays the two main panes out: <paramref name="pane"/> filling the column, or both at <paramref name="split"/>, which is
    /// also the split they return to from a pane filling it. While both are shown each keeps its least height, so no split
    /// hides one; filling the column with the other is what does.
    /// </summary>
    private void LayPanes(MainPane? pane, (GridLength Graph, GridLength Timeline)? split)
    {
        RowDefinitions rows = PanesGrid.RowDefinitions;
        layingPanes = true;
        try
        {
            sharedSplit = split;
            expandedPane = pane;
            (rows[0].Height, rows[2].Height) = pane switch
            {
                MainPane.Graph => (GridLength.Star, new GridLength(0)),
                MainPane.Timeline => (new GridLength(0), GridLength.Star),
                _ => split ?? (GridLength.Star, GridLength.Star),
            };
            rows[0].MinHeight = pane == MainPane.Timeline ? 0 : GraphPaneFloor;
            rows[2].MinHeight = pane == MainPane.Graph ? 0 : TimelinePaneFloor;
            rows[1].Height = new GridLength(pane is null ? 6 : 0);
            GraphPane.IsVisible = pane != MainPane.Timeline;
            TimelinePane.IsVisible = pane != MainPane.Graph;
            PaneSplitter.IsVisible = pane is null;
            GraphExpandToggle.IsChecked = pane == MainPane.Graph;
            TimelineExpandToggle.IsChecked = pane == MainPane.Timeline;
        }
        finally
        {
            layingPanes = false;
        }
    }

    /// <summary>Lays the panes out as an investigation kept them: the graph's share of the height, and the pane filling the column.</summary>
    private void PutBackPanes(WorkspacePanes panes)
    {
        LayPanes(panes.Expanded switch
        {
            WorkspacePane.Graph => MainPane.Graph,
            WorkspacePane.Timeline => MainPane.Timeline,
            _ => null,
        }, (new GridLength(panes.GraphShare, GridUnitType.Star), new GridLength(1 - panes.GraphShare, GridUnitType.Star)));

        // The columns take the widths a person gave them, a rail kept so no longer following the window; one kept at none
        // follows the window again, or stands at its design width. The window then keeps the main panes their room, as it
        // does as it narrows.
        layingPanes = true;
        ColumnDefinition rail = WindowGrid.ColumnDefinitions[0];
        if (panes.RailWidth is { } width)
        {
            followedRailWidth = null;
            rail.Width = new GridLength(width);
        }
        else
        {
            followedRailWidth = rail.Width.Value;
        }

        WindowGrid.ColumnDefinitions[2].Width = new GridLength(panes.InspectorWidth ?? WorkspacePanes.InspectorDesign);
        layingPanes = false;
        if (Bounds.Width > 0)
        {
            FollowRailWidth(Bounds.Width);
            KeepMainPanesWidth(Bounds.Width);
        }
    }

    private void CountCorrelatedOnly(object? sender, RoutedEventArgs eventArgs) =>
        _ = ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCorrelated);

    private void RestoreDefaultView(object? sender, RoutedEventArgs eventArgs) => _ = RestoreDefaultViewAsync();

    private void ToggleCollectorsAside(object? sender, RoutedEventArgs eventArgs) => ChooseCollectorsAside(!collectorsAside);

    /// <summary>
    /// Sets InterCat's own processes aside from the shown session's rows, graph and channels, or shows them again (§19.5's
    /// view filter): the overview already projected is filtered, so nothing is read again, and the view returns to the
    /// machine rung, as a regrouped one does, keeping its time, ranking and scales. A process set aside is no longer the
    /// selection. Each later publication of the session is filtered so too. False when nothing is shown, the session names
    /// no process of InterCat's own, or the choice is made already.
    /// </summary>
    internal bool ChooseCollectorsAside(bool aside)
    {
        if (displayedOverview is not { } shown || aside == collectorsAside || !workspace.OffersCollectorsAside)
        {
            return false;
        }

        if (aside && workspace.SelectedProcess is { Collector: not null })
        {
            workspace.SelectedProcess = null;
        }

        collectorsAside = aside;
        ReplaceWorkspace(new CaptureUiUpdate(phase, CaptureStatus.Text ?? string.Empty, CaptureDetail.Text ?? string.Empty,
            SessionPath: currentSessionPath, Overview: shown, OverviewChunks: displayedChunks), shown, forceOverview: false,
            regrouping: true);

        // A member opened from its investigation keeps the choice there, as it keeps its grouping (§26.3).
        if (layoutHome is { } home)
        {
            KeepLayout(home);
        }

        return true;
    }

    /// <summary>
    /// §6.8's one command back to the defaults: the shown session's rows ranked by records, not per second, every timeline
    /// lane on one scale, its processes grouped by executable and its records counted with correlated evidence only, each
    /// put back as its own control would, so the time, selection and pins stay, and a regrouped view returns to the machine
    /// rung. A session opened from an investigation keeps the defaults there, which is then to keep nothing of them. False
    /// when nothing is shown or every setting is at its default already.
    /// </summary>
    internal async Task<bool> RestoreDefaultViewAsync()
    {
        if (displayedOverview is null || !workspace.DiffersFromDefaults)
        {
            return false;
        }

        ViewSettings defaults = ViewSettings.Default;
        ChooseCollectorsAside(false);
        workspace.RankBy = defaults.RankBy;
        workspace.PerSecond = defaults.PerSecond;
        workspace.ScalesEachLane = defaults.ScalesEachLane;
        workspace.ReadsWallClock = false;

        // Regrouped first, from the overview already projected, so a policy projected again afterwards is grouped by
        // executable too.
        if (workspace.Grouping != defaults.Grouping)
        {
            ChooseGrouping(defaults.Grouping);
        }

        if (workspace.EvidencePolicy != defaults.Policy)
        {
            await ChooseEvidencePolicyAsync(defaults.Policy);
        }

        return true;
    }

    /// <summary>
    /// Counts the shown session's records under another evidence policy (§6.8): its generation is projected again under
    /// it, and the workspace keeps its rung, selection, pins and interval, as a newer publication of it does. A live
    /// capture projects every later publication under it too, and one whose view is held or paused shows it from the next
    /// publication it shows. False when nothing is shown, it is already counted so, or it cannot be counted again, which
    /// the capture card then says.
    /// </summary>
    internal async Task<bool> ChooseEvidencePolicyAsync(EvidencePolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (displayedOverview is not { } shown || currentSessionPath is not { } path || shown.Policy == policy)
        {
            return false;
        }

        evidencePolicy = policy;
        if (IsLive && (!followLatest || workspace.HoldsGeneration))
        {
            return true;
        }

        SessionOverviewBundle overview;
        try
        {
            overview = await Task.Run(
                () => SessionOverviewProjector.Project(SharedSessionStores.Open(path, shown.SessionId), policy));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            // What is shown stays counted as it was, and the button that asked is answered rather than left looking as
            // if it did nothing, unless a later choice has superseded it.
            if (!closed && evidencePolicy == policy)
            {
                CaptureStatus.Text = "The records could not be counted again";
                CaptureDetail.Text = exception.Message;
            }

            return false;
        }

        // A newer publication, another session or another choice may have come meanwhile; only this one's answer is shown.
        if (closed || evidencePolicy != policy || displayedSessionId != overview.SessionId
            || displayedGeneration > overview.Generation)
        {
            return false;
        }

        ReplaceWorkspace(new CaptureUiUpdate(phase, CaptureStatus.Text ?? string.Empty, CaptureDetail.Text ?? string.Empty,
            SessionPath: path, Overview: overview, OverviewChunks: displayedChunks), overview, forceOverview: false);

        // A member opened from its investigation keeps its choice there, as it keeps a pin or its ranking (§26.3).
        if (layoutHome is { } home)
        {
            KeepLayout(home);
        }

        return true;
    }

    /// <summary>
    /// Groups the shown session's processes another way (§6.3): by executable, or by the terminal session their lifecycle
    /// records name. The overview already projected is regrouped, so nothing is read again, and the view returns to the
    /// machine rung, whose rows are the groups, keeping its time, selection, ranking, pins and scales. A live capture groups
    /// each later publication so too, a session opened from an investigation keeps the choice there, and a session opened
    /// afterwards starts grouped by executable, or as its investigation keeps it. False when nothing is shown, its
    /// processes do not offer the grouping, or it is chosen already.
    /// </summary>
    internal bool ChooseGrouping(LaneGrouping grouping)
    {
        if (displayedOverview is not { } shown || grouping == workspace.Grouping
            || !workspace.GroupingOptions.Any(option => option.Grouping == grouping))
        {
            return false;
        }

        laneGrouping = grouping;
        ReplaceWorkspace(new CaptureUiUpdate(phase, CaptureStatus.Text ?? string.Empty, CaptureDetail.Text ?? string.Empty,
            SessionPath: currentSessionPath, Overview: shown, OverviewChunks: displayedChunks), shown, forceOverview: false,
            regrouping: true);

        // A member opened from its investigation keeps its grouping there, as it keeps its ranking (§26.3).
        if (layoutHome is { } home)
        {
            KeepLayout(home);
        }

        return true;
    }

    /// <summary>
    /// The Group by selector's choice, applied once the selector has finished changing, since applying it replaces the
    /// workspace the selector's items come from. A choice that cannot be applied puts the selector back.
    /// </summary>
    private void GroupingChosen(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (GroupBySelector.SelectedItem is not GroupingOption chosen || chosen.Grouping == workspace.Grouping)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!closed && !ChooseGrouping(chosen.Grouping))
            {
                GroupBySelector.SelectedItem = workspace.SelectedGrouping;
            }
        });
    }

    /// <summary>The grouping a projected publication is shown in: the one chosen, where its processes offer it, else by executable.</summary>
    private static LaneGrouping GroupingShown(WorkspaceSnapshot projected, LaneGrouping chosen) =>
        WorkspaceGrouping.Offered(projected).Contains(chosen) ? chosen : LaneGrouping.Executable;

    /// <summary>
    /// The same view's navigation once its processes are regrouped: the groups are what changed, so it returns to the
    /// machine rung, whose rows they are, with no way forward into a group that is gone and nothing chosen at a rung below.
    /// Its time, selected process, search, ranking and scales stay.
    /// </summary>
    private static WorkspaceNavigationMemento Regrouped(WorkspaceNavigationMemento saved) => saved with
    {
        Breadcrumb = [saved.Breadcrumb[0]],
        SelectedRungKey = null,
        SelectedTimelineDirection = null,
        SelectedChannelEnd = null,
        Forward = null,
        ReachedWith = null,
    };

    private void ReplaceWorkspace(CaptureUiUpdate update, SessionOverviewBundle overview, bool forceOverview,
        bool regrouping = false)
    {
        WorkspaceNavigationMemento? savedNavigation = !forceOverview && overview.SessionId == displayedSessionId
            ? workspace.CaptureNavigation() : null;

        // A view held from the newest generation - paused, or kept while records were read - that comes back to it advances
        // there (§6.8): a fit timeline is carried from the range it was drawn at. A publication followed live is drawn at once.
        bool advances = savedNavigation is not null && heldUpdate is not null && !regrouping;
        TimeRange shownRange = TimelineSurface.DrawnViewport;
        if (regrouping && savedNavigation is not null)
        {
            // A regrouped view is the generation already shown, so a newer one held while the view is paused still waits.
            savedNavigation = Regrouped(savedNavigation);
        }
        else
        {
            heldUpdate = null;
        }

        displayedSessionId = overview.SessionId;
        displayedGeneration = overview.Generation;
        displayedOverview = overview;
        displayedChunks = update.OverviewChunks ?? 0;
        currentSessionPath = update.SessionPath ?? currentSessionPath;
        // A later publication of the same session keeps every node that is still drawn where the user last saw it.
        IReadOnlyDictionary<string, GraphPoint>? pins = savedNavigation is null ? null : workspace.GraphPins;
        IReadOnlyList<ProcessInstanceId> lanes = [];
        string? pinsNotice = null;
        ViewSettings? restored = null;
        // Each publication is grouped as its person chose, where its processes offer that grouping.
        WorkspaceSnapshot projected = OverviewWorkspace.From(overview);
        if (savedNavigation is null)
        {
            // A session opened from an investigation it is a member of keeps its pins, pinned lanes and view settings there,
            // and gets back those it kept (§26.3); any other session keeps them only while it is open. Its policy was put
            // back before its overview was projected, under it.
            layoutHome = null;
            keptLanes = null;
            keptSettings = null;
            keptPanes = null;
            laneGrouping = LaneGrouping.Executable;
            collectorsAside = false;
            if (openingFromInvestigation is { } investigation && LayoutKeptIn(investigation, overview.SessionId) is { } kept)
            {
                // The window's panes are the investigation's, for every session opened from it: put back when it keeps them,
                // and left as they are, as for a session opened on its own, when it keeps none.
                layoutHome = (investigation, overview.SessionId);
                pins = kept.Pins;
                lanes = keptLanes = kept.Lanes;
                restored = keptSettings = kept.Settings;
                laneGrouping = kept.Settings.Grouping;
                collectorsAside = kept.Settings.CollectorsAside;
                keptPanes = kept.Panes is { } panes
                    ? (WorkspacePanes.Kept(panes.GraphShare), panes.Expanded, panes.RailWidth, panes.InspectorWidth)
                    : (WorkspacePanes.EqualShare, null, null, null);
                if (kept.Panes is { } put)
                {
                    PutBackPanes(put);
                }

                pinsNotice = $"Its pins and view settings are kept in the investigation {Path.GetFileName(investigation)}"
                    + PutBack(kept.Pins.Count, kept.Lanes.Count,
                        kept.Settings with
                        {
                            Policy = overview.Policy,
                            Grouping = GroupingShown(projected, laneGrouping),
                            // Said put back only where the session names some of InterCat's own to set aside.
                            CollectorsAside = collectorsAside && projected.Processes.Any(process => process.Collector is not null),
                            // Said put back only where the session's capture recorded a wall clock to read it on.
                            WallClock = kept.Settings.WallClock && projected.WallClock is not null,
                        },
                        kept.Panes);
            }

            keptPins = pins;
        }

        // The graph's identity names the grouping too, since its nodes are the groups. InterCat's own processes are set
        // aside as their person chose, after grouping, so a group left with none goes too.
        LaneGrouping grouping = GroupingShown(projected, laneGrouping);
        WorkspaceSnapshot grouped = WorkspaceGrouping.Regroup(projected, grouping);
        WorkspaceSnapshot shownSnapshot = collectorsAside ? WorkspaceCollectors.SetAside(grouped) : grouped;
        // Every read of the workspace binds records as the overview it shows was projected: under its policy, with what the
        // view set aside left out of the totals its rankings state over the rows shown.
        SessionEvidenceSource? evidence = currentSessionPath is { } path
            ? new SessionEvidenceSource(path, overview.SessionId, overview.Generation, overview.Policy)
            {
                SetAside = WorkspaceCollectors.Instances(shownSnapshot),
            }
            : null;
        var replacement = new WorkspaceViewModel(shownSnapshot,
            WorkspaceCollectors.Identity(WorkspaceGrouping.Identity(overview.GraphIdentity, grouping), collectorsAside), evidence,
            savedNavigation is null ? null : workspace.LaidOutPositions, pins);
        if (restored is { } settings)
        {
            replacement.RankBy = settings.RankBy;
            replacement.PerSecond = settings.PerSecond;
            replacement.ScalesEachLane = settings.ScalesEachLane;
            replacement.ReadsWallClock = settings.WallClock;
        }

        // The lanes its investigation kept pinned are pinned before any group's lanes are counted, so they are drawn first.
        replacement.PinLanes(lanes);

        if (pinsNotice is not null)
        {
            CaptureDetail.Text += " " + pinsNotice;
        }
        if (savedNavigation is not null)
        {
            // What the newer generation changed of the view is said once, beside its chunks: what the navigation lost, and
            // a zoom the timeline moves inside what the session keeps once a release gave up time it showed.
            string? navigationNotice = replacement.RestoreNavigation(savedNavigation);
            TimeRange shown = TimelineSurface.Viewport;
            TimeRange kept = shown.ClampInside(replacement.Snapshot.Extent);
            if (!TimelineSurface.IsFit && kept != shown)
            {
                navigationNotice = (navigationNotice is null ? string.Empty : navigationNotice + " ")
                    + "The view showed time the session released, so it "
                    + (kept == replacement.Snapshot.Extent
                        ? "shows all the time the session keeps."
                        : "moved to the earliest time the session keeps.");
            }

            if (navigationNotice is not null)
            {
                Say(CaptureDetail, CaptureDetail.Text + " " + navigationNotice, SaysAloud(update));
            }
        }
        else
        {
            // A session shown afresh opens whole, never at a zoom into the time of the one shown before it.
            TimelineSurface.ForgetViewport();
        }

        if (savedNavigation is not null)
        {
            // The timeline keeps what it drew until this generation's own counts replace it, as the graph keeps its layout,
            // and the ranking keeps its scope's counts instead of blinking back to the whole session meanwhile (§6.4).
            // Its pinned lanes stay pinned, as the graph's nodes do.
            replacement.PinLanes(workspace.PinnedLanes);
            replacement.AdoptTimeline(workspace.CarryTimeline());
            replacement.AdoptScope(workspace.CarryScope());
        }

        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
        workspace = replacement;
        workspace.PropertyChanged += OnWorkspaceChanged;
        DataContext = workspace;
        UpdateBeforeSessionVisibility();
        UpdateLivePreview();
        UpdateHeldBanner();
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.RefreshLaneLayout();
        TimelineSurface.InvalidateVisual();
        MinimapSurface.InvalidateVisual();
        if (advances)
        {
            TimelineSurface.AdvanceFrom(shownRange);
        }
    }

    /// <summary>How soon the first view arrived after recording began and how often it refreshes, once both are known.</summary>
    private static string Freshness(CaptureMilestones? milestones) =>
        milestones is { FirstOverviewAfterStart: { } first, PublicationInterval: { } interval }
            ? $"First view {first.TotalSeconds:0.0} s after recording began · refreshed about every {interval.TotalSeconds:0.#} s"
            : string.Empty;

    /// <summary>Applies the generation held while evidence was inspected. False when none is waiting.</summary>
    private bool ApplyHeldUpdate()
    {
        if (heldUpdate is not { Overview: { } overview } pending || closed)
        {
            return false;
        }

        ReplaceWorkspace(pending, overview, forceOverview: false);
        return true;
    }

    private void UpdateHeldBanner()
    {
        bool held = heldUpdate?.Overview is not null;
        HeldBanner.IsVisible = held;
        HeldBannerText.Text = !held ? string.Empty
            : (followLatest
                ? $"Showing generation {displayedGeneration:N0} while you read records. "
                : $"View paused at generation {displayedGeneration:N0}. ")
                + (IsLive
                    ? $"Generation {heldUpdate!.Overview!.Generation:N0} is published; recording continues."
                    : $"Generation {heldUpdate!.Overview!.Generation:N0} is the session as saved.");
        FollowButton.Content = followLatest ? "Pause view (F)" : "Follow live (F)";
    }

    /// <summary>
    /// The health strip: what the capture is doing, whether the view follows it, what loss is known, and how recent the
    /// last publication is. A loss that is not yet stated reads as not stated, never as none (§20.6, R21).
    /// </summary>
    private void UpdateHealthStrip()
    {
        bool paused = IsLive && (!followLatest || heldUpdate is not null);
        HealthStateText.Text = phase switch
        {
            CaptureUiPhase.Starting => "Starting capture",
            CaptureUiPhase.Recording when paused && workspace.HoldsGeneration => "Recording · view held while you read records",
            CaptureUiPhase.Recording when paused => "Recording · view paused",
            CaptureUiPhase.Recording => "Recording · following live",
            CaptureUiPhase.Finishing => "Stopping and saving",
            CaptureUiPhase.Unavailable => "Capture unavailable",
            _ => displayedOverview is null ? "Ready"
                : displayedOverview.Redaction is not null ? "Redacted package"
                : displayedOverview.Demo ? "Demo session, generated by InterCat"
                : "Saved session",
        };
        // The dot restates the words beside it: the accent while the view follows a recording, caution while a live
        // view is held or paused or the capture is unavailable, muted otherwise. It borrows no mechanism's hue (§6.6),
        // and as classes over theme resources it follows a change of mode at once.
        bool following = phase == CaptureUiPhase.Recording && !paused;
        HealthDot.Classes.Set("accent", following);
        HealthDot.Classes.Set("caution", (IsLive && !following) || phase == CaptureUiPhase.Unavailable);
        HealthLossText.Text = LossStatement();
        string admitted = IsLive && liveHealth is { } counters ? $"{counters.AdmittedRecords:N0} admitted · " : string.Empty;
        HealthFreshnessText.Text = IsLive && lastPublicationUtc is { } last
            ? admitted + $"last publication {Math.Max(0, (DateTimeOffset.UtcNow - last).TotalSeconds):0} s ago"
            : admitted.TrimEnd(' ', '·');
        UpdateSessionGrowth();
    }

    /// <summary>
    /// §12.1 S5: the session's size, bytes per record and tier, as its newest generation measured them; while it records,
    /// when its capture's limits stop it - for a capture keeping a window, from what the window holds, and when a pin
    /// keeping the session past it does; after an interval release what it retains; and what its pins keep - in
    /// `icat session`'s and `icat capture`'s words (R18), each instant read on the view's time base as every other instant
    /// the window states is (§6.2). It reads as a caution when that stop is a minute away, or when the session is past the
    /// sizes any release is qualified at.
    /// </summary>
    private void UpdateSessionGrowth()
    {
        if (growth is not ({ } size, var began, var retained, var pinned, var pins))
        {
            SessionGrowthText.Text = string.Empty;
            SessionGrowthText.IsVisible = false;
            SessionGrowthText.Classes.Set("caution", false);
            return;
        }

        Func<long, string> moment = nanoseconds => workspace.TimeBase.Moment(nanoseconds, CultureInfo.CurrentCulture);
        CaptureHeadroom? headroom = phase == CaptureUiPhase.Recording && captureLimits is { } limits && began is { } start
            ? SessionGrowth.Headroom(size, start, DateTimeOffset.UtcNow, limits, recordingVolume, pins, moment)
            : null;

        // Pins that could not be read are said as the generation said them; read, each is stated from its instant.
        string? pinsLine = pins.Count > 0 ? RetentionPinText.SizeLine(pins, moment) : pinned;
        SessionGrowthText.Text = SessionGrowth.Statement(size, headroom, stopOnItsOwnLine: true)
            + (retained is null ? string.Empty : "\n" + SessionGrowth.Retained(retained, moment))
            + (pinsLine is null ? string.Empty : "\n" + pinsLine);
        SessionGrowthText.IsVisible = true;
        SessionGrowthText.Classes.Set("caution",
            size.Tier == SessionSizeTier.Qualification || headroom?.Remaining <= SessionGrowth.Imminent);
    }

    /// <summary>
    /// Loss so far while recording, from the broker's live counters. Drops and ETW's event and buffer loss are separate
    /// counters and are never added into one total; unreadable ETW counters are said to be unreadable (§20.6, R21).
    /// </summary>
    internal static string LiveLossStatement(BrokerCaptureHealth? health)
    {
        if (health is null)
        {
            return "Loss is stated when recording stops";
        }

        string drops = health.ApplicationDrops == 0
            ? "no drops"
            : $"{Count(health.ApplicationDrops, "record")} dropped by InterCat's queue";
        string? etw = (health.ProviderReportedLoss, health.ConsumerBufferLoss) switch
        {
            (null, _) or (_, null) => "ETW loss unreadable",
            (0, 0) => null,
            ({ } events, 0) => $"ETW lost {Count(events, "event")}",
            (0, { } buffers) => $"ETW lost {Count(buffers, "buffer")}",
            ({ } events, { } buffers) => $"ETW lost {Count(events, "event")} and {Count(buffers, "buffer")}",
        };
        return health.ApplicationDrops == 0 && etw is null
            ? "No loss reported yet · final coverage pending"
            : $"So far {drops} · {etw ?? "no ETW loss"}";

        static string Count(long count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
    }

    private string LossStatement()
    {
        if (displayedOverview is not { } overview)
        {
            return string.Empty;
        }

        if (!overview.CoverageLedgerPublished)
        {
            return IsLive ? LiveLossStatement(liveHealth) : "No coverage ledger · loss unknown";
        }

        // An interval package's ledger speaks only within its interval: what it says there, and that outside it nothing is
        // known, rather than every mechanism unknown over the whole time.
        IReadOnlyList<MechanismCoverage> judged = overview.IntervalCoverage ?? overview.MechanismCoverage;
        MechanismCoverage[] collected = [.. judged.Where(entry => entry.State != CoverageState.NotCollected)];
        MechanismCoverage[] affected = [.. collected.Where(entry => entry.State != CoverageState.Covered)];
        string statement = collected.Length == 0 ? "No mechanism was collected"
            : affected.Length == 0 ? "No loss reported"
            : string.Join(" · ", affected.Select(entry => $"{EvidenceRowText.MechanismName(entry.Mechanism)} {Describe(entry.State)}"));
        return overview.IntervalCoverage is null ? statement
            : statement + " within this package's interval · coverage unknown outside it";

        static string Describe(CoverageState state) => state switch
        {
            CoverageState.PartialGap => "has a coverage gap",
            CoverageState.ReducedFidelity => "has reduced fidelity",
            _ => "coverage unknown",
        };
    }

    /// <summary>Marks every ranked row drawn now by its share of the multi-selection.</summary>
    private void MarkSelectionShares()
    {
        foreach (Control container in RungList.GetRealizedContainers())
        {
            MarkSelectionShare(container);
        }
    }

    /// <summary>
    /// Marks one ranked row by its share of the multi-selection: an accent bar at its edge, lighter for a group only some
    /// of whose processes are in it, and an item status a screen reader announces with the row. A process whose node the
    /// graph rings for a chosen cell, which no selection holds, carries the bar at the highlight's strength.
    /// </summary>
    private void MarkSelectionShare(Control container)
    {
        // A row is read from what the container holds: while it is being prepared, as one scrolled into view is, the list
        // cannot yet say which item it holds, and a row so marked by its index came up unmarked.
        RungRow? row = container.DataContext as RungRow;
        SelectionShare share = row is null ? SelectionShare.None : workspace.ShareOf(row);
        container.Classes.Set("chosen", share == SelectionShare.All);
        container.Classes.Set("chosenPart", share == SelectionShare.Some);
        MarkCellHighlight(container, share == SelectionShare.None && row is not null && workspace.HighlightsForCell(row),
            share switch
            {
                SelectionShare.All => "in the selection",
                SelectionShare.Some => "partly in the selection",
                _ => null,
            });
    }

    private void MarkCellRelationships()
    {
        foreach (Control container in RelationshipList.GetRealizedContainers())
        {
            MarkCellRelationship(container);
        }
    }

    /// <summary>Marks a relationship's row whose edge the graph highlights for a chosen cell, as the edge's table equivalent.</summary>
    private void MarkCellRelationship(Control container) => MarkCellHighlight(container,
        container.DataContext is RelationshipRow row && workspace.HighlightsForCell(row), status: null);

    /// <summary>
    /// Gives a row the bar a chosen cell's highlight is drawn with and the item status a screen reader announces with it,
    /// or else <paramref name="status"/>, or none.
    /// </summary>
    private static void MarkCellHighlight(Control container, bool highlighted, string? status)
    {
        container.Classes.Set("highlighted", highlighted);
        if ((highlighted ? "highlighted for the chosen cell" : status) is { } said)
        {
            AutomationProperties.SetItemStatus(container, said);
        }
        else
        {
            container.ClearValue(AutomationProperties.ItemStatusProperty);
        }
    }

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ShowsMechanismLanes)
            or nameof(WorkspaceViewModel.ShowsProcessLanes)
            or nameof(WorkspaceViewModel.ShowsDirectionLanes)
            or nameof(WorkspaceViewModel.ShowsChannelEndLanes)
            or nameof(WorkspaceViewModel.ShowsRpcCallLane)
            or nameof(WorkspaceViewModel.ShowsHttpExchangeLane))
        {
            // A rung or asynchronous lane query can change the row count without replacing DataContext.
            TimelineSurface.RefreshLaneLayout();
        }
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.SelectedProcess)
            or nameof(WorkspaceViewModel.ShowsProcessLanes) or nameof(WorkspaceViewModel.PinnedLanes))
        {
            // A lane pinned or unpinned moves; the selected one stays in view as it does.
            Dispatcher.UIThread.Post(TimelineSurface.BringSelectedProcessLaneIntoView);
        }

        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.SearchedLane) && workspace.SearchedLane is { } searched)
        {
            // A search that finds a lane of the group shown scrolls it into view (§6.2: search lane names).
            Dispatcher.UIThread.Post(() => TimelineSurface.BringLaneIntoView(searched));
        }
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ChosenProcesses) or nameof(WorkspaceViewModel.HasMultiSelection)
            or nameof(WorkspaceViewModel.CellGraphNodes))
        {
            MarkSelectionShares();
        }

        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.CellGraphEdges))
        {
            MarkCellRelationships();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.RungRows) or nameof(WorkspaceViewModel.Crumbs))
        {
            KeepRailKeyboard();
        }

        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.ReadsWallClock))
        {
            // The size line's instants follow the view's time base.
            UpdateSessionGrowth();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.PinnedGraphNodeKeys) or nameof(WorkspaceViewModel.PinnedLanes)
                or nameof(WorkspaceViewModel.RankBy) or nameof(WorkspaceViewModel.PerSecond) or nameof(WorkspaceViewModel.ScalesEachLane)
                or nameof(WorkspaceViewModel.ReadsWallClock)
            && layoutHome is { } home)
        {
            KeepLayout(home);
        }
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
        MinimapSurface.InvalidateVisual();

        // A card describes what is drawn now, so a count or brush arriving under a resting pointer redraws it, and a
        // layout or pin that moves a node away from the pointer takes the node's card with it (P22).
        GraphSurface.RefreshHover();
        HoverLayer.InvalidateVisual();
        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.HoldsGeneration))
        {
            UpdateHealthStrip();

            // Reading records holds this generation, so the live edge, which continues the newest one, steps aside.
            UpdateLivePreview();
        }

        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.Crumbs))
        {
            // The user moved to another rung. Its table starts at its busiest rows, or at the row it selected, never at
            // the scroll offset of the rung it left, which would open it part-way down with its first row cut off. A
            // publication restores navigation before this window listens, so live updates never move the table.
            ShowRankedTableStart();
        }

        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.HoldsGeneration) && !workspace.HoldsGeneration
            && heldUpdate is not null && followLatest)
        {
            // Leaving the evidence rung releases the hold; apply after this change has finished notifying.
            Dispatcher.UIThread.Post(() =>
            {
                if (!workspace.HoldsGeneration) _ = ApplyHeldUpdate();
            });
        }
    }

    /// <summary>
    /// The new rows are not laid out yet. The offset returns to the top at once, before the layout pass would clamp the
    /// old offset against the new rows. A selected row is brought into view once it has a place.
    /// </summary>
    private void ShowRankedTableStart()
    {
        if (RungList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
        {
            scroller.Offset = new Vector(scroller.Offset.X, 0);
        }

        if (RungList.SelectedItem is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (RungList.SelectedItem is { } selected) RungList.ScrollIntoView(selected);
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateEvidenceAction()
    {
        ProcessInstanceId? channelProcess = ChannelDiscoveryScope(workspace.CaptureNavigation());
        bool session = currentSessionPath is not null && displayedGeneration > 0;
        ShowRecordsButton.IsEnabled = session && !workspace.IsEvidenceRung;
        BrowseChannelsButton.IsEnabled = session && !workspace.IsEvidenceRung;
        ToolTip.SetTip(ShowRecordsButton, ShowRecordsButton.IsEnabled
            ? "Open this rung's admitted source records in the ladder. Esc comes back here."
            : workspace.IsEvidenceRung ? "You are at the source records. Esc returns to where you were."
            : "Open or record a session first.");
        ToolTip.SetTip(BrowseChannelsButton, BrowseChannelsButton.IsEnabled
            ? (channelProcess is null ? "Browse every admitted paired TCP channel in this generation."
                : "Browse admitted paired TCP channels involving this process instance.")
                + " Choosing one opens its source records within the ranking's time scope: a brush, or else the zoomed range."
            : workspace.IsEvidenceRung ? "Esc leaves the source records; paired channels can be browsed from any other level."
            : "Open or record a session first.");

        // Packaging reads a whole published generation, so it waits until a live capture has stopped. One package is made
        // at a time: its own button cancels it, and the other waits.
        bool packagingRedacted = packaging is not null && !packagingOriginal;
        bool packagingCopy = packaging is not null && packagingOriginal;
        bool canPackage = packaging is null && session && !IsLive && !openingSession;

        // A redacted package is shared as it is (§11.3): a package is not built from a package, and its exact copy is that
        // package, pseudonymized, never an original or unredacted one.
        bool redactedPackage = displayedOverview?.Redaction is not null;
        SharePackageButton.Content = packagingRedacted ? "Cancel packaging" : "Share redacted session…";
        SharePackageButton.IsEnabled = packagingRedacted || (canPackage && !redactedPackage);
        ToolTip.SetTip(SharePackageButton, packagingRedacted
            ? "Stop making the package. Nothing is saved, and the session is unchanged."
            : redactedPackage
                ? "This session is already a redacted package, and a package is not built from a package. Share it as it "
                    + "is: Share this package… saves an exact copy."
            : canPackage
                ? "Save a new session folder with pseudonymous names, IDs and addresses and no original journal, "
                    + "that opens in InterCat. What it keeps and leaves out is shown first."
                : PackageUnavailable(session));
        ShareOriginalButton.Content = packagingCopy ? "Cancel packaging"
            : redactedPackage ? "Share this package…" : "Share original session…";
        AutomationProperties.SetHelpText(ShareOriginalButton, redactedPackage
            ? "Save an exact copy of this redacted package to share"
            : "Save an exact, unredacted copy of this session's evidence to share");
        ShareOriginalButton.IsEnabled = packagingCopy || canPackage;
        UpdatePinAction(session);
        ToolTip.SetTip(ShareOriginalButton, packagingCopy
            ? "Stop copying. Nothing is saved, and the session is unchanged."
            : canPackage
                ? redactedPackage
                    ? "Save an exact copy of this redacted package that opens in InterCat as the same package. It holds "
                        + "its pseudonyms, never the original values. What it holds is shown first."
                    : "Save an exact, unredacted copy of this session's evidence that opens in InterCat as the same "
                        + "session. What it holds is shown first."
                : PackageUnavailable(session));
    }

    private string PackageUnavailable(bool session) =>
        packaging is not null ? "Another package is being made. Wait for it, or cancel it with its own button."
        : IsLive ? "Stop the capture first; a package holds a finished session."
        : session ? "Wait for the session to finish opening."
        : "Open or record a session first.";

    /// <summary>
    /// Widens the rail with the window: its design width up to a 1,786-pixel window, then 14% of the window, at most 400.
    /// A fixed rail on a 4K window cut every long name and channel end short (revision 197's live pass). Once the user
    /// resizes the rail by its edge, by pointer or by arrow keys, their width is kept.
    /// </summary>
    private void FollowRailWidth(double windowWidth)
    {
        ColumnDefinition rail = WindowGrid.ColumnDefinitions[0];
        if (followedRailWidth is not { } followed || Math.Abs(rail.Width.Value - followed) > 0.5)
        {
            followedRailWidth = null;
            return;
        }

        double width = Math.Clamp(Math.Round(windowWidth * 0.14), RailDesignWidth, RailWidestFollowed);
        if (Math.Abs(width - followed) > 0.5)
        {
            fittingColumns = true;
            rail.Width = new GridLength(width);
            followedRailWidth = width;
            fittingColumns = false;
        }
    }

    /// <summary>
    /// Gives the main panes back the least width the window keeps for them when it narrows below what a widened inspector or
    /// rail leaves them: the inspector first, then a rail the user resized, each never below its own least. Its edge never
    /// drags a column into that width; a window narrowed afterwards would otherwise push the inspector out of sight (§6.1).
    /// </summary>
    private void KeepMainPanesWidth(double windowWidth)
    {
        ColumnDefinition rail = WindowGrid.ColumnDefinitions[0];
        ColumnDefinition main = WindowGrid.ColumnDefinitions[1];
        ColumnDefinition inspector = WindowGrid.ColumnDefinitions[2];
        double excess = rail.Width.Value + main.MinWidth + inspector.Width.Value - windowWidth;
        double fromInspector = Math.Clamp(excess, 0, inspector.Width.Value - inspector.MinWidth);
        fittingColumns = true;
        if (fromInspector > 0)
        {
            inspector.Width = new GridLength(inspector.Width.Value - fromInspector);
        }

        // A rail that follows the window is never wider than the smallest window leaves it.
        double fromRail = Math.Clamp(excess - fromInspector, 0, rail.Width.Value - rail.MinWidth);
        if (fromRail > 0 && followedRailWidth is null)
        {
            rail.Width = new GridLength(rail.Width.Value - fromRail);
        }

        fittingColumns = false;
    }

    /// <summary>
    /// The process the channel browser lists channels of: the process rung's own on or below it, the selected process
    /// above it, and none, which lists every channel, otherwise. A group's rung once disabled the browser with a process
    /// selected on it (revision 197's live pass).
    /// </summary>
    private static ProcessInstanceId? ChannelDiscoveryScope(WorkspaceNavigationMemento navigation)
    {
        string? processKey = navigation.Breadcrumb.LastOrDefault(rung => rung.Level == DetailLevel.ProcessInstance)
            ?.Focus?.Key;
        if (processKey is not null && Guid.TryParse(processKey, out Guid id) && id != Guid.Empty)
            return new ProcessInstanceId(id);

        return navigation.Breadcrumb[^1].Level is DetailLevel.Machine or DetailLevel.Group ? navigation.SelectedProcess : null;
    }

    /// <summary>
    /// Asks before the window closes while something it would stop is under way: a recording, a package being saved,
    /// an unfinished capture being finished, or a package an investigation's window makes. Kept going, the window stays
    /// open; stopped, it closes once each has ended, so a recording is saved and no package's unsaved folder outlives
    /// it.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (UnderwayNow() is not { Count: > 0 } stops) return;
        eventArgs.Cancel = true;
        if (closingPrompt) return;
        closingPrompt = true;
        try
        {
            if (await CloseQuestion.For(stops).ShowDialog<bool>(this) && !closed)
            {
                // Once all has stopped, nothing is under way, so the close goes without asking again.
                await StopUnderwayAsync();
                Close();
            }
        }
        finally
        {
            closingPrompt = false;
        }
    }

    /// <summary>What closing the window now would stop, each as the question asked first says it.</summary>
    private List<CloseStop> UnderwayNow()
    {
        List<CloseStop> stops = [];
        if (captureTask is { IsCompleted: false }) stops.Add(CloseStop.Recording);
        if (packageWork is { Work.IsCompleted: false } package) stops.Add(package.Stop);
        if (finishWork is { Work.IsCompleted: false } finish) stops.Add(finish.Stop);
        stops.AddRange(investigations.Select(shown => shown.PackagingStop).OfType<CloseStop>());
        return stops;
    }

    /// <summary>
    /// Stops all that a close stops, and waits until each has ended: a recording saved, as Stop saves it; a package's
    /// unsaved folder removed; a finish stopped with what it derived kept; and each package an investigation makes.
    /// </summary>
    private async Task StopUnderwayAsync()
    {
        bool recording = captureTask is { IsCompleted: false };
        Say(CaptureStatus, recording ? "Stopping and saving before close" : "Stopping before close", aloud: true);

        // Everything is stopped at once, and then waited for.
        if (recording) captureStop?.Cancel();
        List<Task> ending = StopWork();
        if (recording && captureTask is { } capture)
        {
            try
            {
                await capture;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                CaptureDetail.Text = exception.Message + " The broker owner lease still bounds this capture.";
            }
        }

        // A package asked for while the rest stops, here or in an investigation's window, is stopped too: nothing is
        // left running once the window goes.
        for (; ending.Count > 0; ending = StopWork())
        {
            try
            {
                await Task.WhenAll(ending);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Each one's own handler says how it ended.
            }
        }
    }

    /// <summary>Cancels each package and finish under way, here and in each investigation's window; what each ends with.</summary>
    private List<Task> StopWork()
    {
        List<Task> ending = [];
        if (packageWork is { Work.IsCompleted: false } package)
        {
            packaging?.Cancel();
            ending.Add(package.Work);
        }

        if (finishWork is { Work.IsCompleted: false } finish)
        {
            finishingCapture?.Cancel();
            ending.Add(finish.Work);
        }

        ending.AddRange(investigations.Where(shown => shown.PackagingStop is not null)
            .Select(shown => shown.StopPackagingAsync()));
        return ending;
    }

    private void OnThemeModeChanged(object? sender, EventArgs eventArgs)
    {
        workspace.RefreshTheme();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
        MinimapSurface.InvalidateVisual();
        HoverLayer.InvalidateVisual();
    }

    public void Dispose()
    {
        ThemeResources.ModeChanged -= OnThemeModeChanged;
        healthClock.Stop();
        offerRecheck.Stop();
        finishingCapture?.Cancel();
        packaging?.Cancel();
        captureStop?.Cancel();
        captureStop?.Dispose();
        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
    }
}

/// <summary>The two main panes a person may let fill the column (§6.1).</summary>
internal enum MainPane
{
    Graph,
    Timeline,
}
