using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Windows;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;

// A rolling capture holds its window for hours (§20.2, ADR-045, ADR-048 decision 5; IC-016a). The Desktop's capture runner
// keeps the newest stretch of session time, its follow releasing what is older between mirrors, and the broker releases
// its own copy of what the session gave up. This scenario records for hours keeping a window, as the Explore card's "The
// last 10 minutes" does, and samples what the session, the evidence and the viewer hold. It passes when the capture
// records to its quota and saves its session, the session holds its window at every sample once it has filled and the
// evidence no more than the session and a lag, and what they hold does not grow from the run's first quarter to its last.
[SupportedOSPlatform("windows")]
internal static partial class Qualification
{
    /// <summary>How far past its window's edges a sample may read and still hold it: a few chunks and a publication.</summary>
    private const int WindowToleranceSeconds = 30;

    /// <summary>
    /// How many chunks the counts a sample compares may disagree by: the published count is the latest live preview's, the
    /// derived count the latest overview's, and the files are listed at the sample, each read at its own moment.
    /// </summary>
    private const int CountSkewChunks = 3;

    /// <summary>How much more the run's last quarter may hold than its first, median to median, and still be bounded.</summary>
    private const double RollingGrowthBound = 1.5;

    /// <summary>The fewest samples a run judges, once its window has filled: two in each quarter.</summary>
    private const int FewestJudgedSamples = 8;

    /// <summary>The most messages a second a run's load stream is asked for: some 40,000 records a second.</summary>
    private const int MostLoadMessagesPerSecond = 20_000;

    /// <summary>How often the load stream writes its burst of messages: a timer's resolution, give or take.</summary>
    private static readonly TimeSpan LoadTick = TimeSpan.FromMilliseconds(20);

    public static async Task<int> RollingAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        double hours = double.TryParse(Option(args, "--hours") ?? "2", NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedHours)
            && parsedHours is > 0 and <= 24 ? parsedHours : -1;
        int keepMinutes = int.TryParse(Option(args, "--keep-minutes") ?? "10", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedKeep)
            && parsedKeep is >= 1 and <= 60 ? parsedKeep : -1;
        int load = int.TryParse(Option(args, "--load") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int parsedLoad)
            && parsedLoad is >= 0 and <= MostLoadMessagesPerSecond ? parsedLoad : -1;

        // A run records at least four windows, so that its window fills and is then held for most of it.
        if (output is null || hours < 0 || keepMinutes < 0 || load < 0 || keepMinutes * 4 > hours * 60)
        {
            return Usage();
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("The rolling qualification starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; qualification writes only to a new directory.");
            return 2;
        }

