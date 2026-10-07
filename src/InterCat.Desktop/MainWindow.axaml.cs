using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
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

public sealed partial class MainWindow : Window, IDisposable
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

    /// <summary>A session folder picker is open, so a second one is not started behind it.</summary>
    private bool choosingSession;
    private bool closed;

    /// <summary>A support bundle is being saved, so a second click does not start another.</summary>
    private bool savingSupport;

    /// <summary>The demo is being made or opened, so a second click does not start another.</summary>
    private bool exploringDemo;

    /// <summary>Where the demo is kept and made (§14 M5); InterCat's own folder beside its sessions unless a test says.</summary>
    internal string? DemoRoot { get; set; }

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

    // Whether the splitter between the panes is being dragged, which is kept once it is let go, and whether a split moved
    // otherwise is waiting to be kept once every row it moved has its height.
    private bool draggingSplit;
    private bool splitToKeep;

    /// <summary>
    /// The panes as the shown session's investigation last kept them, or put them back - equal halves with both shown when it
    /// keeps none - or null when that is not known, so the next change is written whatever it is.
    /// </summary>
    private (double GraphShare, WorkspacePane? Expanded)? keptPanes;

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
    /// second, the evidence policy its records are counted under, whether each timeline lane has its own scale, and how its
    /// processes are grouped.
    /// </summary>
    private readonly record struct ViewSettings(RankingMetric RankBy, bool PerSecond, EvidencePolicy Policy, bool ScalesEachLane,
        LaneGrouping Grouping)
    {
        /// <summary>What a session opened on its own starts with, and an investigation that keeps nothing of it puts back.</summary>
        public static ViewSettings Default =>
            new(RankingMetric.Records, false, EvidencePolicy.IncludeCorrelated, false, LaneGrouping.Executable);

        /// <summary>What the shown workspace is set to now, grouped as its person chose (<paramref name="grouping"/>).</summary>
        public static ViewSettings Of(WorkspaceViewModel workspace, LaneGrouping grouping) =>
            new(workspace.RankBy, workspace.PerSecond, workspace.EvidencePolicy, workspace.ScalesEachLane, grouping);
    }

    // Whether the ranked table owns the keyboard as far as the user is concerned (FollowKeyboardOwner).
    private bool railOwnsKeyboard;
    private bool closingPrompt;
    private bool closeAfterCapture;

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
    private DateTimeOffset? lastPublicationUtc;
    private BrokerCaptureHealth? liveHealth;

    // §12.1 S5: the session's size as its newest generation measured it, with the wall-clock moment its capture began,
    // and the limits and volume of a capture that records, from which the window says when it stops.
    private (SessionSize Size, DateTimeOffset? Began)? growth;
    private CaptureLimits? captureLimits;
    private RecordingVolume? recordingVolume;

    // The broker's latest live preview, and how many published chunks the displayed generation was derived from: the
    // preview draws the chunks after those (§12, §19.3).
    private BrokerCapturePreview? livePreview;
    private int displayedChunks;

    // A capture an earlier viewer left unfinished, offered in the rail (§3.1 step 6): where to look for one, the one
    // offered, what its card says, and the finish running for it, whose cancellation is the finish button's second meaning.
    private string? sessionRoot;
    private IReadOnlyList<RecentSessionRow> recentSessions = [];
    private InterruptedFollow? offeredFollow;
    private InterruptedCaptureOffer? offer;
    private CancellationTokenSource? finishingCapture;
    private readonly DispatcherTimer offerRecheck = new() { Interval = TimeSpan.FromSeconds(5) };

    // Which look at the session folder is the latest. The recheck's timer and the user's own actions each start one, and
    // only the latest one's answer is shown: an older one may have read a ticket the user has since forgotten.
    private int offerRefresh;

    // The application's settings (§26.3): where they are kept, when a file is in use, and what reading it found.
    private ApplicationSettingsStore? settingsStore;
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

        // The graph's and the timeline's hover cards are drawn by one layer above every pane (section 6.2).
        HoverLayer.Track(GraphSurface);
        HoverLayer.Track(TimelineSurface);

        // Window shortcuts are handled while the key tunnels down, because a focused list would otherwise
        // consume a letter key for type-ahead and the keyboard path would silently stop working (R15).
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, RequestContextMenuOnShiftF10, RoutingStrategies.Bubble);

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
        workspace = viewModel;
        DataContext = workspace;
        workspace.PropertyChanged += OnWorkspaceChanged;

        // Every list and combo box names its items by their rows' accessible sentences, not their record fields (R15).
        foreach (ItemsControl items in this.GetLogicalDescendants().OfType<ItemsControl>())
        {
            Presentation.AccessibleItems.Name(items);
        }

        // A ranked row in §6.7's multi-selection is marked where it is drawn, and says so to a screen reader; the rows are
        // not rebuilt, so the keyboard focus a Ctrl+Space set out from stays where it was.
        RungList.ContainerPrepared += (_, prepared) => MarkSelectionShare(prepared.Container);
        AddHandler(GotFocusEvent, FollowKeyboardOwner, RoutingStrategies.Bubble, handledEventsToo: true);

        Opened += (_, _) => StartExploringButton.Focus();
        SizeChanged += (_, change) => FollowRailWidth(change.NewSize.Width);
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

    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
        {
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
            case Key.R when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (StartExploringButton.IsEnabled)
                {
                    BeginCapture();
                }
                e.Handled = true;
                break;
            case Key.E when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ExportView();
                e.Handled = true;
                break;
            case Key.T:
                viewModel.ShowTables = !viewModel.ShowTables;
                e.Handled = true;
                break;
            case Key.Enter:
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
                e.Handled = viewModel.ShowEvidence();
                break;
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
            case Key.F5:
                e.Handled = ApplyHeldUpdate();
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
            case Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = viewModel.Ascend();
                break;
            case Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = viewModel.GoForward();
                break;
            case Key.Delete:
                e.Handled = viewModel.RemoveSelectedFilter();
                break;
            default:
                break;
        }

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
            // Enter lists the rung's records, and a screen reader reads the reason beside it.
            if (EmptyEvidenceButton.IsEffectivelyVisible && !ReferenceEquals(focused, EmptyEvidenceButton))
            {
                EmptyEvidenceButton.Focus(NavigationMethod.Directional);
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
    /// Follows who owns the keyboard as far as the user is concerned: the ranked table does once focus moves into it, and
    /// stops only when focus moves to another control. A row rebuilt away under the focus, which leaves focus nowhere or on
    /// the window itself, does not take the keyboard from the table.
    /// </summary>
    private void FollowKeyboardOwner(object? sender, GotFocusEventArgs focus)
    {
        if (focus.Source is not Visual target || ReferenceEquals(target, this)) return;
        railOwnsKeyboard = ReferenceEquals(target, RungList) || RungList.IsVisualAncestorOf(target)
            || ReferenceEquals(target, EmptyEvidenceButton);
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
        if (DataContext is not WorkspaceViewModel viewModel || !viewModel.OpenSearchResult()) return false;
        FocusRail();
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

    private void StartExploring(object? sender, RoutedEventArgs eventArgs) => BeginCapture();

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
        if (request.Handled || request.Source is not ListBoxItem row) return;
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

    /// <summary>§6.7's Ctrl+click on a ranked row: adds it to the multi-selection or removes it, never selecting it alone.</summary>
    private void OnRungListPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !eventArgs.GetCurrentPoint(RungList).Properties.IsLeftButtonPressed
            || (eventArgs.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not RungRow row)
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
        viewModel.ToggleRungInSelection(focused?.DataContext as RungRow ?? viewModel.SelectedRung);
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
            if (!closed && !workspace.CanExport) CaptureDetail.Text = "Nothing to export at this rung yet.";
            return;
        }

        exporting = true;
        try
        {
            if (redacted && !await ConfirmRedactedShareAsync()) return;
            ExportContext context = workspace.DescribeExport(DateTimeOffset.UtcNow);
            Avalonia.Platform.Storage.IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
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
            if (file is null || closed) return;
            string path = file.Path.LocalPath;
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

            Avalonia.Platform.Storage.IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Save support bundle",
                SuggestedFileName = SupportBundle.SuggestedName,
                DefaultExtension = "json",
                FileTypeChoices = [new("JSON support bundle") { Patterns = ["*.json"] }],
            });
            if (file is null || closed)
            {
                return;
            }

            string path = file.Path.LocalPath;
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
    }

    private async void OpenSavedSession(object? sender, RoutedEventArgs eventArgs)
    {
        if (openingSession || choosingSession || packaging is not null || finishingCapture is not null
            || captureTask is { IsCompleted: false }) return;
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders;
        choosingSession = true;
        try
        {
            // The picker starts where InterCat saves sessions, so the user's own captures are the first thing shown.
            Avalonia.Platform.Storage.IStorageFolder? saved = sessionRoot is { } root && Directory.Exists(root)
                ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(root))
                : null;
            folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "Open an InterCat session directory", AllowMultiple = false, SuggestedStartLocation = saved,
            });
        }
        finally
        {
            choosingSession = false;
        }

        if (folders.Count > 0 && !closed) _ = await OpenSessionAsync(folders[0].Path.LocalPath);
    }

    private async void OpenInvestigation(object? sender, RoutedEventArgs eventArgs)
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Open an InterCat investigation",
            AllowMultiple = false,
            FileTypeFilter = [InvestigationFiles],
        });
        if (files.Count > 0 && !closed) _ = ShowInvestigation(files[0].Path.LocalPath);
    }

    private async void NewInvestigation(object? sender, RoutedEventArgs eventArgs)
    {
        Avalonia.Platform.Storage.IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Start an InterCat investigation",
            SuggestedFileName = "investigation" + InvestigationWorkspace.Extension,
            DefaultExtension = InvestigationWorkspace.Extension.TrimStart('.'),
            FileTypeChoices = [InvestigationFiles],
            ShowOverwritePrompt = false,
        });
        if (file is null || closed) return;
        _ = ShowInvestigation(file.Path.LocalPath, create: true);
    }

    private static readonly Avalonia.Platform.Storage.FilePickerFileType InvestigationFiles = new("InterCat investigation")
    {
        Patterns = ["*" + InvestigationWorkspace.Extension],
    };

    /// <summary>
    /// Shows an investigation in a window of its own, beside the session this window shows, making it first when asked
    /// and none is there; a file that already exists is opened, never written over. Opening a member opens it here.
    /// </summary>
    internal InvestigationWindow ShowInvestigation(string path, bool create = false)
    {
        string full = Path.GetFullPath(path);
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

        var window = new InvestigationWindow(full, session => FromInvestigationAsync(full, () => OpenSessionAsync(session)), made,
            (session, interval) => FromInvestigationAsync(full, () => OpenSessionAtAsync(session, interval)));
        window.Show(this);
        return window;
    }

    /// <summary>Opens a member from its investigation, which then keeps its pins and view settings (§26.3).</summary>
    private async Task<bool> FromInvestigationAsync(string investigation, Func<Task<bool>> open)
    {
        openingFromInvestigation = investigation;
        try
        {
            return await open();
        }
        finally
        {
            openingFromInvestigation = null;
        }
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
                    layout.Grouping ?? LaneGrouping.Executable),
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
                settings.ScalesEachLane, CultureInfo.CurrentCulture),
            .. panes is null ? [] : WorkspacePanes.Parts(panes.GraphShare, panes.Expanded, CultureInfo.CurrentCulture),
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
        ViewSettings settings = ViewSettings.Of(workspace, laneGrouping);
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
            pinnedLanes, settings.Grouping));
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
        (double share, WorkspacePane? expanded) = PanesNow();
        (double GraphShare, WorkspacePane? Expanded) keeping = (WorkspacePanes.Kept(share), expanded);
        if (keptPanes == keeping)
        {
            return;
        }

        keptPanes = keeping;
        Task<WorkspacePanes?> written = WriteToInvestigation(() =>
            InvestigationWorkspace.SetPanes(home.Workspace, keeping.GraphShare, keeping.Expanded, DateTimeOffset.UtcNow));
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
                CaptureDetail.Text += " The window's panes could not be kept in the investigation: " + exception.Message;
            }

            return;
        }

        foreach (InvestigationWindow shown in OwnedWindows.OfType<InvestigationWindow>())
        {
            shown.PanesKept(investigation, panes);
        }
    }

    /// <summary>
    /// The panes as a person left them: the graph's share of the height the two share - the split they return to, while one
    /// fills the column - and the pane filling it, if one does.
    /// </summary>
    private (double GraphShare, WorkspacePane? Expanded) PanesNow()
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
        });
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
                CaptureDetail.Text += " The pins and view settings could not be kept in the investigation: " + exception.Message;
            }

            return;
        }

        foreach (InvestigationWindow shown in OwnedWindows.OfType<InvestigationWindow>())
        {
            shown.LayoutKept(home.Workspace, home.Session, layout);
        }
    }

    /// <summary>
    /// Completes once every change kept in the shown session's investigation - its layout and the window's panes - is
    /// written there.
    /// </summary>
    internal Task InvestigationWritten => investigationWritten;

    /// <summary>
    /// Opens a session - or keeps it, when it is the one shown - with its timeline zoomed to <paramref name="interval"/> of
    /// its own time, with a column of it either side, and that interval selected, so its records are what this window shows.
    /// </summary>
    internal async Task<bool> OpenSessionAtAsync(string path, TimeRange interval)
    {
        string full = Path.GetFullPath(path);
        bool shown = currentSessionPath is { } current
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)), Path.TrimEndingDirectorySeparator(full),
                StringComparison.OrdinalIgnoreCase);
        if (!shown && !await OpenSessionAsync(full) || closed)
        {
            return false;
        }

        // A column may reach past the session's own records, before or after its capture: only what the session holds of it
        // is selected, and nothing when it holds none of it - the session is then shown whole.
        TimeRange extent = workspace.Snapshot.Extent;
        if (interval.StartTicks >= extent.EndTicks || interval.EndTicks <= extent.StartTicks)
        {
            return true;
        }

        TimeRange held = new(Math.Max(interval.StartTicks, extent.StartTicks), Math.Min(interval.EndTicks, extent.EndTicks));
        long span = Math.Max(held.EndTicks - held.StartTicks, 1);
        TimelineSurface.SetViewport(new TimeRange(held.StartTicks - span, held.EndTicks + span));
        workspace.SelectInterval(held);
        return true;
    }

    /// <summary>
    /// Opens a published session directory as the workspace. A redacted package says so where the status is read, so
    /// nobody mistakes its pseudonyms for the source machine's names and IDs. False when the directory could not open;
    /// the current workspace is then unchanged.
    /// </summary>
    internal async Task<bool> OpenSessionAsync(string path, (string Headline, string Detail)? status = null)
    {
        if (openingSession || captureTask is { IsCompleted: false }) return false;
        openingSession = true;
        StartExploringButton.IsEnabled = false;
        OpenSavedSessionButton.IsEnabled = false;
        try
        {
            CaptureStatus.Text = "Opening saved session";
            string? investigation = openingFromInvestigation;
            (SessionStore store, SessionOverviewBundle overview) = await Task.Run(() =>
            {
                SessionStore opened = SharedSessionStores.Open(path);

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
            if (closed) return false;
            evidencePolicy = overview.Policy;
            int run = ++captureRunId;
            heldUpdate = null;
            CaptureSummary.Text = string.Empty;
            (string headline, string detail) = overview.Redaction is { } redaction
                ? ("Redacted session package open", redaction.Statement(CultureInfo.CurrentCulture))
                : status ?? ("Saved session open",
                    overview.Edges.Any(edge => edge.Mechanism == Mechanism.Rpc)
                        ? "This is a published generation. The graph contains admitted paired TCP and RPC calls linked "
                            + "through ALPC; a process's RPC calls are on its rung, and all other activity is in the timeline."
                        : "This is a published generation. The graph contains admitted paired TCP; a process's RPC calls are "
                            + "on its rung, and all other observed activity remains in the timeline.");

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
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            CaptureStatus.Text = "Could not open this session";
            CaptureDetail.Text = exception.Message + " The current workspace is unchanged.";
            return false;
        }
        finally
        {
            openingSession = false;
            if (!closed)
            {
                StartExploringButton.IsEnabled = true;
                OpenSavedSessionButton.IsEnabled = true;
                UpdateEvidenceAction();
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

        // Another open or a capture that began meanwhile supersedes this session's view; its next lease measures again.
        if (closed || run != captureRunId || report.Verified || openingSession || captureTask is { IsCompleted: false })
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
        UpdateThemeMenu();
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

    private async Task RefreshRecentSessionsAsync()
    {
        if (closed || sessionRoot is not { } root) return;
        IReadOnlyList<RecentSessionRow> rows = await Task.Run(() => RecentSessions.Find(
            root, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture));
        if (closed) return;
        recentSessions = rows;
        RecentSessionsList.ItemsSource = rows;
        UpdateBeforeSessionVisibility();
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
    /// What the rail offers only before any session is shown and while no capture runs: the saved sessions, what exploring
    /// records, and the demo to try (§14 M5). Once a session is shown the ranked list needs the rail's height - three rows
    /// at the smallest window - and the demo waits in the Investigation menu.
    /// </summary>
    private void UpdateBeforeSessionVisibility()
    {
        bool idle = phase is not (CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing);
        bool first = workspace.IsEmptyWorkspace && idle;
        RecentSessionsPanel.IsVisible = recentSessions.Count > 0 && first;
        CaptureIntro.IsVisible = first;
        ExploreDemoButton.IsVisible = first;
    }

    private void OpenRecentSession(object? sender, TappedEventArgs eventArgs) => _ = OpenSelectedRecentSessionAsync();

    /// <summary>
    /// Opens the recent session the keyboard is on, or else the one selected: Tab reaches a row without selecting it.
    /// False when there is none, or it could not open.
    /// </summary>
    internal async Task<bool> OpenSelectedRecentSessionAsync()
    {
        RecentSessionRow? row = (FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as RecentSessionRow
            ?? RecentSessionsList.SelectedItem as RecentSessionRow;
        return row is not null && await OpenSessionAsync(row.Path);
    }

    /// <summary>How the window looks for an unfinished capture in the session folder; a test holds one look back with it.</summary>
    internal Func<string, InterruptedFollow?> InterruptedCaptureFinder { get; set; } = FindInterruptedCapture;

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
            if (await OpenSessionAsync(follow.Ticket.SessionDirectory, ("Partial session open", shown.Detail)))
            {
                // Nothing further can be derived for it: once seen, it is not offered again.
                _ = LiveFollowTicket.Remove(follow.Ticket.SessionDirectory);
                await RefreshInterruptedOfferAsync();
            }

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
            InterruptedFollowResult result = await Task.Run(() =>
            {
                InterruptedFollowResult finished = InterruptedFollow.Finish(follow.Ticket, progress, cancellationToken: finishing.Token);

                // A finished session is complete, so its derivation checkpoint lets it reopen without reading every
                // record; one that is not published only costs that reopen time (derivation-checkpoint-v1).
                if (finished.Completed)
                {
                    _ = SessionCheckpoints.Publish(finished.Session, DateTimeOffset.UtcNow, finishing.Token);
                }

                return finished;
            });

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
            ShowInterruptedOffer(follow);
            UnfinishedCaptureDetail.Text = exception.Message + " Your session keeps what was derived before this.";
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

        if (currentSessionPath is not { } source || displayedOverview is not { } overview || IsLive || exporting)
        {
            return;
        }

        if (await ConfirmRedactedPackageAsync(overview, workspace.ScopeInterval) is not { } chosen) return;
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Choose where to save the redacted session package",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || closed) return;
        string destination = NewPackageDirectory(folders[0].Path.LocalPath, DateTimeOffset.Now);
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

        if (currentSessionPath is not { } source || displayedOverview is null || IsLive || exporting)
        {
            return;
        }

        OriginalEvidencePackagePreview preview;
        try
        {
            preview = await Task.Run(() => OriginalEvidencePackage.Preview(SharedSessionStores.Open(source)));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            CaptureDetail.Text = "Could not read the session to package it: " + exception.Message;
            return;
        }

        if (closed || !await ConfirmOriginalPackageAsync(preview)) return;
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = preview.Redacted
                ? "Choose where to save a copy of this redacted package"
                : "Choose where to save the original, unredacted session",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || closed) return;
        string destination = NewPackageDirectory(folders[0].Path.LocalPath, DateTimeOffset.Now,
            preview.Redacted ? "intercat-redacted-session" : "intercat-original-session");
        OriginalEvidencePackageResult? result = await WriteOriginalPackageAsync(source, destination, preview.Redacted);
        if (result is not null && !closed && await ShowOriginalResultAsync(result))
        {
            _ = await OpenSessionAsync(result.Directory);
        }
    }

    /// <summary>
    /// Makes the original package off the UI thread, stating its progress on the capture card, and returns null when it
    /// was cancelled or refused - the card then says which, and the source session is unchanged either way.
    /// </summary>
    internal async Task<OriginalEvidencePackageResult?> WriteOriginalPackageAsync(string source, string destination,
        bool redacted = false)
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
            CaptureDetail.Text = update.Total > 0 ? $"{stage}… {update.Done * 100 / update.Total}%" : stage + "…";
        });
        try
        {
            // Each file is hashed as it is copied, so the session's shared store, which listed its files, is enough.
            OriginalEvidencePackageResult result = await Task.Run(() => OriginalEvidencePackage.Create(
                SharedSessionStores.Open(source), destination, progress, cancellation.Token), cancellation.Token);
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
            CaptureDetail.Text = update.Total > 0
                ? $"{stage}… {update.Done * 100 / update.Total}%"
                : stage + "…";
        });
        try
        {
            RedactedSessionPackageResult result = await Task.Run(() => RedactedSessionPackage.Create(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(source)), destination, DateTimeOffset.UtcNow,
                interval, progress, cancellation.Token), cancellation.Token);
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

    private async void BeginCapture()
    {
        if (openingSession || finishingCapture is not null || captureTask is { IsCompleted: false })
        {
            return;
        }

        captureStop?.Dispose();
        captureStop = new CancellationTokenSource();
        int run = ++captureRunId;
        ForgetDisplayedSession();
        evidencePolicy = EvidencePolicy.IncludeCorrelated;
        laneGrouping = LaneGrouping.Executable;
        ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Preparing Explore",
            "Windows may ask for administrator approval to record system-wide events."));
        try
        {
            // Each publication is projected under the policy the window holds when it is projected, so a person's choice
            // made while recording holds from the next one on.
            var options = new CaptureRunOptions { EvidencePolicy = () => evidencePolicy };
            captureTask = Task.Run(() => DesktopCaptureRunner.RunAsync(
                update => ReceiveCaptureUpdate(run, update), captureStop.Token, options));
            await captureTask;
        }
        catch (Exception exception)
        {
            ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture interrupted",
                exception.Message + " Any published evidence is retained in the session shown below."));
        }
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
        UpdateSessionGrowth();
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
    /// Shows one capture or open-session update. A newer generation of the displayed session replaces the workspace,
    /// except while the user reads evidence, when it is held and offered instead.
    /// </summary>
    internal void ApplyCaptureUpdate(CaptureUiUpdate update, bool forceOverview = false)
    {
        phase = update.Phase;
        CaptureStatus.Text = update.Headline;
        bool unavailable = update.Phase == CaptureUiPhase.Unavailable;
        StartExploringButton.Content = unavailable ? "Retry exploring (Ctrl+R)" : "Start exploring (Ctrl+R)";
        ToolTip.SetTip(StartExploringButton, unavailable ? update.Detail : null);
        // An unavailable capture is a warning in words: caution ink, never a mechanism's hue (§6.6).
        CaptureStatus.Classes.Set("caution", unavailable);

        // A repeated detail (a live-counter update) keeps any navigation notice the last refresh appended to it.
        if (!string.Equals(update.Detail, appliedDetail, StringComparison.Ordinal))
        {
            appliedDetail = update.Detail;
            CaptureDetail.Text = update.Detail;
        }

        CaptureLatency.Text = Freshness(update.Milestones);
        if (update.Summary is not null) CaptureSummary.Text = update.Summary;
        if (update.SessionPath is not null) CaptureSessionPath.Text = update.SessionPath;
        bool busy = update.Phase is CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StartExploringButton.IsEnabled = !busy;
        OpenSavedSessionButton.IsEnabled = !busy;
        InvestigationButton.IsEnabled = !busy;

        // While a capture runs the card holds only what can be done now - stop it, pause the view - and its state. The
        // actions that wait for it to end, and the words about starting one, give the ranked list the rail's height.
        StartExploringButton.IsVisible = !busy;
        OpenSavedSessionButton.IsVisible = !busy;
        InvestigationButton.IsVisible = !busy;
        UnfinishedCaptureCard.IsVisible = !busy && offer is not null;
        StopCaptureButton.IsVisible = update.Phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StopCaptureButton.IsEnabled = update.Phase == CaptureUiPhase.Recording
            && captureStop?.IsCancellationRequested != true;
        FollowButton.IsVisible = IsLive;
        liveHealth = IsLive ? update.LiveHealth : null;
        livePreview = update.Phase == CaptureUiPhase.Recording ? update.LivePreview : null;

        // The newest generation measures the session, whether it is shown or held while the user reads records.
        captureLimits = update.Limits;
        recordingVolume = update.Volume;
        if (update.Overview is { Size: { } measured } newest)
        {
            growth = (measured, newest.Began);
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
        displayedChunks);

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
    private void PutBackPanes(WorkspacePanes panes) =>
        LayPanes(panes.Expanded switch
        {
            WorkspacePane.Graph => MainPane.Graph,
            WorkspacePane.Timeline => MainPane.Timeline,
            _ => null,
        }, (new GridLength(panes.GraphShare, GridUnitType.Star), new GridLength(1 - panes.GraphShare, GridUnitType.Star)));

    private void CountCorrelatedOnly(object? sender, RoutedEventArgs eventArgs) =>
        _ = ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCorrelated);

    private void RestoreDefaultView(object? sender, RoutedEventArgs eventArgs) => _ = RestoreDefaultViewAsync();

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
        workspace.RankBy = defaults.RankBy;
        workspace.PerSecond = defaults.PerSecond;
        workspace.ScalesEachLane = defaults.ScalesEachLane;

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
    /// publication it shows. False when nothing is shown, or it is already counted so.
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

        SessionOverviewBundle overview = await Task.Run(
            () => SessionOverviewProjector.Project(SharedSessionStores.Open(path, shown.SessionId), policy));

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
        // Every read of the workspace binds records as the overview it shows was projected: under its policy.
        SessionEvidenceSource? evidence = currentSessionPath is { } path
            ? new SessionEvidenceSource(path, overview.SessionId, overview.Generation, overview.Policy)
            : null;
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
            if (openingFromInvestigation is { } investigation && LayoutKeptIn(investigation, overview.SessionId) is { } kept)
            {
                // The window's panes are the investigation's, for every session opened from it: put back when it keeps them,
                // and left as they are, as for a session opened on its own, when it keeps none.
                layoutHome = (investigation, overview.SessionId);
                pins = kept.Pins;
                lanes = keptLanes = kept.Lanes;
                restored = keptSettings = kept.Settings;
                laneGrouping = kept.Settings.Grouping;
                keptPanes = kept.Panes is { } panes
                    ? (WorkspacePanes.Kept(panes.GraphShare), panes.Expanded)
                    : (WorkspacePanes.EqualShare, null);
                if (kept.Panes is { } put)
                {
                    PutBackPanes(put);
                }

                pinsNotice = $"Its pins and view settings are kept in the investigation {Path.GetFileName(investigation)}"
                    + PutBack(kept.Pins.Count, kept.Lanes.Count,
                        kept.Settings with { Policy = overview.Policy, Grouping = GroupingShown(projected, laneGrouping) },
                        kept.Panes);
            }

            keptPins = pins;
        }

        // The graph's identity names the grouping too, since its nodes are the groups.
        LaneGrouping grouping = GroupingShown(projected, laneGrouping);
        var replacement = new WorkspaceViewModel(WorkspaceGrouping.Regroup(projected, grouping),
            WorkspaceGrouping.Identity(overview.GraphIdentity, grouping), evidence,
            savedNavigation is null ? null : workspace.LaidOutPositions, pins);
        if (restored is { } settings)
        {
            replacement.RankBy = settings.RankBy;
            replacement.PerSecond = settings.PerSecond;
            replacement.ScalesEachLane = settings.ScalesEachLane;
        }

        // The lanes its investigation kept pinned are pinned before any group's lanes are counted, so they are drawn first.
        replacement.PinLanes(lanes);

        if (pinsNotice is not null)
        {
            CaptureDetail.Text += " " + pinsNotice;
        }
        if (savedNavigation is not null
            && replacement.RestoreNavigation(savedNavigation) is { } navigationNotice)
        {
            CaptureDetail.Text += " " + navigationNotice;
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
        HeldFollowButton.IsVisible = held && !followLatest;
        HeldBannerText.Text = !held ? string.Empty
            : (followLatest
                ? $"Showing generation {displayedGeneration:N0} while you read records. "
                : $"View paused at generation {displayedGeneration:N0}. ")
                + $"Generation {heldUpdate!.Overview!.Generation:N0} is published; recording continues.";
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
    /// §12.1 S5: the session's size, bytes per record and tier, as its newest generation measured them, and while it
    /// records, when its capture's limits stop it - in `icat session`'s and `icat capture`'s words (R18). It reads as a
    /// caution when that stop is a minute away, or when the session is past the sizes any release is qualified at.
    /// </summary>
    private void UpdateSessionGrowth()
    {
        if (growth is not ({ } size, var began))
        {
            SessionGrowthText.Text = string.Empty;
            SessionGrowthText.IsVisible = false;
            SessionGrowthText.Classes.Set("caution", false);
            return;
        }

        CaptureHeadroom? headroom = phase == CaptureUiPhase.Recording && captureLimits is { } limits && began is { } start
            ? SessionGrowth.Headroom(size, start, DateTimeOffset.UtcNow, limits, recordingVolume)
            : null;
        SessionGrowthText.Text = SessionGrowth.Statement(size, headroom, stopOnItsOwnLine: true);
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
    /// of whose processes are in it, and an item status a screen reader announces with the row.
    /// </summary>
    private void MarkSelectionShare(Control container)
    {
        SelectionShare share = RungList.ItemFromContainer(container) is RungRow row ? workspace.ShareOf(row) : SelectionShare.None;
        container.Classes.Set("chosen", share == SelectionShare.All);
        container.Classes.Set("chosenPart", share == SelectionShare.Some);
        if (share == SelectionShare.None)
        {
            container.ClearValue(AutomationProperties.ItemStatusProperty);
        }
        else
        {
            AutomationProperties.SetItemStatus(container,
                share == SelectionShare.All ? "in the selection" : "partly in the selection");
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
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ChosenProcesses) or nameof(WorkspaceViewModel.HasMultiSelection))
        {
            MarkSelectionShares();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.RungRows) or nameof(WorkspaceViewModel.Crumbs))
        {
            KeepRailKeyboard();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.PinnedGraphNodeKeys) or nameof(WorkspaceViewModel.PinnedLanes)
                or nameof(WorkspaceViewModel.RankBy) or nameof(WorkspaceViewModel.PerSecond) or nameof(WorkspaceViewModel.ScalesEachLane)
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
            rail.Width = new GridLength(width);
            followedRailWidth = width;
        }
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

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (closeAfterCapture || captureTask is not { IsCompleted: false })
        {
            return;
        }

        eventArgs.Cancel = true;
        if (closingPrompt) return;
        closingPrompt = true;
        try
        {
            if (await StopExplorePrompt().ShowDialog<bool>(this))
            {
                captureStop?.Cancel();
                CaptureStatus.Text = "Stopping and saving before close";
                if (captureTask is { } task)
                {
                    try { await task; }
                    catch (Exception exception)
                    {
                        CaptureDetail.Text = exception.Message
                            + " The broker owner lease still bounds this capture.";
                    }
                }
                closeAfterCapture = true;
                Close();
            }
        }
        finally
        {
            closingPrompt = false;
        }
    }

    /// <summary>
    /// Asked when the window closes while Explore records; true to stop, save and close. Escape, like the focused first
    /// button, keeps recording.
    /// </summary>
    internal static Window StopExplorePrompt()
    {
        var prompt = new Window
        {
            Title = "Stop Explore?", Width = 410,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var stay = new Button { Content = "Keep recording" };
        var stop = new Button { Content = "Stop, save, and close" };
        stay.Click += (_, _) => prompt.Close(false);
        stop.Click += (_, _) => prompt.Close(true);
        prompt.Opened += (_, _) => stay.Focus();
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
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "The broker will stop and finalize. All published evidence stays saved.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { stay, stop } },
            },
        };
        return prompt;
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
