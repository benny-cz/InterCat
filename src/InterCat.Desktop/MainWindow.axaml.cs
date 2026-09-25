using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.CaptureBroker;
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

    /// <summary>The package being made, while one is; its cancellation is the share button's second meaning.</summary>
    private CancellationTokenSource? packaging;

    /// <summary>A session folder picker is open, so a second one is not started behind it.</summary>
    private bool choosingSession;
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
    private BrokerCaptureHealth? liveHealth;

    // The broker's latest live preview, and how many published chunks the displayed generation was derived from: the
    // preview draws the chunks after those (§12, §19.3).
    private BrokerCapturePreview? livePreview;
    private int displayedChunks;
    private string? appliedDetail;
    private readonly DispatcherTimer healthClock = new() { Interval = TimeSpan.FromSeconds(1) };

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
                viewModel.SearchText = string.Empty;
                RungList.Focus();
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

        if (e.Source is TextBox) return;

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

    private bool OpenSelectedSearchHit()
    {
        if (DataContext is not WorkspaceViewModel viewModel || !viewModel.OpenSearchResult()) return false;
        RungList.Focus();
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

    private void LoadMoreRecords(object? sender, RoutedEventArgs eventArgs) => _ = workspace.LoadMoreEvidenceAsync();

    /// <summary>The pointer equivalent of P: pins the selected graph node where it is drawn, or releases its pin.</summary>
    private void TogglePinNode(object? sender, RoutedEventArgs eventArgs) => _ = workspace.TogglePinSelectedGraphNode();

    /// <summary>The pointer equivalent of L: lays the graph out afresh, keeping pinned nodes where they are.</summary>
    private void RelayoutGraph(object? sender, RoutedEventArgs eventArgs) => _ = workspace.RelayoutGraph();

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
                string what = written.Context.Rung == DetailLevel.Evidence ? "records" : "rows";
                CaptureDetail.Text = written.Context.Complete
                    ? $"{(redacted ? "Saved redacted report with" : "Exported")} {written.Rows:N0} {what} (complete) to {path}."
                    : $"{(redacted ? "Saved redacted report with" : "Exported the first")} {written.Rows:N0} {what} of the scope to {path}; the file says what it leaves out.";
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

    private async Task<bool> ConfirmRedactedShareAsync()
    {
        var prompt = new Window
        {
            Title = "Share a redacted report?", Width = 540, Height = 360,
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
        return await prompt.ShowDialog<bool>(this);
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
        if (openingSession || choosingSession || packaging is not null || captureTask is { IsCompleted: false }) return;
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders;
        choosingSession = true;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "Open an InterCat session directory", AllowMultiple = false,
            });
        }
        finally
        {
            choosingSession = false;
        }

        if (folders.Count > 0 && !closed) _ = await OpenSessionAsync(folders[0].Path.LocalPath);
    }

    /// <summary>
    /// Opens a published session directory as the workspace. A redacted package says so where the status is read, so
    /// nobody mistakes its pseudonyms for the source machine's names and IDs. False when the directory could not open;
    /// the current workspace is then unchanged.
    /// </summary>
    internal async Task<bool> OpenSessionAsync(string path)
    {
        if (openingSession || captureTask is { IsCompleted: false }) return false;
        openingSession = true;
        StartExploringButton.IsEnabled = false;
        OpenSavedSessionButton.IsEnabled = false;
        try
        {
            CaptureStatus.Text = "Opening saved session";
            SessionOverviewBundle overview = await Task.Run(() =>
                SessionOverviewProjector.Project(SharedSessionStores.Open(path)));
            if (closed) return false;
            captureRunId++;
            heldUpdate = null;
            CaptureSummary.Text = string.Empty;
            ApplyCaptureUpdate(overview.Redaction is { } redaction
                ? new(CaptureUiPhase.Complete, "Redacted session package open",
                    SessionRedaction.Summary + " " + redaction.Warning, SessionPath: path, Overview: overview)
                : new(CaptureUiPhase.Complete, "Saved session open",
                    "This is a published generation. The graph contains admitted paired TCP only; "
                    + "other observed activity remains in the timeline.", SessionPath: path, Overview: overview),
                forceOverview: true);
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

    /// <summary>A new folder name under the chosen one: dated, and numbered rather than reusing an existing name.</summary>
    internal static string NewPackageDirectory(string parent, DateTimeOffset now)
    {
        string stem = Path.Combine(parent, $"intercat-redacted-session-{now:yyyyMMdd-HHmmss}");
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
                CaptureDetail.Text = $"Saved {result.Counts.Rows:N0} records to {result.Directory}. It was reopened and "
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

    private async Task<bool> ConfirmRedactedPackageAsync(SessionOverviewBundle overview)
    {
        var prompt = new Window
        {
            Title = "Share a redacted session package?", Width = 580, Height = 470,
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
                Paragraph($"Kept: all {overview.ObservationRows:N0} records with their times, sizes, status and quality; "
                    + $"the {overview.Nodes.Count:N0} processes and how they relate; coverage and loss."),
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
        return await prompt.ShowDialog<bool>(this);
    }

    /// <summary>Says where the package is and what verified it; true when the user wants to open it here to review.</summary>
    private async Task<bool> ShowPackageResultAsync(RedactedSessionPackageResult result)
    {
        var prompt = new Window
        {
            Title = "Redacted session package saved", Width = 580, Height = 330,
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
                Paragraph($"Saved {result.Counts.Rows:N0} records to {result.Directory}."),
                Paragraph($"Before it was saved, the package was reopened as a recipient would open it, every value "
                    + $"was checked against the pseudonyms it issued, and {result.FilesVerified:N0} files were searched "
                    + "for this session's identities and names. None was found."),
                Paragraph($"Left behind: {result.Source.SourceJournals:N0} original {journals} "
                    + $"({result.Source.SourceJournalBytes / 1024.0 / 1024.0:N1} MB). Opening the package here shows "
                    + "what a recipient will see."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { done, open } },
            },
        };
        return await prompt.ShowDialog<bool>(this);
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private async void BeginCapture()
    {
        if (openingSession || captureTask is { IsCompleted: false })
        {
            return;
        }

        captureStop?.Dispose();
        captureStop = new CancellationTokenSource();
        int run = ++captureRunId;
        ForgetDisplayedSession();
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
        CaptureStatus.Foreground = unavailable
            ? (this.TryFindResource("Family.RemoteCall.Ink", out object? warning) ? warning : null) as Avalonia.Media.IBrush
            : null;

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
        SessionEvidenceSource? evidence = currentSessionPath is { } path
            ? new SessionEvidenceSource(path, overview.SessionId, overview.Generation)
            : null;
        // A later publication of the same session keeps every node that is still drawn where the user last saw it.
        var replacement = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity, evidence,
            savedNavigation is null ? null : workspace.LaidOutPositions,
            savedNavigation is null ? null : workspace.GraphPins);
        if (savedNavigation is not null
            && replacement.RestoreNavigation(savedNavigation) is { } navigationNotice)
        {
            CaptureDetail.Text += " " + navigationNotice;
        }

        if (savedNavigation is not null)
        {
            // The timeline keeps what it drew until this generation's own counts replace it, as the graph keeps its layout.
            replacement.AdoptTimeline(workspace.CarryTimeline());
        }

        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
        workspace = replacement;
        workspace.PropertyChanged += OnWorkspaceChanged;
        DataContext = workspace;
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
        HealthDot.Foreground = (this.TryFindResource(
            phase == CaptureUiPhase.Recording && !paused ? "Family.Alpc.Ink"
                : IsLive ? "Family.RemoteCall.Ink" : "Ink.Muted", out object? brush) ? brush : null) as Avalonia.Media.IBrush;
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

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.ShowsMechanismLanes)
            or nameof(WorkspaceViewModel.ShowsProcessLanes)
            or nameof(WorkspaceViewModel.ShowsDirectionLanes)
            or nameof(WorkspaceViewModel.ShowsChannelEndLanes))
        {
            // A rung or asynchronous lane query can change the row count without replacing DataContext.
            TimelineSurface.RefreshLaneLayout();
        }
        if (eventArgs.PropertyName is nameof(WorkspaceViewModel.SelectedProcess)
            or nameof(WorkspaceViewModel.ShowsProcessLanes))
        {
            Dispatcher.UIThread.Post(TimelineSurface.BringSelectedProcessLaneIntoView);
        }
        UpdateEvidenceAction();
        GraphSurface.InvalidateVisual();
        TimelineSurface.InvalidateVisual();
        MinimapSurface.InvalidateVisual();

        // A card describes what is drawn now, so a count or brush arriving under a resting pointer redraws it.
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

        // Packaging reads a whole published generation, so it waits until a live capture has stopped.
        SharePackageButton.Content = packaging is null ? "Share redacted session…" : "Cancel packaging";
        SharePackageButton.IsEnabled = packaging is not null || (session && !IsLive && !openingSession);
        ToolTip.SetTip(SharePackageButton, packaging is not null
            ? "Stop making the package. Nothing is saved, and the session is unchanged."
            : SharePackageButton.IsEnabled
                ? "Save a new session folder with pseudonymous names, IDs and addresses and no original journal, "
                    + "that opens in InterCat. What it keeps and leaves out is shown first."
                : IsLive ? "Stop the capture first; a package holds a finished session."
                : "Open or record a session first.");
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
        packaging?.Cancel();
        captureStop?.Cancel();
        captureStop?.Dispose();
        workspace.PropertyChanged -= OnWorkspaceChanged;
        workspace.Dispose();
    }
}