        Directory.CreateDirectory(output);
        int seconds = (int)Math.Round(hours * 3_600);
        var policy = new RollingRetentionPolicy(TimeSpan.FromMinutes(keepMinutes));
        TimeSpan every = TimeSpan.FromSeconds(Math.Min(60, keepMinutes * 15));
        string[] loadNote = load == 0 ? [] :
        [
            "A load of " + load.ToString("N0", CultureInfo.InvariantCulture) + " messages a second ran beside that traffic "
                + "for the whole recording: a second loopback TCP stream writing 1 KiB messages in bursts every 20 ms, each "
                + "read at its other end before the next, so a window holds as many rows as a busy machine's whatever else "
                + "the machine did; the messages it sent are counted.",
        ];
        var report = new RollingReport
        {
            Schema = "intercat.rolling-capture.v1",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            RecordingSeconds = seconds,
            KeepSeconds = (int)policy.Keep.TotalSeconds,
            SlackSeconds = (int)policy.Slack.TotalSeconds,
            SampleSeconds = (int)every.TotalSeconds,
            GcHeapHardLimit = System.Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
            GcRegionRange = System.Environment.GetEnvironmentVariable("DOTNET_GCRegionRange"),
            LoadMessagesPerSecond = load,
            Notes =
            [
                "The Desktop's DesktopCaptureRunner runs unchanged but for its options: this tool as its broker over a "
                    + "qualification root, never the production root; a session root in the output directory; the run's "
                    + "length as the capture's quota, which the broker ends it at; and the window it keeps, as the Explore "
                    + "card's rolling choices ask for it.",
                "Traffic is one paced loopback TCP stream (1 KiB every 20 ms) plus a new connection each second, on top of "
                    + "whatever else the machine did during the run.",
                "Each sample reads the latest overview the runner handed over: its rows, the session time its extent spans "
                    + "(what it holds), where the session keeps every record from and the rows it kept from before as "
                    + "identity evidence. It sums the bytes of the session's files and of the capture's evidence, counts "
                    + "the journal chunks each holds, and reads the viewer's private bytes and managed heap, the latter as "
                    + "of its latest collection, and the broker's private bytes.",
                "Judged are the samples taken while recording from the window and its slack plus a minute on, by when the "
                    + "session's first release is due. The window is held when the session's extent spans no less than "
                    + "the window less 30 s and no more than the window, its slack and 30 s, at every judged sample, at "
                    + "the end and in the saved session reopened. What each has given up is the capture's chunks it was "
                    + "given - published by the broker, derived by the follow - less the journal chunks its files hold. The "
                    + "evidence keeps no more than the session when, at every judged sample, it has given up all the "
                    + "session had at the sample before, less 3 chunks for the counts' moments: the broker releases what "
                    + "the session gave up once the runner's next lease renewal says so. Bounded means the median of the judged "
                    + "samples' last quarter is within 1.5 times their first quarter's, for the session's bytes per row "
                    + "and the evidence's, so the rate of the machine's traffic cancels out, and for the viewer's heap.",
                "A run under DOTNET_GCHeapHardLimit with a wide DOTNET_GCRegionRange, as the report names, also shows the "
                    + "viewer kept within that heap for its whole length: otherwise it runs out of memory and fails.",
                .. loadNote,
            ],
        };

        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        var collected = new RollingCollector();
        Task<long> loading = Task.FromResult(0L);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task capture = DesktopCaptureRunner.RunAsync(collected.Receive, stop.Token, new CaptureRunOptions
            {
                Broker = new(Environment.ProcessPath!, ["serve"]),
                SessionRoot = Path.Combine(output, "session"),
                MaximumDurationSeconds = seconds,
                Rolling = policy,
            });
            await Task.WhenAny(collected.Recording, capture);
            Task traffic = Task.CompletedTask;
            Task sampling = Task.CompletedTask;
            if (!capture.IsCompleted)
            {
                traffic = PacedLoopbackAsync(TimeSpan.FromSeconds(seconds + 120), capture, cancellationToken);
                loading = load > 0 ? LoadLoopbackAsync(load, TimeSpan.FromSeconds(seconds + 120), capture, cancellationToken) : loading;
                sampling = SampleRollingAsync(collected, capture, every, report.Samples, cancellationToken);
            }

