using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private bool closed;
    private bool closingPrompt;
    private bool closeAfterCapture;

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
        workspace = viewModel;
        DataContext = workspace;
        workspace.PropertyChanged += OnWorkspaceChanged;
        Opened += (_, _) => StartExploringButton.Focus();
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
            case Key.T:
                viewModel.ShowTables = !viewModel.ShowTables;
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = viewModel.Descend();
                break;
            case Key.E:
                e.Handled = viewModel.ShowEvidence();
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
        if (DataContext is WorkspaceViewModel viewModel)
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

    private async void InspectSourceRows(object? sender, RoutedEventArgs eventArgs)
    {
        if (currentSessionPath is null || displayedSessionId is not { } sessionId
            || displayedGeneration < 1) return;
        WorkspaceNavigationMemento navigation = workspace.CaptureNavigation();
        string? channelKey = navigation.Breadcrumb.LastOrDefault(rung => rung.Level == DetailLevel.Channel)
            ?.Focus?.Key;
        NavigationState current = navigation.Breadcrumb[^1];
        if (channelKey is null
            && (current.Level != DetailLevel.Machine || navigation.SelectedProcess is not null)) return;
        TimeRange? interval = navigation.SelectedInterval
            ?? (current.Viewport == workspace.Snapshot.Extent ? null : current.Viewport);
        using var inspector = new SessionEvidenceWindow(currentSessionPath, sessionId,
            displayedGeneration, channelKey, interval);
        try
        {
            await inspector.ShowDialog(this);
        }
        catch (InvalidOperationException exception)
        {
            if (!closed) CaptureDetail.Text = "Could not open the source-row inspector: " + exception.Message;
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

    private void ApplyCaptureUpdate(CaptureUiUpdate update, bool forceOverview = false)
    {
        CaptureStatus.Text = update.Headline;
        CaptureDetail.Text = update.Detail;
        if (update.Summary is not null) CaptureSummary.Text = update.Summary;
        if (update.SessionPath is not null) CaptureSessionPath.Text = update.SessionPath;
        bool busy = update.Phase is CaptureUiPhase.Starting or CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StartExploringButton.IsEnabled = !busy;
        OpenSavedSessionButton.IsEnabled = !busy;
        StopCaptureButton.IsVisible = update.Phase is CaptureUiPhase.Recording or CaptureUiPhase.Finishing;
        StopCaptureButton.IsEnabled = update.Phase == CaptureUiPhase.Recording
            && captureStop?.IsCancellationRequested != true;

        if (update.Overview is { } overview
            && (forceOverview || overview.SessionId != displayedSessionId
                || overview.Generation > displayedGeneration))
        {
            WorkspaceNavigationMemento? savedNavigation = !forceOverview && overview.SessionId == displayedSessionId
                ? workspace.CaptureNavigation() : null;
            displayedSessionId = overview.SessionId;
            displayedGeneration = overview.Generation;
            currentSessionPath = update.SessionPath;
            var replacement = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity);
            if (savedNavigation is not null
                && replacement.RestoreNavigation(savedNavigation) is { } navigationNotice)
            {
                CaptureDetail.Text += " " + navigationNotice;
            }
            workspace.PropertyChanged -= OnWorkspaceChanged;
            workspace.Dispose();
            workspace = replacement;
            DataContext = workspace;
            UpdateEvidenceAction();
            GraphSurface.InvalidateVisual();
            TimelineSurface.InvalidateVisual();
        }
    }

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
    }

    private void UpdateEvidenceAction()
    {
        WorkspaceNavigationMemento navigation = workspace.CaptureNavigation();
        NavigationState[] path = [.. navigation.Breadcrumb];
        bool atMachine = path[^1].Level == DetailLevel.Machine && navigation.SelectedProcess is null;
        bool inChannel = path.Any(rung => rung.Level == DetailLevel.Channel);
        InspectEvidenceButton.IsEnabled = currentSessionPath is not null && displayedGeneration > 0
            && (atMachine || inChannel);
        ToolTip.SetTip(InspectEvidenceButton, InspectEvidenceButton.IsEnabled
            ? "Read exact normalized rows from this published generation. No payload bytes or operation pairing."
            : "Open a session, clear a process selection for whole-machine rows, or descend to one channel.");
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
        captureStop?.Cancel();
        captureStop?.Dispose();
        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
    }
}
