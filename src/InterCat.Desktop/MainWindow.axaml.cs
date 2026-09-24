using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using InterCat.Analysis;
using InterCat.Application;
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
    private bool closed;
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
    private DateTimeOffset? lastPublicationUtc;
    private readonly DispatcherTimer healthClock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow() : this(new WorkspaceViewModel(OverviewWorkspace.Empty(), "empty-workspace"))
    {
    }

    public MainWindow(WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();

        // Window shortcuts are handled while the key tunnels down, because a focused list would otherwise
        // consume a letter key for type-ahead and the keyboard path would silently stop working (R15).
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);

        // The current rung is the last crumb. When a descent or a long channel name widens the trail, it scrolls so
        // that crumb stays in view instead of being clipped behind the level badge (section 3.2, position stated).
        CrumbScroller.ScrollChanged += (_, change) =>
        {
            if (change.ExtentDelta.X != 0 || change.ViewportDelta.X != 0)
            {
                // ScrollToEnd means bottom-left in Avalonia; the trail's end is its right edge.
                CrumbScroller.Offset = new(Math.Max(0, CrumbScroller.Extent.Width - CrumbScroller.Viewport.Width), 0);
            }
        };
        workspace = viewModel;
        DataContext = workspace;
        workspace.PropertyChanged += OnWorkspaceChanged;
        Opened += (_, _) => StartExploringButton.Focus();
        healthClock.Tick += (_, _) => UpdateHealthStrip();
        healthClock.Start();
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
    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel || e.Source is TextBox)
        {
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
            case Key.M when viewModel.CanLoadMoreEvidence:
                _ = viewModel.LoadMoreEvidenceAsync();
                e.Handled = true;
                break;
            case Key.F5:
                e.Handled = ApplyHeldUpdate();
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                e.Handled = ToggleFollowLatest();
                break;
            case Key.Escape:
                _ = viewModel.Ascend();
                e.Handled = true;
                break;
            case Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = viewModel.Ascend();
                break;
            case Key.Delete:
                e.Handled = viewModel.RemoveSelectedFilter();
                break;
            default:
                break;
        }
    }

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

    private void AscendOrClear(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            _ = viewModel.Ascend();
        }
    }

    private void ToggleTables(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.ShowTables = !viewModel.ShowTables;
        }
    }

    private void StartExploring(object? sender, RoutedEventArgs eventArgs) => BeginCapture();

    /// <summary>The pointer equivalent of E: one step to the current rung's source records (section 3.2).</summary>
    private void ShowSourceRecords(object? sender, RoutedEventArgs eventArgs) => _ = workspace.ShowEvidence();

    private void LoadMoreRecords(object? sender, RoutedEventArgs eventArgs) => _ = workspace.LoadMoreEvidenceAsync();

    private void ExportView(object? sender, RoutedEventArgs eventArgs) => ExportView();

    /// <summary>
    /// Exports the applied view (§6.4, §6.7 Ctrl+E): the rung's ranked rows or loaded evidence records, named by session,
    /// generation, rung, filters and interval. The user chooses the file; nothing is written anywhere else.
    /// </summary>
    private async void ExportView()
    {
        if (exporting || workspace.ExportRowCount == 0)
        {
            if (!closed && workspace.ExportRowCount == 0) CaptureDetail.Text = "Nothing to export at this rung yet.";
            return;
        }

        exporting = true;
        try
        {
            ExportContext context = workspace.DescribeExport(DateTimeOffset.UtcNow);
            Avalonia.Platform.Storage.IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Export this view",
                SuggestedFileName = WorkspaceExport.SuggestedName(context, "json"),
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
            int rows = await WriteExportAsync(path, format);
            if (!closed)
                CaptureDetail.Text = $"Exported {rows:N0} rows ({(context.Complete ? "complete" : "the loaded part of the scope")}) to {path}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed) CaptureDetail.Text = "Could not export this view: " + exception.Message;
        }
        finally
        {
            exporting = false;
        }
    }

    /// <summary>Writes the applied view to a chosen path; the text is built on the UI thread from what is shown.</summary>
    internal async Task<int> WriteExportAsync(string path, ExportFormat format)
    {
        string content = workspace.Export(format, DateTimeOffset.UtcNow);
        int rows = workspace.ExportRowCount;
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return rows;
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

    private async void BrowseChannels(object? sender, RoutedEventArgs eventArgs)
    {
        if (currentSessionPath is null || displayedSessionId is not { } sessionId
            || displayedGeneration < 1) return;
        WorkspaceNavigationMemento navigation = workspace.CaptureNavigation();
        var (available, processScope) = ChannelDiscoveryScope(navigation);
        if (!available) return;
        using var browser = new SessionChannelWindow(currentSessionPath, sessionId, displayedGeneration, processScope);
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
        if (openingSession || captureTask is { IsCompleted: false }) return;
        openingSession = true;
        StartExploringButton.IsEnabled = false;
        OpenSavedSessionButton.IsEnabled = false;
        try
        {
            IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders =
                await StorageProvider.OpenFolderPickerAsync(new()
                {
                    Title = "Open an InterCat session directory", AllowMultiple = false,
                });
            if (folders.Count == 0) return;
            string path = folders[0].Path.LocalPath;
            CaptureStatus.Text = "Opening saved session";
            OpenSavedSessionButton.IsEnabled = false;
            SessionOverviewBundle overview = await Task.Run(() =>
                SessionOverviewProjector.Project(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path))));
            if (closed) return;
            captureRunId++;
            heldUpdate = null;
            CaptureSummary.Text = string.Empty;
            ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open",
                "This is a published generation. The graph contains admitted paired TCP only; "
                + "other observed activity remains in the timeline.", SessionPath: path, Overview: overview),
                forceOverview: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            CaptureStatus.Text = "Could not open this session";
            CaptureDetail.Text = exception.Message + " The current workspace is unchanged.";
        }
        finally
        {
            openingSession = false;
            if (!closed)
            {
                StartExploringButton.IsEnabled = true;
                OpenSavedSessionButton.IsEnabled = true;
            }
        }
    }

    private async void BeginCapture()
    {
        if (openingSession || captureTask is { IsCompleted: false })
        {
            return;
        }

        captureStop?.Dispose();
        captureStop = new CancellationTokenSource();
        int run = ++captureRunId;
        displayedSessionId = null;
        displayedGeneration = -1;
        currentSessionPath = null;
        heldUpdate = null;
        followLatest = true;
        displayedOverview = null;
        lastPublicationUtc = null;
        UpdateHeldBanner();
        UpdateEvidenceAction();
        CaptureSummary.Text = string.Empty;
        CaptureSessionPath.Text = string.Empty;
        ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Preparing Explore",
            "Windows may ask for administrator approval to record system-wide events."));
        try
        {
            captureTask = Task.Run(() => DesktopCaptureRunner.RunAsync(
                update => ReceiveCaptureUpdate(run, update), captureStop.Token));
            await captureTask;
        }
        catch (Exception exception)
        {
            ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture interrupted",
                exception.Message + " Any published evidence is retained in the session shown below."));
        }
    }

    private void ReceiveCaptureUpdate(int run, CaptureUiUpdate update) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!closed && run == captureRunId)
            {
                ApplyCaptureUpdate(update);
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
        CaptureDetail.Text = update.Detail;
        CaptureLatency.Text = Freshness(update.Milestones);
        if (update.Summary is not null) CaptureSummary.Text = update.Summary;
        if (update.SessionPath is not null) CaptureSessionPath.Text = update.SessionPath;
        bool busy = update.Phase is CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StartExploringButton.IsEnabled = !busy;
        OpenSavedSessionButton.IsEnabled = !busy;
        StopCaptureButton.IsVisible = update.Phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StopCaptureButton.IsEnabled = update.Phase == CaptureUiPhase.Recording
            && captureStop?.IsCancellationRequested != true;
        FollowButton.IsVisible = IsLive;
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

        UpdateHealthStrip();
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

        UpdateHeldBanner();
        UpdateHealthStrip();
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
        currentSessionPath = update.SessionPath ?? currentSessionPath;
        SessionEvidenceSource? evidence = currentSessionPath is { } path
            ? new SessionEvidenceSource(path, overview.SessionId, overview.Generation)
            : null;
        var replacement = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity, evidence);
        if (savedNavigation is not null
            && replacement.RestoreNavigation(savedNavigation) is { } navigationNotice)
        {
            CaptureDetail.Text += " " + navigationNotice;
        }
        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
        workspace = replacement;
        workspace.PropertyChanged += OnWorkspaceChanged;
        DataContext = workspace;
        UpdateHeldBanner();
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
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
            _ => displayedOverview is null ? "Ready" : "Saved session",
        };
        HealthDot.Foreground = (this.TryFindResource(
            phase == CaptureUiPhase.Recording && !paused ? "Family.Alpc.Ink"
                : IsLive ? "Family.RemoteCall.Ink" : "Ink.Muted", out object? brush) ? brush : null) as Avalonia.Media.IBrush;
        HealthLossText.Text = LossStatement();
        HealthFreshnessText.Text = IsLive && lastPublicationUtc is { } last
            ? $"last publication {Math.Max(0, (DateTimeOffset.UtcNow - last).TotalSeconds):0} s ago"
            : string.Empty;
    }

    private string LossStatement()
    {
        if (displayedOverview is not { } overview)
        {
            return string.Empty;
        }

        if (!overview.CoverageLedgerPublished)
        {
            return IsLive ? "Loss is stated when recording stops" : "No coverage ledger · loss unknown";
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

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
        if (eventArgs.PropertyName == nameof(WorkspaceViewModel.HoldsGeneration))
        {
            UpdateHealthStrip();
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

    private void UpdateEvidenceAction()
    {
        WorkspaceNavigationMemento navigation = workspace.CaptureNavigation();
        var (channelsAvailable, channelProcess) = ChannelDiscoveryScope(navigation);
        bool session = currentSessionPath is not null && displayedGeneration > 0;
        ShowRecordsButton.IsEnabled = session && !workspace.IsEvidenceRung;
        BrowseChannelsButton.IsEnabled = session && channelsAvailable && !workspace.IsEvidenceRung;
        ToolTip.SetTip(ShowRecordsButton, ShowRecordsButton.IsEnabled
            ? "Open this rung's admitted source records in the ladder. Esc comes back here."
            : workspace.IsEvidenceRung ? "You are at the source records. Esc returns to where you were."
            : "Open or record a session first.");
        ToolTip.SetTip(BrowseChannelsButton, BrowseChannelsButton.IsEnabled
            ? (channelProcess is null ? "Browse every admitted paired TCP channel in this generation."
                : "Browse admitted paired TCP channels involving this process instance.")
                + " Choosing one opens its source records; a time brush applies to them."
            : "Open a session, or return to Machine or Process to browse paired channels.");
    }

    private static (bool Available, ProcessInstanceId? ProcessScope) ChannelDiscoveryScope(
        WorkspaceNavigationMemento navigation)
    {
        string? processKey = navigation.Breadcrumb.LastOrDefault(rung => rung.Level == DetailLevel.ProcessInstance)
            ?.Focus?.Key;
        if (processKey is not null && Guid.TryParse(processKey, out Guid id) && id != Guid.Empty)
            return (true, new ProcessInstanceId(id));
        return navigation.Breadcrumb[^1].Level == DetailLevel.Machine
            ? (true, navigation.SelectedProcess) : (false, null);
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
            var prompt = new Window
            {
                Title = "Stop Explore?", Width = 410, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };
            var stay = new Button { Content = "Keep recording" };
            var stop = new Button { Content = "Stop, save, and close" };
            stay.Click += (_, _) => prompt.Close(false);
            stop.Click += (_, _) => prompt.Close(true);
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
            if (await prompt.ShowDialog<bool>(this))
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

    public void Dispose()
    {
        healthClock.Stop();
        captureStop?.Cancel();
        captureStop?.Dispose();
        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
    }
}