            // The broker ends the capture at its quota; nothing here stops it unless it overruns by a quarter of an hour.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 900));
                try
                {
                    await capture.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    report.Failure = "The capture did not end within a quarter of an hour of its quota; the viewer stopped it.";
                    await stop.CancelAsync();
                    await capture.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
                }
            }

            await traffic;
            await sampling;
            RollingCollector.Seen seen = collected.Snapshot();
            report.FinalHeadline = seen.Headline;
            report.FinalDetail = seen.Detail;
            report.Overviews = seen.Overviews;
            report.ReleasesSeen = seen.Releases;
            report.JournaledRecords = seen.Journaled;
            report.PublicationIntervalSeconds = seen.PublicationInterval?.TotalSeconds;
            report.FinalRows = seen.Rows;
            report.FinalHeldSeconds = seen.HeldSeconds;
            report.FinalRetainedFromSeconds = seen.RetainedFromSeconds;
            report.FinalKeptRows = seen.KeptRows;
            report.FinalRecordedSeconds = seen.RecordedSeconds;
            report.SessionSaved = seen.Phase == CaptureUiPhase.Complete && seen.Headline == "Session saved";
            if (seen.SessionPath is { } path && Directory.Exists(path))
            {
                (report.SessionBytes, report.SessionChunks) = Holding(path);

                // What a person opening the saved session sees: the same rows, and the window it kept.
                SessionOverviewBundle reopened = SessionOverviewProjector.Project(
                    SessionStore.OpenForViewing(LocalOwnedDirectory.Open(path)), cancellationToken: cancellationToken);
                report.ReopenedRows = reopened.ObservationRows;
                report.ReopenedHeldSeconds = HeldSeconds(reopened);
            }

            Judge(report);
            if (!await WaitForCliBrokerExitAsync(cancellationToken))
            {
                report.Failure ??= "The broker did not exit within 90 s of the capture's end.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Failure = exception.ToString();
        }
        finally
        {
            // The load stream stops at its next burst once the capture ends, however it ended, so even a run that failed
            // says how many messages it carried.
            _ = await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None));
            report.LoadMessagesSent = loading.IsCompletedSuccessfully ? loading.Result : report.LoadMessagesSent;
            report.LeakedSessions = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Passed = report.Failure is null && report.LeakedSessions.Count == 0 && report.SessionSaved
                && report.RanToQuota && report.WindowHeld && report.EvidenceHeld && report.Bounded
                && report.ReopenedRows == report.FinalRows;
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json),
                CancellationToken.None);
            TryDelete(CliRootParent);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"recorded {report.FinalRecordedSeconds:0} s keeping {report.KeepSeconds} s: {report.JournaledRecords:N0} records "
            + $"journaled; the session ended holding {report.FinalHeldSeconds:0} s of session time in {report.FinalRows:N0} "
            + $"rows after {report.ReleasesSeen} releases; {report.SamplesOutsideWindow} of {report.SamplesJudged} judged "
            + $"samples outside the window, {report.SamplesEvidenceHeldBack} with the evidence keeping what the session had "
            + $"given up (at most {report.MostEvidenceBehindChunks} chunks behind it); first "
            + $"quarter to last: session bytes per row x{report.SessionBytesPerRowGrowth:0.00}, evidence bytes per row "
            + $"x{report.EvidenceBytesPerRowGrowth:0.00}, viewer heap x{report.ViewerHeapGrowth:0.00}"));
        Console.WriteLine(report.Passed ? "Rolling qualification passed." : "Rolling qualification FAILED; see report.json.");
        Console.WriteLine(Path.Combine(output, "report.json"));
        return report.Passed ? 0 : 1;
    }

    /// <summary>Whether the capture ran its length, the window was held, the evidence no more than it, and nothing grew.</summary>
    private static void Judge(RollingReport report)
    {
        bool Within(double? held) => held is { } span
            && span >= report.KeepSeconds - WindowToleranceSeconds
            && span <= report.KeepSeconds + report.SlackSeconds + WindowToleranceSeconds;

        // The session's first release is due once it holds its window and slack; its overview follows within a minute.
        RollingSample[] judged = [.. report.Samples.Where(sample => sample.Phase == nameof(CaptureUiPhase.Recording)
            && sample.ElapsedSeconds >= report.KeepSeconds + report.SlackSeconds + 60)];
        report.SamplesJudged = judged.Length;
        report.SamplesOutsideWindow = judged.Count(sample => !Within(sample.HeldSeconds));

        // By each sample the broker has released whatever the session had given up by the one before.
        report.SamplesEvidenceHeldBack = judged.Skip(1).Zip(judged, (sample, before) =>
            sample.EvidenceGaveUpChunks < before.SessionGaveUpChunks - CountSkewChunks).Count(heldBack => heldBack);
        report.MostEvidenceBehindChunks = judged.Length == 0 ? 0 : judged.Max(sample => sample.EvidenceBehindChunks);
        report.RanToQuota = report.FinalRecordedSeconds >= report.RecordingSeconds - WindowToleranceSeconds;
        bool enough = judged.Length >= FewestJudgedSamples;
        report.WindowHeld = enough && report.ReleasesSeen > 0 && report.SamplesOutsideWindow == 0
            && Within(report.FinalHeldSeconds) && Within(report.ReopenedHeldSeconds);
        report.EvidenceHeld = enough && report.SamplesEvidenceHeldBack == 0;
        if (!enough)
        {
            return;
        }

        int quarter = judged.Length / 4;
        report.FirstQuarter = RollingQuarter.Of(judged[..quarter]);
        report.LastQuarter = RollingQuarter.Of(judged[^quarter..]);
        report.SessionBytesPerRowGrowth = Growth(report.FirstQuarter.SessionBytesPerRow, report.LastQuarter.SessionBytesPerRow);
        report.EvidenceBytesPerRowGrowth = Growth(report.FirstQuarter.EvidenceBytesPerRow, report.LastQuarter.EvidenceBytesPerRow);
        report.ViewerHeapGrowth = Growth(report.FirstQuarter.ViewerManagedHeapBytes, report.LastQuarter.ViewerManagedHeapBytes);
        report.Bounded = report.SessionBytesPerRowGrowth <= RollingGrowthBound
            && report.EvidenceBytesPerRowGrowth <= RollingGrowthBound
            && report.ViewerHeapGrowth <= RollingGrowthBound;
    }

    private static double? Growth(double before, double after) => before > 0 ? Math.Round(after / before, 3) : null;

    /// <summary>
    /// A second loopback stream carrying <paramref name="messagesPerSecond"/> 1 KiB messages, each read at its other end
    /// before the next is written, in a burst every <see cref="LoadTick"/>, for the given duration or until the capture
    /// ends; returns the messages sent. A burst falls behind on a machine too slow for the rate, and the next tick writes
    /// what the clock says is due, so the rate holds over the run rather than tick by tick.
    /// </summary>
    private static async Task<long> LoadLoopbackAsync(int messagesPerSecond, TimeSpan duration, Task capture, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stream = new TcpClient { NoDelay = true };
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        await stream.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        using TcpClient peer = await accepted;
        byte[] payload = new byte[1024];
        byte[] buffer = new byte[payload.Length];
        var clock = Stopwatch.StartNew();
        long sent = 0;
        while (clock.Elapsed < duration && !capture.IsCompleted)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * messagesPerSecond);
            for (; sent < due; sent++)
            {
                await stream.GetStream().WriteAsync(payload, cancellationToken);
                int read = 0;
                while (read < buffer.Length)
                {
                    read += await peer.GetStream().ReadAsync(buffer.AsMemory(read), cancellationToken);
                }
            }

            await Task.Delay(LoadTick, cancellationToken);
        }

        return sent;
    }

    /// <summary>The session time an overview's extent spans, in seconds: what the session holds; null when it holds no timed record.</summary>
    private static double? HeldSeconds(SessionOverviewBundle overview) =>
        overview.Extent is { } extent ? extent.SpanTicks / (double)TimeSpan.TicksPerSecond : null;

    private static async Task SampleRollingAsync(
        RollingCollector collected, Task capture, TimeSpan every, List<RollingSample> samples, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using Process viewer = Process.GetCurrentProcess();
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        while (true)
        {
            await Task.WhenAny(capture, Task.Delay(every, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.IsCompleted)
            {
                return;
            }

            viewer.Refresh();
            long? brokerPrivate = null;
            DateTime newest = DateTime.MinValue;
            foreach (Process broker in Process.GetProcessesByName(name))
            {
                using (broker)
                {
                    try
                    {
                        if (broker.Id == Environment.ProcessId || broker.StartTime < newest)
                        {
                            continue;
                        }

                        newest = broker.StartTime;
                        brokerPrivate = broker.PrivateMemorySize64;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // It exited between the listing and the reading.
                    }
                }
            }

            RollingCollector.Seen seen = collected.Snapshot();
            (long sessionBytes, int sessionChunks) = seen.SessionPath is { } path ? Holding(path) : (0, 0);
            (long evidenceBytes, int evidenceChunks) = NewestEvidence() is { } evidence ? Holding(evidence) : (0, 0);
            samples.Add(new()
            {
                ElapsedSeconds = (int)Math.Round(clock.Elapsed.TotalSeconds),
                Phase = seen.Phase.ToString(),
                JournaledRecords = seen.Journaled,
                Rows = seen.Rows,
                HeldSeconds = seen.HeldSeconds,
                RetainedFromSeconds = seen.RetainedFromSeconds,
                KeptRows = seen.KeptRows,
                PublishedChunks = seen.PublishedChunks,
                DerivedChunks = seen.DerivedChunks,
                SessionBytes = sessionBytes,
                SessionChunks = sessionChunks,
                EvidenceBytes = evidenceBytes,
                EvidenceChunks = evidenceChunks,
                ViewerPrivateBytes = viewer.PrivateMemorySize64,
                ViewerManagedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                BrokerPrivateBytes = brokerPrivate,
            });
        }
    }

    /// <summary>The newest capture's evidence beneath the qualification root the tool's broker keeps; null before there is one.</summary>
    private static string? NewestEvidence()
    {
        var root = new DirectoryInfo(Path.Combine(CliRootParent, RootName));
        return root.Exists ? root.EnumerateDirectories("capture-*").MaxBy(directory => directory.CreationTimeUtc)?.FullName : null;
    }

    /// <summary>
    /// The bytes of a store's files and the journal chunks among them, listed without opening any, so no reading holds up a
    /// publication or a release; a file published or released meanwhile counts or not.
    /// </summary>
    private static (long Bytes, int Chunks) Holding(string directory)
    {
        long bytes = 0;
        int chunks = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += file.Length;
                    chunks += file.Extension.Equals(".icatj", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                }
                catch (FileNotFoundException)
                {
                    // Released between the listing and the reading.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A directory replaced while it was listed: the next sample lists it again.
        }

        return (bytes, chunks);
    }

    /// <summary>
    /// What a rolling run keeps of each update the runner hands over: its phase and what the latest overview holds, never an
    /// overview, so the harness does not hold what the viewer gave up.
    /// </summary>
    private sealed class RollingCollector
    {
        private readonly Lock gate = new();
        private readonly TaskCompletionSource recording = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<long> boundaries = [];
        private CaptureUiPhase phase = CaptureUiPhase.Starting;
        private string headline = string.Empty;
        private string detail = string.Empty;
        private string? sessionPath;
        private TimeSpan? publicationInterval;
        private long journaled;
        private int publishedChunks;
        private int derivedChunks;
        private long rows;
        private double? heldSeconds;
        private double? retainedFromSeconds;
        private long? keptRows;
        private double? recordedSeconds;
        private int overviews;

        public Task Recording => recording.Task;

        public void Receive(CaptureUiUpdate update)
        {
            lock (gate)
            {
                (phase, headline, detail) = (update.Phase, update.Headline, update.Detail);
                sessionPath = update.SessionPath ?? sessionPath;
                publicationInterval = update.Milestones?.PublicationInterval ?? publicationInterval;
                journaled = Math.Max(journaled, update.LiveHealth?.AdmittedRecords ?? 0);

                // The chunk a live preview counts as open is being written: every one before it is published.
                publishedChunks = Math.Max(publishedChunks, (update.LivePreview?.OpenChunk ?? 1) - 1);
                derivedChunks = Math.Max(derivedChunks, update.OverviewChunks ?? 0);
                if (update.Overview is { } overview)
                {
                    overviews++;
                    rows = overview.ObservationRows;
                    heldSeconds = HeldSeconds(overview);
                    retainedFromSeconds = overview.RetainedFromNanoseconds / 1e9;
                    keptRows = overview.RetainedSince?.KeptRows;
                    recordedSeconds = overview.Recording is { } recorded ? recorded.EndTicks / (double)TimeSpan.TicksPerSecond : null;
                    if (overview.RetainedFromNanoseconds is { } boundary)
                    {
                        boundaries.Add(boundary);
                    }
                }
            }

            if (update.Phase == CaptureUiPhase.Recording)
            {
                recording.TrySetResult();
            }
        }

        public Seen Snapshot()
        {
            lock (gate)
            {
                return new(phase, headline, detail, sessionPath, publicationInterval, journaled, publishedChunks,
                    derivedChunks, rows, heldSeconds, retainedFromSeconds, keptRows, recordedSeconds, overviews,
                    boundaries.Count);
            }
        }

        /// <param name="PublishedChunks">The chunks the broker had published, as the latest live preview counts them.</param>
        /// <param name="DerivedChunks">The capture's chunks the latest overview was derived from.</param>
        /// <param name="Releases">The interval releases the run's overviews have shown: each states a new boundary.</param>
        public sealed record Seen(
            CaptureUiPhase Phase,
            string Headline,
            string Detail,
            string? SessionPath,
            TimeSpan? PublicationInterval,
            long Journaled,
            int PublishedChunks,
            int DerivedChunks,
            long Rows,
            double? HeldSeconds,
            double? RetainedFromSeconds,
            long? KeptRows,
            double? RecordedSeconds,
            int Overviews,
            int Releases);
    }
}

