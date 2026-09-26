using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;

// First feedback of the Desktop's own live path (plan §3.1, §12): the Desktop capture runner launches this tool as its
// broker over a qualification root, follows the published evidence and projects each overview exactly as the window
// does. Records carry native QPC readings and every overview is stamped with the QPC reading at hand-off, so each
// record's delay from acquisition to the window is computed per record rather than estimated.
[SupportedOSPlatform("windows")]
internal static partial class Qualification
{
    private const long FirstUsefulOverviewBudgetMilliseconds = 3_000;
    private const long EventToVisibleP95BudgetMilliseconds = 1_500;
    private const long EventToVisibleP99BudgetMilliseconds = 3_000;
    private const long DerivationP95BudgetMilliseconds = 500;
    private const long ProjectionP95BudgetMilliseconds = 250;

    /// <summary>How often a run samples the viewer's and the broker's memory while the capture runs.</summary>
    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(10);

    /// <summary>The length of one trend window: a long run reports each minute on its own, so growth is not averaged away.</summary>
    private const int TrendWindowSeconds = 60;

    public static async Task<int> FirstFeedbackAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        int runs = int.TryParse(Option(args, "--runs") ?? "3", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedRuns)
            && parsedRuns is >= 1 and <= 10 ? parsedRuns : -1;
        int seconds = int.TryParse(Option(args, "--seconds") ?? "15", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSeconds)
            && parsedSeconds is >= 5 and <= 600 ? parsedSeconds : -1;
        bool bounded = args.Contains("--bounded", StringComparer.Ordinal);
        if (output is null || runs < 0 || seconds < 0)
        {
            return Usage();
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("First-feedback qualification starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; qualification writes only to a new directory.");
            return 2;
        }

