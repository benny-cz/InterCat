using System.Collections.Concurrent;
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

    public static async Task<int> FirstFeedbackAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        int runs = int.TryParse(Option(args, "--runs") ?? "3", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedRuns)
            && parsedRuns is >= 1 and <= 10 ? parsedRuns : -1;
        int seconds = int.TryParse(Option(args, "--seconds") ?? "15", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSeconds)
            && parsedSeconds is >= 5 and <= 120 ? parsedSeconds : -1;
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
            Schema = "intercat.first-feedback.v1",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            QpcFrequency = Stopwatch.Frequency,
            RecordingSeconds = seconds,
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
                    + "root in the output directory, and a 120-second quota.",
                "Visible means the overview was projected and handed to the window; drawing it is not included. A "
                    + "record's delay is the QPC reading at hand-off of the first overview whose derived records "
                    + "include it, minus the record's own QPC reading.",
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
                FirstFeedbackRun result = await FirstFeedbackRunAsync(output, run, seconds, cancellationToken);
                report.Runs.Add(result);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"run {run}: first overview {result.FirstOverviewAfterFirstDeliveredMs} ms after first record; "
                    + $"event to visible p95 {result.EventToVisible?.P95Ms} ms, p99 {result.EventToVisible?.P99Ms} ms"));
                await WaitForCliBrokerExitAsync(cancellationToken);
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
        string output, int run, int seconds, CancellationToken cancellationToken)
    {
        var result = new FirstFeedbackRun { Run = run };
        var updates = new ConcurrentQueue<CaptureUiUpdate>();
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var options = new CaptureRunOptions
        {
            Broker = new(Environment.ProcessPath!, ["serve"]),
            SessionRoot = Path.Combine(output, $"run-{run}"),
            MaximumDurationSeconds = 120,
        };
        Task capture = DesktopCaptureRunner.RunAsync(update =>
        {
            updates.Enqueue(update);
            if (update.Phase == CaptureUiPhase.Recording) recording.TrySetResult();
        }, stop.Token, options);

        await Task.WhenAny(recording.Task, capture);
        if (!capture.IsCompleted)
        {
            await PacedLoopbackAsync(TimeSpan.FromSeconds(seconds), cancellationToken);
            stop.Cancel();
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            await capture.WaitAsync(timeout.Token);
        }

        CaptureUiUpdate[] all = [.. updates];
        CaptureUiUpdate last = all[^1];
        result.FinalHeadline = last.Headline;
        CaptureMilestones? milestones = all.LastOrDefault(update => update.Milestones is not null)?.Milestones;
        result.BrokerReadyMs = Milliseconds(milestones?.BrokerReady);
        result.PreparedMs = Milliseconds(milestones?.Prepared);
        result.StartedMs = Milliseconds(milestones?.Started);
        result.FirstGenerationMs = Milliseconds(milestones?.FirstGeneration);
        result.FirstOverviewMs = Milliseconds(milestones?.FirstOverview);
        result.PublicationIntervalMs = Milliseconds(milestones?.PublicationInterval);
        CaptureVisibility[] visible = [.. all.Where(update => update.Visibility is not null)
            .Select(update => update.Visibility!).OrderBy(visibility => visibility.DerivedRecords)];
        result.OverviewsHandedOff = visible.Length;
        BrokerCaptureHealth[] live = [.. all
            .Where(update => update.Phase == CaptureUiPhase.Recording && update.LiveHealth is not null)
            .Select(update => update.LiveHealth!)
            .Distinct()];
        result.LiveCounterReadings = live.Length;
        result.LastLiveCounters = live.LastOrDefault();
        result.Derivation = Summarize(visible.Select(visibility => visibility.Derivation.TotalMilliseconds));
        result.Projection = Summarize(visible.Select(visibility => visibility.Projection.TotalMilliseconds));
        string? sessionPath = all.LastOrDefault(update => update.SessionPath is not null)?.SessionPath;
        if (sessionPath is null || visible.Length == 0 || last.Phase != CaptureUiPhase.Complete)
        {
            result.FailureReason = $"The capture ended without a followed overview: {last.Headline}. {last.Detail}";
            return result;
        }

        // Each record's delay: from its own QPC reading to the first overview that included it.
        var delays = new List<double>();
        long firstDelivered = long.MaxValue;
        long negative = 0;
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(sessionPath));
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
                    if (segment.UnsignedValue(SegmentColumnId.JournalRecordIndex, row) is not { } index)
                    {
                        result.RecordsWithoutJournalIndex++;
                        continue;
                    }

                    int covering = FirstCovering(visible, index);
                    if (covering < 0)
                    {
                        result.RecordsFirstVisibleAfterStop++;
                        continue;
                    }

                    long delay = visible[covering].ObservedQpc - native;
                    if (delay < 0) negative++;
                    delays.Add(delay * 1000d / Stopwatch.Frequency);
                }
            }
        }

        result.RecordsWithNegativeDelay = negative;
        result.EventToVisible = Summarize(delays);
        result.FirstOverviewAfterFirstDeliveredMs = (long)Math.Round(
            (visible[0].ObservedQpc - firstDelivered) * 1000d / Stopwatch.Frequency);
        result.Valid = negative == 0 && result.Records > 0 && delays.Count > 0 && live.Length > 0;
        result.MeetsBudgets = result.Valid
            && result.FirstOverviewAfterFirstDeliveredMs <= FirstUsefulOverviewBudgetMilliseconds
            && result.EventToVisible!.P95Ms <= EventToVisibleP95BudgetMilliseconds
            && result.EventToVisible.P99Ms <= EventToVisibleP99BudgetMilliseconds
            && result.Derivation!.P95Ms <= DerivationP95BudgetMilliseconds
            && result.Projection!.P95Ms <= ProjectionP95BudgetMilliseconds;
        if (negative > 0)
            result.FailureReason = "Some records were read after the overview that included them. The native readings "
                + "are not this machine's QPC clock, so no delay is reported as measured.";
        else if (live.Length == 0)
            result.FailureReason = "No live acquisition counters reached the viewer while it recorded.";
        return result;
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

    /// <summary>One paced stream and a new connection each second, the steady activity a latency distribution needs.</summary>
    private static async Task PacedLoopbackAsync(TimeSpan duration, CancellationToken cancellationToken)
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
        while (clock.Elapsed < duration)
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

    /// <summary>A broker launched through the tool's serve mode idle-exits on its own; the next run waits for that.</summary>
    private static async Task WaitForCliBrokerExitAsync(CancellationToken cancellationToken)
    {
        foreach (Process broker in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!))
            .Where(candidate => candidate.Id != Environment.ProcessId))
        {
            using (broker)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                await broker.WaitForExitAsync(idle.Token);
            }
        }
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
}