internal sealed class RollingReport
{
    public required string Schema { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset FinishedUtc { get; set; }

    public required ProbeEnvironment Environment { get; init; }

    /// <summary>The capture's quota: how long it records before the broker ends it.</summary>
    public required int RecordingSeconds { get; init; }

    /// <summary>The session time the capture keeps.</summary>
    public required int KeepSeconds { get; init; }

    /// <summary>How far past its window the session grows before its oldest records go (RollingRetentionPolicy.Slack).</summary>
    public required int SlackSeconds { get; init; }

    public required int SampleSeconds { get; init; }

    /// <summary>The GC heap hard limit the run was made under, as its environment set it; null when none.</summary>
    public string? GcHeapHardLimit { get; init; }

    /// <summary>The GC region range the run was made under, as its environment set it; null when the runtime chose.</summary>
    public string? GcRegionRange { get; init; }

    /// <summary>The messages a second the run's load stream was asked to carry beside its paced traffic; 0 for none.</summary>
    public int LoadMessagesPerSecond { get; init; }

    /// <summary>The messages the load stream sent, each read at its other end.</summary>
    public long LoadMessagesSent { get; set; }

    public required IReadOnlyList<string> Notes { get; init; }

    public bool Passed { get; set; }

    public string? Failure { get; set; }

    public IReadOnlyList<string> LeakedSessions { get; set; } = [];

    public bool SessionSaved { get; set; }

    /// <summary>Whether the saved session recorded to its quota, less 30 s: the broker, not a limit, ended it.</summary>
    public bool RanToQuota { get; set; }

    public bool WindowHeld { get; set; }

    public bool EvidenceHeld { get; set; }

    public bool Bounded { get; set; }

    public string? FinalHeadline { get; set; }

    public string? FinalDetail { get; set; }

    public double? PublicationIntervalSeconds { get; set; }

    /// <summary>The records the broker admitted over the whole run, as its live counters last said.</summary>
    public long JournaledRecords { get; set; }

    public int Overviews { get; set; }

    /// <summary>The interval releases the run's overviews showed.</summary>
    public int ReleasesSeen { get; set; }

    /// <summary>The session time the saved session's recording reaches, in seconds.</summary>
    public double? FinalRecordedSeconds { get; set; }

    public long FinalRows { get; set; }

    /// <summary>The session time the last overview's extent spans, in seconds.</summary>
    public double? FinalHeldSeconds { get; set; }

    /// <summary>Where, in seconds of session time, the session keeps every record from; null when it released nothing.</summary>
    public double? FinalRetainedFromSeconds { get; set; }

    /// <summary>The rows of released records the session kept as the identity evidence of what came after.</summary>
    public long? FinalKeptRows { get; set; }

    public long SessionBytes { get; set; }

    public int SessionChunks { get; set; }

    /// <summary>The rows the saved session, reopened for viewing, projects, and the session time it spans.</summary>
    public long ReopenedRows { get; set; }

    public double? ReopenedHeldSeconds { get; set; }

    public int SamplesJudged { get; set; }

    public int SamplesOutsideWindow { get; set; }

    /// <summary>Judged samples at which the evidence still kept chunks the session had given up by the sample before.</summary>
    public int SamplesEvidenceHeldBack { get; set; }

    /// <summary>The most chunks the session had given up and the evidence still kept, at any judged sample.</summary>
    public int MostEvidenceBehindChunks { get; set; }

    public RollingQuarter? FirstQuarter { get; set; }

    public RollingQuarter? LastQuarter { get; set; }

    public double? SessionBytesPerRowGrowth { get; set; }

    public double? EvidenceBytesPerRowGrowth { get; set; }

    public double? ViewerHeapGrowth { get; set; }

    public List<RollingSample> Samples { get; } = [];
}

internal sealed class RollingSample
{
    public int ElapsedSeconds { get; init; }