        Directory.CreateDirectory(output);
        var report = new FirstFeedbackReport
        {
            Schema = "intercat.first-feedback.v4",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            QpcFrequency = Stopwatch.Frequency,
            RecordingSeconds = seconds,
            Bounded = bounded,
            Budgets = new()
            {
                ["firstUsefulOverviewAfterFirstDeliveredMs"] = FirstUsefulOverviewBudgetMilliseconds,
                ["eventToVisibleP95Ms"] = EventToVisibleP95BudgetMilliseconds,
                ["eventToVisibleP99Ms"] = EventToVisibleP99BudgetMilliseconds,
                ["derivationP95Ms"] = DerivationP95BudgetMilliseconds,
                ["projectionP95Ms"] = ProjectionP95BudgetMilliseconds,
            },
            Notes =
            [
                "The Desktop's DesktopCaptureRunner runs unchanged except for three options: this tool as its broker "
                    + "(the production composition over a qualification root, never the production root), a session "
                    + "root in the output directory, and the capture's duration quota.",
                bounded
                    ? "Bounded: the quota is the recording time, and nothing here stops the capture. The broker ends it at "
                        + "its quota, as it ends the default 600-second Explore capture, and the viewer follows it to the end."
                    : "Stopped: the viewer stops the capture after the recording time, as the Stop button or closing the "
                        + "window does. The quota is 120 s or the recording time plus 60 s, whichever is longer.",
                "Exact means the overview was projected and handed to the window; drawing it is not included. A "
                    + "record's exact delay is the QPC reading at hand-off of the first overview whose derived records "
                    + "include it, minus the record's own QPC reading.",
                "Previewed means a live preview (broker-v1 §5.9) holding the record was handed to the window: its "
                    + "capture-wide journal index lies in [journaled - counted, journaled) of that preview. Visible is "
                    + "whichever came first, preview or exact, and is what the event-to-visible budget is judged on "
                    + "(plan §12, §19.3's labelled preview). Schema v2 added the preview; v1's event-to-visible is v2's "
                    + "event-to-exact.",
                "Schema v4 adds the manifests the derived session and the broker's evidence hold at the end, and their "
                    + "bytes: a writer removes superseded manifests (store-v1 §9), so a long capture keeps a few, not one "
                    + "per publication.",
                "Schema v3 adds bounded runs, memory samples every 10 s and 60-second trend windows: overviews by their "
                    + "hand-off time, records by their own acquisition time, both from the first recording update.",
                "Memory: the viewer is this process, which runs the Desktop's capture runner (follower, derived session "
                    + "and overview projection) and keeps only compact measurements of each update, never an overview. "
                    + "It excludes the window, its workspace and drawing. The broker is this tool's serve mode, a separate "
                    + "process. The managed heap is its size after the most recent collection.",
                "The harness is elevated, so the broker launch shows no approval prompt; the approval step of the "
                    + "ordinary-integrity first run is not measured here.",
                "Traffic is one paced loopback TCP stream (1 KiB every 20 ms) plus a new connection each second, "
                    + "on top of whatever else the machine did during the run.",
            ],
        };

        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        try
        {
            for (int run = 1; run <= runs; run++)
            {
                FirstFeedbackRun result = await FirstFeedbackRunAsync(output, run, seconds, bounded, cancellationToken);
                report.Runs.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"run {run}: {result.Records:N0} records; first overview {result.FirstOverviewAfterFirstDeliveredMs} ms "
                    + $"after first record; event to visible p95 {result.EventToVisible?.P95Ms} ms, "
                    + $"p99 {result.EventToVisible?.P99Ms} ms (preview p95 {result.EventToPreview?.P95Ms} ms, "
                    + $"exact p95 {result.EventToExact?.P95Ms} ms); last overview derived in "
                    + $"{result.LastOverviewDerivationMs} ms, projected in {result.LastOverviewProjectionMs} ms; "
                    + $"viewer {result.MemoryAtEnd?.ViewerPrivateBytes / (1024 * 1024)} MiB private at the end"));
                if (!await WaitForCliBrokerExitAsync(cancellationToken))
                {
                    // The next run would meet this broker still serving; nothing after it would measure what it says.
                    report.Failure = "The broker did not exit within 90 s of the capture's end, so no further run was made.";
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Failure = exception.ToString();
        }
        finally
        {
            report.LeakedSessions = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Passed = report.Failure is null && report.LeakedSessions.Count == 0
                && report.Runs.Count == runs && report.Runs.All(run => run.Valid);
            report.MeetsBudgets = report.Passed && report.Runs.All(run => run.MeetsBudgets);
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json),
                CancellationToken.None);
            TryDelete(CliRootParent);
        }

        Console.WriteLine(!report.Passed ? "First-feedback qualification FAILED; see report.json."
            : report.MeetsBudgets ? "First-feedback qualification passed and met every budget."
            : "First-feedback qualification measured; at least one budget was missed (see report.json).");
        Console.WriteLine(Path.Combine(output, "report.json"));
        return report.Passed ? 0 : 1;
    }

    private static async Task<FirstFeedbackRun> FirstFeedbackRunAsync(
        string output, int run, int seconds, bool bounded, CancellationToken cancellationToken)
    {
        var result = new FirstFeedbackRun { Run = run };
        var collected = new FeedbackCollector();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var options = new CaptureRunOptions
        {
            Broker = new(Environment.ProcessPath!, ["serve"]),
            SessionRoot = Path.Combine(output, $"run-{run}"),
            MaximumDurationSeconds = bounded ? seconds : Math.Max(120, seconds + 60),
        };
        Task capture = DesktopCaptureRunner.RunAsync(collected.Receive, stop.Token, options);

        await Task.WhenAny(collected.Recording, capture);
        var samples = new List<MemorySample>();
        Task sampling = Task.CompletedTask;
        if (!capture.IsCompleted)
        {
            sampling = SampleMemoryAsync(collected, capture, samples, cancellationToken);

            // A bounded capture ends at its own quota, and the traffic with it; a stopped run stops it as the Stop button does.
            await PacedLoopbackAsync(TimeSpan.FromSeconds(bounded ? seconds + 60 : seconds), capture, cancellationToken);
            if (!capture.IsCompleted)
            {
                result.BoundOverrun = bounded;
                stop.Cancel();
            }
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await capture.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result.FailureReason = "The capture did not finish within 120 s of its end.";
                return result;
            }
        }

        await sampling;
        FeedbackSnapshot seen = collected.Snapshot();
        result.FinalHeadline = seen.Headline;
        result.FinalDetail = seen.Detail;
        CaptureMilestones? milestones = seen.Milestones;
        result.BrokerReadyMs = Milliseconds(milestones?.BrokerReady);
        result.PreparedMs = Milliseconds(milestones?.Prepared);
        result.StartedMs = Milliseconds(milestones?.Started);
        result.FirstGenerationMs = Milliseconds(milestones?.FirstGeneration);
        result.FirstOverviewMs = Milliseconds(milestones?.FirstOverview);
        result.PublicationIntervalMs = Milliseconds(milestones?.PublicationInterval);
        CaptureVisibility[] visible = [.. seen.Visible.OrderBy(visibility => visibility.DerivedRecords)];
        result.OverviewsHandedOff = visible.Length;
        result.ChunksFollowed = seen.Chunks;
        result.LiveCounterReadings = seen.LiveCounterReadings;
        result.LastLiveCounters = seen.LastLiveCounters;
        PreviewSighting[] previews = [.. seen.Previews.OrderBy(preview => preview.Qpc)];
        result.PreviewsHandedOff = previews.Length;
        result.LargestUnbinnedPreviewRecords = previews.Length == 0 ? 0 : previews.Max(preview => preview.UnbinnedRecords);
        result.Derivation = Summarize(visible.Select(visibility => visibility.Derivation.TotalMilliseconds));
        result.Projection = Summarize(visible.Select(visibility => visibility.Projection.TotalMilliseconds));
        if (visible.Length > 0)
        {
            result.LastOverviewDerivedRecords = visible[^1].DerivedRecords;
            result.LastOverviewDerivationMs = (long)Math.Round(visible[^1].Derivation.TotalMilliseconds);
            result.LastOverviewProjectionMs = (long)Math.Round(visible[^1].Projection.TotalMilliseconds);
        }

        result.Memory = samples;
        result.MemoryAtEnd = samples.LastOrDefault(sample => sample.Phase == nameof(CaptureUiPhase.Recording));
        if (seen.SessionPath is null || visible.Length == 0 || seen.Phase != CaptureUiPhase.Complete)
        {
            result.FailureReason = $"The capture ended without a followed overview: {seen.Headline}. {seen.Detail}";
            return result;
        }

        result.SessionBytes = Directory.EnumerateFiles(seen.SessionPath, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        (result.SessionManifests, result.SessionManifestBytes) = Manifests(seen.SessionPath);
        if (new DirectoryInfo(Path.Combine(CliRootParent, RootName)) is { Exists: true } brokerRoot
            && brokerRoot.EnumerateDirectories("capture-*").MaxBy(capture => capture.CreationTimeUtc) is { } evidence)
        {
            (result.EvidenceManifests, result.EvidenceManifestBytes) = Manifests(evidence.FullName);
        }

        // Each record's delays: from its own QPC reading to the first live preview, and to the first overview, that held it.
        var exactDelays = new List<double>();
        var previewDelays = new List<double>();
        var firstSight = new List<double>();
        var windows = new SortedDictionary<int, TrendAccumulator>();
        TrendAccumulator Window(long qpc)
        {
            int index = (int)Math.Max(0, (qpc - seen.RecordingQpc) / (TrendWindowSeconds * Stopwatch.Frequency));
            if (!windows.TryGetValue(index, out TrendAccumulator? window)) windows[index] = window = new();
            return window;
        }

        foreach (CaptureVisibility visibility in visible)
        {
            TrendAccumulator window = Window(visibility.ObservedQpc);
            window.Derivation.Add(visibility.Derivation.TotalMilliseconds);
            window.Projection.Add(visibility.Projection.TotalMilliseconds);
            window.DerivedRecordsAtEnd = Math.Max(window.DerivedRecordsAtEnd, visibility.DerivedRecords);
        }

        long firstDelivered = long.MaxValue;
        long negative = 0;
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(seen.SessionPath));
        using (EvidenceLease lease = store.AcquireLease())
        {
            foreach (string name in SessionSegments.Names(lease.Manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, lease.Manifest, name);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    result.Records++;
                    long native = segment.SignedValue(SegmentColumnId.NativeTicks, row)!.Value;
                    firstDelivered = Math.Min(firstDelivered, native);
                    TrendAccumulator window = Window(native);
                    window.Records++;
                    if (segment.UnsignedValue(SegmentColumnId.JournalRecordIndex, row) is not { } index)
                    {
                        result.RecordsWithoutJournalIndex++;
                        continue;
                    }

                    int covering = FirstCovering(visible, index);
                    double? exact = covering < 0 ? null : (visible[covering].ObservedQpc - native) * 1000d / Stopwatch.Frequency;
                    double? previewed = FirstPreviewCovering(previews, index) is { } at
                        ? (at - native) * 1000d / Stopwatch.Frequency : null;
                    if (exact is null) result.RecordsFirstVisibleAfterStop++;
                    if (previewed is null) result.RecordsNeverPreviewed++;
                    if (exact < 0 || previewed < 0) negative++;
                    if (exact is { } exactDelay)
                    {
                        exactDelays.Add(exactDelay);
                        window.Exact.Add(exactDelay);
                    }

                    if (previewed is { } previewDelay)
                    {
                        previewDelays.Add(previewDelay);
                        window.Preview.Add(previewDelay);
                    }

                    if (exact is not null || previewed is not null)
                    {
                        double sight = Math.Min(exact ?? double.MaxValue, previewed ?? double.MaxValue);
                        firstSight.Add(sight);
                        window.Visible.Add(sight);
                    }
                }
            }
        }

        result.RecordsWithNegativeDelay = negative;
        result.EventToExact = Summarize(exactDelays);
        result.EventToPreview = Summarize(previewDelays);
        result.EventToVisible = Summarize(firstSight);
        result.Trend = [.. windows.Select(entry => entry.Value.Finish(entry.Key * TrendWindowSeconds))];
        result.FirstOverviewAfterFirstDeliveredMs = (long)Math.Round(
            (visible[0].ObservedQpc - firstDelivered) * 1000d / Stopwatch.Frequency);

        // The runner's own verdict on a closed capture: fully finalized, and every published chunk followed.
        bool saved = seen.Headline == "Session saved";
        result.Valid = negative == 0 && result.Records > 0 && exactDelays.Count > 0 && seen.LiveCounterReadings > 0
            && previews.Length > 0 && (!bounded || (saved && !result.BoundOverrun));
        result.MeetsBudgets = result.Valid
            && result.FirstOverviewAfterFirstDeliveredMs <= FirstUsefulOverviewBudgetMilliseconds
            && result.EventToVisible!.P95Ms <= EventToVisibleP95BudgetMilliseconds
            && result.EventToVisible.P99Ms <= EventToVisibleP99BudgetMilliseconds
            && result.Derivation!.P95Ms <= DerivationP95BudgetMilliseconds
            && result.Projection!.P95Ms <= ProjectionP95BudgetMilliseconds;
        if (negative > 0)
            result.FailureReason = "Some records were read after the overview that included them. The native readings "
                + "are not this machine's QPC clock, so no delay is reported as measured.";
        else if (seen.LiveCounterReadings == 0)
            result.FailureReason = "No live acquisition counters reached the viewer while it recorded.";
        else if (previews.Length == 0)
            result.FailureReason = "No live preview reached the viewer while it recorded.";
        else if (result.BoundOverrun)
            result.FailureReason = "The broker did not end the bounded capture within 60 s of its quota; the viewer stopped it.";
        else if (bounded && !saved)
            result.FailureReason = $"The bounded capture did not end with its whole session saved: {seen.Headline}. {seen.Detail}";
        return result;
    }

    /// <summary>
    /// The QPC reading at hand-off of the first live preview that held this capture-wide journal index, or null. Previews
    /// are in hand-off order, so the journaled count only grows; the first to pass the index is the first that could hold
    /// it, and it holds it when the index is among the records its covered chunks count.
    /// </summary>
    private static long? FirstPreviewCovering(PreviewSighting[] previews, ulong index)
    {
        int low = 0;
        int high = previews.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if ((ulong)previews[middle].JournaledRecords > index) high = middle;
            else low = middle + 1;
        }

        return low < previews.Length
            && (ulong)(previews[low].JournaledRecords - previews[low].CountedRecords) <= index
            ? previews[low].Qpc
            : null;
    }

    /// <summary>The first overview whose derived records include this capture-wide journal index, or -1.</summary>
    private static int FirstCovering(CaptureVisibility[] visible, ulong index)
    {
        int low = 0;
        int high = visible.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if ((ulong)visible[middle].DerivedRecords > index) high = middle;
            else low = middle + 1;
        }

        return low < visible.Length ? low : -1;
    }

    /// <summary>Exact order statistics of a sample: the value at each rank, never an interpolated percentile.</summary>
    private static LatencySummary? Summarize(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0) return null;
        long At(double quantile) => (long)Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)]);
        return new()
        {
            Count = sorted.Length,
            P50Ms = At(0.50),
            P95Ms = At(0.95),
            P99Ms = At(0.99),
            MaxMs = (long)Math.Round(sorted[^1]),
        };
    }

    private static long? Milliseconds(TimeSpan? value) => value is { } span ? (long)Math.Round(span.TotalMilliseconds) : null;

    /// <summary>
    /// The viewer's and the broker's memory every <see cref="MemorySampleInterval"/> until the capture ends. The viewer is
    /// this process; the broker is this tool's serve mode, the most recently started other instance of it.
    /// </summary>
    private static async Task SampleMemoryAsync(
        FeedbackCollector collected, Task capture, List<MemorySample> samples, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using Process viewer = Process.GetCurrentProcess();
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        while (true)
        {
            await Task.WhenAny(capture, Task.Delay(MemorySampleInterval, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.IsCompleted) return;

            viewer.Refresh();
            long? brokerWorkingSet = null;
            long? brokerPrivate = null;
            DateTime newest = DateTime.MinValue;
            foreach (Process broker in Process.GetProcessesByName(name))
            {
                using (broker)
                {
                    try
                    {
                        if (broker.Id == Environment.ProcessId || broker.StartTime < newest) continue;
                        newest = broker.StartTime;
                        brokerWorkingSet = broker.WorkingSet64;
                        brokerPrivate = broker.PrivateMemorySize64;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // It exited between the listing and the reading.
                    }
                }
            }

            (CaptureUiPhase phase, long derived, long journaled) = collected.Progress();
            samples.Add(new()
            {
                ElapsedSeconds = (int)Math.Round(clock.Elapsed.TotalSeconds),
                Phase = phase.ToString(),
                DerivedRecords = derived,
                JournaledRecords = journaled,
                ViewerWorkingSetBytes = viewer.WorkingSet64,
                ViewerPrivateBytes = viewer.PrivateMemorySize64,
                ViewerManagedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                BrokerWorkingSetBytes = brokerWorkingSet,
                BrokerPrivateBytes = brokerPrivate,
            });
        }
    }

    /// <summary>
    /// One paced stream and a new connection each second, the steady activity a latency distribution needs, for the given
    /// duration or until the capture ends, whichever comes first.
    /// </summary>
    private static async Task PacedLoopbackAsync(TimeSpan duration, Task capture, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stream = new TcpClient();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        await stream.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        using TcpClient peer = await accepted;
        byte[] payload = new byte[1024];
        byte[] buffer = new byte[payload.Length];
        var clock = Stopwatch.StartNew();
        TimeSpan nextConnection = TimeSpan.Zero;
        while (clock.Elapsed < duration && !capture.IsCompleted)
        {
            await stream.GetStream().WriteAsync(payload, cancellationToken);
            int read = 0;
            while (read < buffer.Length)
            {
                read += await peer.GetStream().ReadAsync(buffer.AsMemory(read), cancellationToken);
            }

            if (clock.Elapsed >= nextConnection)
            {
                nextConnection += TimeSpan.FromSeconds(1);
                using var brief = new TcpClient();
                Task<TcpClient> briefAccepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
                await brief.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                using TcpClient briefPeer = await briefAccepted;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    /// <summary>
    /// A broker launched through the tool's serve mode idle-exits on its own; the next run waits for that. A viewer that
    /// lost its connection leaves the capture to its 30-second owner lease first, so the wait allows 90 s. False when a
    /// broker is still running after it.
    /// </summary>
    private static async Task<bool> WaitForCliBrokerExitAsync(CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(TimeSpan.FromSeconds(90));
        foreach (Process broker in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!))
            .Where(candidate => candidate.Id != Environment.ProcessId))
        {
            using (broker)
            {
                try
                {
                    await broker.WaitForExitAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (int Count, long Bytes) Manifests(string sessionDirectory)
    {
        FileInfo[] manifests = new DirectoryInfo(sessionDirectory).GetFiles("manifest-*.json");
        return (manifests.Length, manifests.Sum(manifest => manifest.Length));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A live preview as the window received it: the records it covered and the QPC reading at hand-off.</summary>
    private readonly record struct PreviewSighting(long JournaledRecords, long CountedRecords, long UnbinnedRecords, long Qpc);

    private sealed record FeedbackSnapshot(
        CaptureUiPhase Phase,
        string Headline,
        string Detail,
        string? SessionPath,
        CaptureMilestones? Milestones,
        long RecordingQpc,
        int Chunks,
        int LiveCounterReadings,
        BrokerCaptureHealth? LastLiveCounters,
        IReadOnlyList<CaptureVisibility> Visible,
        IReadOnlyList<PreviewSighting> Previews);

    /// <summary>
    /// What a run keeps of each update the runner hands to the window: its measurements, never an overview, which the
    /// window replaces with the next one. Keeping every overview would make the harness, not the viewer, what the memory
    /// samples measure over a long run.
    /// </summary>
    private sealed class FeedbackCollector
    {
        private readonly Lock gate = new();
        private readonly TaskCompletionSource recording = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CaptureVisibility> visible = [];
        private readonly List<PreviewSighting> previews = [];
        private readonly HashSet<BrokerCaptureHealth> live = [];
        private CaptureUiPhase phase = CaptureUiPhase.Starting;
        private string headline = string.Empty;
        private string detail = string.Empty;
        private string? sessionPath;
        private CaptureMilestones? milestones;
        private BrokerCaptureHealth? lastHealth;
        private long recordingQpc;
        private int chunks;

        public Task Recording => recording.Task;

        public void Receive(CaptureUiUpdate update)
        {
            lock (gate)
            {
                (phase, headline, detail) = (update.Phase, update.Headline, update.Detail);
                sessionPath = update.SessionPath ?? sessionPath;
                milestones = update.Milestones ?? milestones;
                if (update.Visibility is { } visibility) visible.Add(visibility);
                if (update.OverviewChunks is { } followed) chunks = Math.Max(chunks, followed);
                if (update.Phase == CaptureUiPhase.Recording)
                {
                    if (recordingQpc == 0) recordingQpc = Stopwatch.GetTimestamp();
                    if (update.LiveHealth is { } health && live.Add(health)) lastHealth = health;
                    if (update is { PreviewObservedQpc: { } qpc, LivePreview: { } preview })
                    {
                        previews.Add(new(preview.JournaledRecords, preview.CountedRecords, preview.UnbinnedRecords, qpc));
                    }
                }
            }

            if (update.Phase == CaptureUiPhase.Recording) recording.TrySetResult();
        }

        /// <summary>The phase, records derived into the last overview, and records the last preview saw journaled.</summary>
        public (CaptureUiPhase Phase, long Derived, long Journaled) Progress()
        {
            lock (gate)
            {
                return (phase, visible.Count == 0 ? 0 : visible[^1].DerivedRecords,
                    previews.Count == 0 ? 0 : previews[^1].JournaledRecords);
            }
        }

        public FeedbackSnapshot Snapshot()
        {
            lock (gate)
            {
                return new(phase, headline, detail, sessionPath, milestones, recordingQpc, chunks, live.Count, lastHealth,
                    [.. visible], [.. previews]);
            }
        }
    }

    private sealed class TrendAccumulator
    {
        public List<double> Derivation { get; } = [];
        public List<double> Projection { get; } = [];
        public List<double> Visible { get; } = [];
        public List<double> Preview { get; } = [];
        public List<double> Exact { get; } = [];
        public long DerivedRecordsAtEnd { get; set; }
        public long Records { get; set; }

        public FeedbackWindow Finish(int fromSecond) => new()
        {
            FromSecond = fromSecond,
            Overviews = Derivation.Count,
            DerivedRecordsAtEnd = DerivedRecordsAtEnd,
            Derivation = Summarize(Derivation),
            Projection = Summarize(Projection),
            Records = Records,
            EventToVisible = Summarize(Visible),
            EventToPreview = Summarize(Preview),
            EventToExact = Summarize(Exact),
        };
    }
}

internal sealed class FirstFeedbackReport
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public required ProbeEnvironment Environment { get; init; }
    public required long QpcFrequency { get; init; }
    public required int RecordingSeconds { get; init; }

    /// <summary>Whether the broker ended each capture at its quota (true) or the viewer stopped it (false).</summary>
    public required bool Bounded { get; init; }
    public required Dictionary<string, long> Budgets { get; init; }
    public bool Passed { get; set; }
    public bool MeetsBudgets { get; set; }
    public List<FirstFeedbackRun> Runs { get; } = [];
    public IReadOnlyList<string> LeakedSessions { get; set; } = [];
    public string? Failure { get; set; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed class FirstFeedbackRun
{
    public required int Run { get; init; }
    public bool Valid { get; set; }
    public bool MeetsBudgets { get; set; }
    public string? FinalHeadline { get; set; }
    public string? FinalDetail { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>A bounded capture still recording 60 s past its quota, which the viewer then stopped.</summary>
    public bool BoundOverrun { get; set; }
    public long? BrokerReadyMs { get; set; }
    public long? PreparedMs { get; set; }
    public long? StartedMs { get; set; }
    public long? FirstGenerationMs { get; set; }
    public long? FirstOverviewMs { get; set; }
    public long? PublicationIntervalMs { get; set; }
    public long? FirstOverviewAfterFirstDeliveredMs { get; set; }
    public int OverviewsHandedOff { get; set; }
    public int ChunksFollowed { get; set; }

    /// <summary>Distinct live acquisition-counter readings the viewer received while recording, and the last of them.</summary>
    public int LiveCounterReadings { get; set; }
    public BrokerCaptureHealth? LastLiveCounters { get; set; }
    public long Records { get; set; }
    public long SessionBytes { get; set; }

    /// <summary>Manifests the derived session and the broker's evidence hold at the end, and their bytes (store-v1 §9).</summary>
    public int SessionManifests { get; set; }
    public long SessionManifestBytes { get; set; }
    public int? EvidenceManifests { get; set; }
    public long? EvidenceManifestBytes { get; set; }
    public long RecordsWithoutJournalIndex { get; set; }
    public long RecordsFirstVisibleAfterStop { get; set; }
    public long RecordsWithNegativeDelay { get; set; }

    /// <summary>Distinct live previews handed to the window while recording, and the most records one left unbinned.</summary>
    public int PreviewsHandedOff { get; set; }
    public long LargestUnbinnedPreviewRecords { get; set; }
    public long RecordsNeverPreviewed { get; set; }

    /// <summary>To the first preview or overview holding the record, whichever came first: what the budget is judged on.</summary>
    public LatencySummary? EventToVisible { get; set; }
    public LatencySummary? EventToPreview { get; set; }

    /// <summary>To the first overview holding the record: schema v1's event-to-visible.</summary>
    public LatencySummary? EventToExact { get; set; }
    public LatencySummary? Derivation { get; set; }
    public LatencySummary? Projection { get; set; }

    /// <summary>The last overview handed to the window: what deriving and projecting the whole capture cost at its end.</summary>
    public long? LastOverviewDerivedRecords { get; set; }
    public long? LastOverviewDerivationMs { get; set; }
    public long? LastOverviewProjectionMs { get; set; }

    /// <summary>Memory while the capture ran, and the last sample taken while it was still recording.</summary>
    public IReadOnlyList<MemorySample> Memory { get; set; } = [];
    public MemorySample? MemoryAtEnd { get; set; }

    /// <summary>Consecutive windows of <c>60</c> s from the first recording update, so growth over a long run shows.</summary>
    public IReadOnlyList<FeedbackWindow> Trend { get; set; } = [];
}

internal sealed class LatencySummary
{
    public int Count { get; init; }
    public long P50Ms { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public long MaxMs { get; init; }
}

internal sealed class MemorySample
{
    public required int ElapsedSeconds { get; init; }
    public required string Phase { get; init; }
    public long DerivedRecords { get; init; }
    public long JournaledRecords { get; init; }
    public long ViewerWorkingSetBytes { get; init; }
    public long ViewerPrivateBytes { get; init; }
    public long ViewerManagedHeapBytes { get; init; }
    public long? BrokerWorkingSetBytes { get; init; }
    public long? BrokerPrivateBytes { get; init; }
}

internal sealed class FeedbackWindow
{
    public required int FromSecond { get; init; }
    public int Overviews { get; init; }
    public long DerivedRecordsAtEnd { get; init; }
    public LatencySummary? Derivation { get; init; }
    public LatencySummary? Projection { get; init; }

    /// <summary>Records acquired in this window, and their delays however late they were seen.</summary>
    public long Records { get; init; }
    public LatencySummary? EventToVisible { get; init; }
    public LatencySummary? EventToPreview { get; init; }
    public LatencySummary? EventToExact { get; init; }
}
