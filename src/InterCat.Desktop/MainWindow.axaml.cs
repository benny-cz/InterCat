using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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

    // The investigation a session is being opened from, while it is; then the one that keeps the shown session's pins
    // (§26.3), with the pins it last kept and the writes of them in order. A session opened on its own keeps none there.
    private string? openingFromInvestigation;
    private (string Workspace, Guid Session)? layoutHome;
    private IReadOnlyDictionary<string, GraphPoint>? keptPins;

    /// <summary>The ranking last kept in the shown session's investigation, or null when none was kept or read yet.</summary>
    private (RankingMetric RankBy, bool PerSecond)? keptRanking;

    /// <summary>The evidence policy last kept in the shown session's investigation; null when none is kept there.</summary>
    private EvidencePolicy? keptPolicy;
    private Task pinsWritten = Task.CompletedTask;

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
    private DateTimeOffset? lastPublicationUtc;
    private BrokerCaptureHealth? liveHealth;

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
                e.Handled = viewModel.TogglePinSelectedGraphNode();
                break;
            case Key.L when e.KeyModifiers == KeyModifiers.None:
                e.Handled = viewModel.RelayoutGraph();
                break;
            case Key.Escape:
                _ = viewModel.Ascend();
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
                new TextBlock { Text = "Included: relative times, counts, sizes, status, quality, and random relationship tokens consistent only within this file.",
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

    /// <summary>Opens a member from its investigation, which then keeps its pins, ranking and evidence policy (§26.3).</summary>
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
    /// What member <paramref name="sessionId"/> keeps in <paramref name="investigation"/>: its pins, none when it keeps none,
    /// its ranking, records counted whole when it keeps none, and its evidence policy, correlated evidence when it keeps
    /// none; null when the file cannot be read or does not name the session, which then keeps them only while it is open.
    /// </summary>
    private static (Dictionary<string, GraphPoint> Pins, RankingMetric RankBy, bool PerSecond, EvidencePolicy Policy)? LayoutKeptIn(
        string investigation,
        Guid sessionId)
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
                layout?.RankBy ?? RankingMetric.Records, layout?.PerSecond ?? false,
                layout?.EvidencePolicy ?? EvidencePolicy.IncludeCorrelated);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What an investigation put back of a session's pins, ranking and evidence policy, ending its notice: ", which put back
    /// 2 pins."
    /// </summary>
    private static string PutBack(int pins, RankingMetric rankBy, bool perSecond, EvidencePolicy policy)
    {
        string? pinned = pins == 0 ? null : string.Create(CultureInfo.CurrentCulture, $"{pins:N0} {(pins == 1 ? "pin" : "pins")}");
        string? ranked = rankBy == RankingMetric.Records && !perSecond
            ? null
            : $"its ranking by {RankingMetrics.Phrase(rankBy)}{(perSecond ? " per second" : string.Empty)}";
        string? counted = policy == EvidencePolicy.IncludeCandidates ? "its counting of candidates" : null;
        string[] restored = [.. new[] { pinned, ranked, counted }.OfType<string>()];
        return restored.Length switch
        {
            0 => ".",
            1 => $", which put back {restored[0]}.",
            _ => $", which put back {string.Join(", ", restored[..^1])} and {restored[^1]}.",
        };
    }

    /// <summary>
    /// Keeps the shown session's pins, ranking and evidence policy in the investigation it was opened from, when they changed:
    /// one write after another, off the UI thread, and a write that fails is said beside the session's status rather than
    /// lost silently.
    /// </summary>
    private void KeepLayout((string Workspace, Guid Session) home)
    {
        IReadOnlyDictionary<string, GraphPoint> pins = workspace.GraphPins;
        (RankingMetric RankBy, bool PerSecond) ranking = (workspace.RankBy, workspace.PerSecond);
        EvidencePolicy policy = workspace.EvidencePolicy;
        if (keptPins is { } kept && kept.Count == pins.Count
            && pins.All(pin => kept.TryGetValue(pin.Key, out GraphPoint at) && at == pin.Value)
            && keptRanking == ranking && keptPolicy == policy)
        {
            return;
        }

        keptPins = pins;
        keptRanking = ranking;
        keptPolicy = policy;
        WorkspacePin[] layout = [.. pins.Select(pin => new WorkspacePin { Key = pin.Key, X = pin.Value.X, Y = pin.Value.Y })];
        Task previous = pinsWritten;
        pinsWritten = Task.Run(async () =>
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

            InvestigationWorkspace.SetLayout(home.Workspace, home.Session, layout, DateTimeOffset.UtcNow, ranking.RankBy, ranking.PerSecond,
                policy);
        });
        _ = SayIfPinsNotKeptAsync(pinsWritten);
    }

    private async Task SayIfPinsNotKeptAsync(Task written)
    {
        try
        {
            await written;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            if (!closed)
            {
                keptPins = null;
                keptRanking = null;
                keptPolicy = null;
                CaptureDetail.Text += " The pins, ranking and evidence policy could not be kept in the investigation: " + exception.Message;
            }
        }
    }

    /// <summary>Completes once every change to the shown session's layout is written to its investigation.</summary>
    internal Task PinsWritten => pinsWritten;

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
                    ? kept.Policy
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
                ? ("Redacted session package open", SessionRedaction.Summary + " " + redaction.Warning)
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
        UpdateRecentSessionsVisibility();
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
    private void UpdateRecentSessionsVisibility() =>
        RecentSessionsPanel.IsVisible = recentSessions.Count > 0 && workspace.IsEmptyWorkspace
            && phase is not (CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing);

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

        if (!await ConfirmRedactedPackageAsync(overview)) return;
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Choose where to save the redacted session package",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || closed) return;
        string destination = NewPackageDirectory(folders[0].Path.LocalPath, DateTimeOffset.Now);
        RedactedSessionPackageResult? result = await WriteRedactedPackageAsync(source, destination);
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
    internal async Task<RedactedSessionPackageResult?> WriteRedactedPackageAsync(string source, string destination)
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
                progress, cancellation.Token), cancellation.Token);
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

    private async Task<bool> ConfirmRedactedPackageAsync(SessionOverviewBundle overview) =>
        await RedactedPackagePrompt(overview).ShowDialog<bool>(this);

    /// <summary>What a redacted session package keeps, replaces and leaves out, asked before its folder is chosen.</summary>
    internal static Window RedactedPackagePrompt(SessionOverviewBundle overview)
    {
        var prompt = new Window
        {
            Title = "Share a redacted session package?", Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var cancel = new Button { Content = "Cancel" };
        var proceed = new Button { Content = "Choose a folder…" };
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
                Paragraph("This saves a new session folder that opens in InterCat, for someone else to explore. The "
                    + "session you have open is not changed."),
                Paragraph($"Kept: every record, {overview.ObservationRows:N0} in all, with its time, size, status and quality; "
                    + $"every process, {overview.Nodes.Count:N0} in all, and how they relate; coverage and loss."),
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
                Paragraph($"Saved {Spoken.Count(result.Counts.Rows, "record")} to {result.Directory}."),
                Paragraph($"Before it was saved, the package was reopened as a recipient would open it, every value "
                    + $"was checked against the pseudonyms it issued, and {result.FilesVerified:N0} files were searched "
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
        UpdateHeldBanner();
        UpdateEvidenceAction();
        CaptureSummary.Text = string.Empty;
        CaptureSessionPath.Text = string.Empty;
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
        CaptureIntro.IsVisible = !busy;
        UnfinishedCaptureCard.IsVisible = !busy && offer is not null;
        StopCaptureButton.IsVisible = update.Phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StopCaptureButton.IsEnabled = update.Phase == CaptureUiPhase.Recording
            && captureStop?.IsCancellationRequested != true;
        FollowButton.IsVisible = IsLive;
        liveHealth = IsLive ? update.LiveHealth : null;
        livePreview = update.Phase == CaptureUiPhase.Recording ? update.LivePreview : null;
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
        UpdateRecentSessionsVisibility();
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
            UpdateRecentSessionsVisibility();
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

    private void CountCorrelatedOnly(object? sender, RoutedEventArgs eventArgs) =>
        _ = ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCorrelated);

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

    private void ReplaceWorkspace(CaptureUiUpdate update, SessionOverviewBundle overview, bool forceOverview)
    {
        WorkspaceNavigationMemento? savedNavigation = !forceOverview && overview.SessionId == displayedSessionId
            ? workspace.CaptureNavigation() : null;
        heldUpdate = null;
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
        string? pinsNotice = null;
        (RankingMetric RankBy, bool PerSecond)? restoredRanking = null;
        if (savedNavigation is null)
        {
            // A session opened from an investigation it is a member of keeps its pins, ranking and evidence policy there,
            // and gets back those it kept (§26.3); any other session keeps them only while it is open. Its policy was put
            // back before its overview was projected, under it.
            layoutHome = null;
            keptRanking = null;
            keptPolicy = null;
            if (openingFromInvestigation is { } investigation && LayoutKeptIn(investigation, overview.SessionId) is { } kept)
            {
                layoutHome = (investigation, overview.SessionId);
                pins = kept.Pins;
                restoredRanking = keptRanking = (kept.RankBy, kept.PerSecond);
                keptPolicy = kept.Policy;
                pinsNotice = $"Its pins, ranking and evidence policy are kept in the investigation {Path.GetFileName(investigation)}"
                    + PutBack(kept.Pins.Count, kept.RankBy, kept.PerSecond, overview.Policy);
            }

            keptPins = pins;
        }

        var replacement = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity, evidence,
            savedNavigation is null ? null : workspace.LaidOutPositions, pins);
        if (restoredRanking is { } ranking)
        {
            replacement.RankBy = ranking.RankBy;
            replacement.PerSecond = ranking.PerSecond;
        }

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
            replacement.AdoptTimeline(workspace.CarryTimeline());
            replacement.AdoptScope(workspace.CarryScope());
        }

        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
        workspace = replacement;
        workspace.PropertyChanged += OnWorkspaceChanged;
        DataContext = workspace;
        UpdateRecentSessionsVisibility();
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
                : displayedOverview.Redaction is null ? "Saved session" : "Redacted package",
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

        MechanismCoverage[] collected = [.. overview.MechanismCoverage
            .Where(entry => entry.State != CoverageState.NotCollected)];
        MechanismCoverage[] affected = [.. collected.Where(entry => entry.State != CoverageState.Covered)];
        return collected.Length == 0 ? "No mechanism was collected"
            : affected.Length == 0 ? "No loss reported"
            : string.Join(" · ", affected.Select(entry => $"{EvidenceRowText.MechanismName(entry.Mechanism)} {Describe(entry.State)}"));

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
            or nameof(WorkspaceViewModel.ShowsProcessLanes))
        {
            Dispatcher.UIThread.Post(TimelineSurface.BringSelectedProcessLaneIntoView);
        }
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ChosenProcesses) or nameof(WorkspaceViewModel.HasMultiSelection))
        {
            MarkSelectionShares();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.RungRows) or nameof(WorkspaceViewModel.Crumbs))
        {
            KeepRailKeyboard();
        }

        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.PinnedGraphNodeKeys) or nameof(WorkspaceViewModel.RankBy)
                or nameof(WorkspaceViewModel.PerSecond)
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