    public required string Phase { get; init; }

    public long JournaledRecords { get; init; }

    /// <summary>The rows of the latest overview handed over.</summary>
    public long Rows { get; init; }

    /// <summary>The session time the latest overview's extent spans, in seconds.</summary>
    public double? HeldSeconds { get; init; }

    public double? RetainedFromSeconds { get; init; }

    public long? KeptRows { get; init; }

    /// <summary>The capture's chunks the broker had published, as the latest live preview counts them.</summary>
    public int PublishedChunks { get; init; }

    /// <summary>The capture's chunks the latest overview was derived from.</summary>
    public int DerivedChunks { get; init; }

    public long SessionBytes { get; init; }

    /// <summary>The journal chunks the session's files hold.</summary>
    public int SessionChunks { get; init; }

    public long EvidenceBytes { get; init; }

    /// <summary>The journal chunks the evidence's files hold.</summary>
    public int EvidenceChunks { get; init; }

    /// <summary>The capture's chunks the session has given up: those derived, less those its files hold.</summary>
    public int SessionGaveUpChunks => DerivedChunks - SessionChunks;

    /// <summary>The capture's chunks the evidence has given up: those published, less those its files hold.</summary>
    public int EvidenceGaveUpChunks => PublishedChunks - EvidenceChunks;