internal sealed class FirstFeedbackReport
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public required ProbeEnvironment Environment { get; init; }
    public required long QpcFrequency { get; init; }
    public required int RecordingSeconds { get; init; }
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
    public string? FailureReason { get; set; }
    public long? BrokerReadyMs { get; set; }
    public long? PreparedMs { get; set; }
    public long? StartedMs { get; set; }
    public long? FirstGenerationMs { get; set; }
    public long? FirstOverviewMs { get; set; }
    public long? PublicationIntervalMs { get; set; }
    public long? FirstOverviewAfterFirstDeliveredMs { get; set; }
    public int OverviewsHandedOff { get; set; }

    /// <summary>Distinct live acquisition-counter readings the viewer received while recording, and the last of them.</summary>
    public int LiveCounterReadings { get; set; }
    public BrokerCaptureHealth? LastLiveCounters { get; set; }
    public long Records { get; set; }
    public long RecordsWithoutJournalIndex { get; set; }
    public long RecordsFirstVisibleAfterStop { get; set; }
    public long RecordsWithNegativeDelay { get; set; }
    public LatencySummary? EventToVisible { get; set; }
    public LatencySummary? Derivation { get; set; }
    public LatencySummary? Projection { get; set; }
}

internal sealed class LatencySummary
{
    public int Count { get; init; }
    public long P50Ms { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public long MaxMs { get; init; }
}