    /// <summary>The chunks the session has given up and the evidence still keeps.</summary>
    public int EvidenceBehindChunks => SessionGaveUpChunks - EvidenceGaveUpChunks;

    public long ViewerPrivateBytes { get; init; }

    public long ViewerManagedHeapBytes { get; init; }

    public long? BrokerPrivateBytes { get; init; }
}

/// <summary>The medians of a quarter of a run's judged samples.</summary>
internal sealed class RollingQuarter
{
    public required int FromSecond { get; init; }

    public required int ToSecond { get; init; }

    public double Rows { get; init; }

    public double HeldSeconds { get; init; }

    public double SessionBytes { get; init; }

    public double SessionChunks { get; init; }

    public double SessionBytesPerRow { get; init; }

    public double EvidenceBytes { get; init; }

    public double EvidenceChunks { get; init; }

    public double EvidenceBytesPerRow { get; init; }

    public double ViewerPrivateBytes { get; init; }

    public double ViewerManagedHeapBytes { get; init; }

    public double BrokerPrivateBytes { get; init; }

    public static RollingQuarter Of(RollingSample[] samples) => new()
    {
        FromSecond = samples[0].ElapsedSeconds,
        ToSecond = samples[^1].ElapsedSeconds,
        Rows = Median(samples, sample => sample.Rows),
        HeldSeconds = Median(samples, sample => sample.HeldSeconds ?? 0),
        SessionBytes = Median(samples, sample => sample.SessionBytes),
        SessionChunks = Median(samples, sample => sample.SessionChunks),
        SessionBytesPerRow = Median(samples, sample => sample.Rows > 0 ? sample.SessionBytes / (double)sample.Rows : 0),
        EvidenceBytes = Median(samples, sample => sample.EvidenceBytes),
        EvidenceChunks = Median(samples, sample => sample.EvidenceChunks),
        EvidenceBytesPerRow = Median(samples, sample => sample.Rows > 0 ? sample.EvidenceBytes / (double)sample.Rows : 0),
        ViewerPrivateBytes = Median(samples, sample => sample.ViewerPrivateBytes),
        ViewerManagedHeapBytes = Median(samples, sample => sample.ViewerManagedHeapBytes),
        BrokerPrivateBytes = Median(samples, sample => sample.BrokerPrivateBytes ?? 0),
    };

    private static double Median(RollingSample[] samples, Func<RollingSample, double> measure)
    {
        double[] sorted = [.. samples.Select(measure).Order()];
        int middle = sorted.Length / 2;
        return Math.Round(sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2, 3);
    }
}
