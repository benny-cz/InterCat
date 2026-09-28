using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using Xunit.Sdk;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A headless Avalonia fact that measures §12's query gates only when asked: set <c>INTERCAT_SCALE_OUTPUT</c> to a new
/// directory for its report. <c>INTERCAT_SCALE_SESSIONS</c> names a directory to keep the generated sessions in, so a
/// later run reuses them, and <c>INTERCAT_SCALE_ROWS</c> the sizes, comma separated (1,000,000 and 10,000,000 by default).
/// A measurement is not a test of this machine's speed, so without the variable it is reported as skipped, never passed.
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScaleGateFactAttribute : FactAttribute
{
    public const string Variable = "INTERCAT_SCALE_OUTPUT";
    public const string SessionsVariable = "INTERCAT_SCALE_SESSIONS";
    public const string RowsVariable = "INTERCAT_SCALE_ROWS";

    public ScaleGateFactAttribute()
    {
        if (OutputPath is null)
        {
            Skip = $"Set {Variable} to a new directory to measure §12's query gates at 1M and 10M observations.";
        }
    }

    public static string? OutputPath => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    public static string? SessionsPath =>
        Environment.GetEnvironmentVariable(SessionsVariable) is { Length: > 0 } path ? path : null;

    public static long[] Rows => Environment.GetEnvironmentVariable(RowsVariable) is { Length: > 0 } sizes
        ? [.. sizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(size => long.Parse(size, NumberStyles.AllowThousands, CultureInfo.InvariantCulture))]
        : [1_000_000, 10_000_000];
}

/// <summary>
/// §12's query gates over finished sessions of 1M and 10M observations: a reopen until usable, the warm 2,000-column
/// timeline query at L0 and across 40 process lanes, a bounded graph and top-100 ranking over the whole session and
/// within brushed intervals, and the first evidence page of a process or a channel. Each is measured cold, the first
/// time the session answers it after a reopen in this process, and then warm, as a distribution.
/// </summary>
public sealed class ScaleGateTests
{
    private const double ReopenBudgetMs = 3_000;
    private const double TimelineBudgetMs = 100;
    private const double RankingBudgetMs = 250;
    private const double DetailBudgetMs = 150;
    private const int Columns = 2_000;
    private const int WindowColumns = 256;
    private const int Pairs = 200;
    private const int ExecutablesPerSide = 5;
    private const long RowSpacingTicks = 600;
    private const int ChunkRows = 500_000;
    private const string Generator = "scale-gates-v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [ScaleGateFact(DisplayName = "§12: the query gates are measured over finished sessions of 1M and 10M observations")]
    public async Task MeasureQueryGatesAtScale()
    {
        string output = Path.GetFullPath(ScaleGateFactAttribute.OutputPath!);
        Directory.CreateDirectory(output);
        string sessionsRoot = Path.GetFullPath(ScaleGateFactAttribute.SessionsPath ?? Path.Combine(output, "sessions"));
        var sessions = new List<Dictionary<string, object?>>();
        foreach (long rows in ScaleGateFactAttribute.Rows)
        {
            string path = Path.Combine(sessionsRoot, string.Create(CultureInfo.InvariantCulture, $"scale-{rows}"));
            double? generatedSeconds = Ensure(path, rows);
            sessions.Add(await MeasureAsync(path, rows, generatedSeconds));
        }

        var report = new Dictionary<string, object?>
        {
            ["schema"] = "intercat.scale-gates.v1",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["machine"] = $"{Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS") ?? "?"} logical processors · {Environment.OSVersion}",

            // What the runtime schedules on, which DOTNET_PROCESSOR_COUNT lowers to stand in for §12's reference machine
            // of 8 cores and 16 threads on a larger one.
            ["processorsUsed"] = Environment.ProcessorCount,
            ["configuration"] =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            ["budgetsMs"] = new Dictionary<string, double>
            {
                ["reopenUsable"] = ReopenBudgetMs,
                ["timelineQueryWarmP95"] = TimelineBudgetMs,
                ["graphAndRankingP95"] = RankingBudgetMs,
                ["firstEvidencePageP95"] = DetailBudgetMs,
            },
            ["notes"] = new[]
            {
                "Sessions are synthetic and finished: 200 loopback TCP conversations between 400 processes of 10 "
                    + "executables of 40 processes each, every conversation also sending UDP to an off-machine resolver, "
                    + "every process created at the start, one record every 60 µs. Each was published in chunks of "
                    + "500,000 records, as a live capture publishes, and then its derivation checkpoint and overview.",
                "Cold is the first time this process answers a question of the session after reopening it: nothing "
                    + "derived is in memory and no segment is open, but the operating system's file cache may hold the "
                    + "files. Warm is the same question asked again over other viewports, brushes or scopes.",
                "The reopen is the store's open and the first overview projection; the window's open to laid out is "
                    + "reported beside it. The L0 timeline query counts 2,000 columns. At a 40-process group it is timed "
                    + "twice: with the group's 40 process lanes, counted in the same pass, at the window's widest request "
                    + "of 256 columns; and at 2,000 columns, where the 40 lanes are counted in the 500 columns the "
                    + "20,000-cell bound allows (revision 210), in the same pass as the group's own 2,000.",
                "Ranking is the bounded graph projection and the top 100 rows of the machine and group rungs; brushed, it "
                    + "includes counting the interval's records per process, edge and channel.",
            },
            ["sessions"] = sessions,
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
    }

    /// <summary>
    /// The session at <paramref name="path"/>, generated unless a finished one of this size and generator is there.
    /// Returns how long generating took, or null when an existing one was reused. A directory holding anything else is
    /// refused rather than overwritten.
    /// </summary>
    private static double? Ensure(string path, long rows)
    {
        string marker = Path.Combine(path, "scale-session.json");
        if (File.Exists(marker))
        {
            using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(marker));
            if (existing.RootElement.GetProperty("rows").GetInt64() == rows
                && existing.RootElement.GetProperty("generator").GetString() == Generator)
            {
                return null;
            }

            throw new InvalidOperationException($"{path} holds another scale session; remove it to generate this one.");
        }

        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException($"{path} is not empty and holds no scale session; it is not overwritten.");
        }

        Directory.CreateDirectory(path);
        var clock = Stopwatch.StartNew();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(path), Guid.NewGuid(), "scale-gates");
        for (long first = 0; first < rows; first += ChunkRows)
        {
            _ = Publish(store, Rows(first, (int)Math.Min(ChunkRows, rows - first)));
        }

        CheckpointPublication checkpoint = SessionCheckpoints.Publish(store, DateTimeOffset.UtcNow);
        Assert.Equal(CheckpointOutcome.Published, checkpoint.Outcome);
        File.WriteAllText(marker, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["rows"] = rows,
            ["generator"] = Generator,
            ["generatedSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        }));
        return Math.Round(clock.Elapsed.TotalSeconds, 1);
    }

    private static async Task<Dictionary<string, object?>> MeasureAsync(string path, long rows, double? generatedSeconds)
    {
        var result = new Dictionary<string, object?>
        {
            ["rows"] = rows,
            ["generatedSeconds"] = generatedSeconds,
            ["bytesOnDisk"] = Directory.EnumerateFiles(path).Sum(file => new FileInfo(file).Length),
        };

        var retained = new Dictionary<string, object?>();

        // Nothing this process derived for the session may stand in for a reopen.
        SessionDerivationCache.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var clock = Stopwatch.StartNew();
        // The store the window shares for this session, first opened here: one store per session, as in the product.
        SessionStore store = SharedSessionStores.Open(path);
        double storeOpen = clock.Elapsed.TotalMilliseconds;
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store);
        double reopen = clock.Elapsed.TotalMilliseconds;
        Retain("reopen");
        result["reopen"] = Gate(reopen, ReopenBudgetMs, new()
        {
            ["storeOpenMs"] = Round(storeOpen),
            ["segmentsOpenedBeforeFirstView"] = store.SegmentReaderCache.Entries,
            ["processes"] = overview.Nodes.Count,
            ["relationships"] = overview.Edges.Count,
        });

        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        clock.Restart();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Scale gate measurement.",
            SessionPath: path, Overview: overview), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        result["windowOpenToLaidOutMs"] = Round(clock.Elapsed.TotalMilliseconds);
        Retain("window");
        window.Close();

        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        NavigationState root = SyntheticWorkspace.Root(snapshot);
        TimeRange extent = snapshot.Extent;
        TimeRange[] viewports = [extent, .. Enumerable.Range(0, 19).Select(index => Slice(extent, index, 19, 4))];

        // L0: the whole timeline's columns. The first query opens every segment's time column.
        result["timelineL0"] = Distribution(
            TimelineBudgetMs, viewports, viewport => _ = SessionTimelineQuery.Detail(store, viewport, Columns));

        // L1: a 40-process group, whose process lanes are counted with the group's focus in one pass.
        LadderRow group = LadderProjection.Project(snapshot, root).Rows
            .First(row => snapshot.Processes.Count(process => process.GroupKey == row.Key) == 40);
        var ladder = new DetailLadder(root);
        Assert.True(ladder.TryDescend(LadderProjection.DescentFor(group, ladder.Current, extent), out string? refusal), refusal);
        TimelineFocus focus = TimelineFocus.Of(EvidenceScopes.Resolve(snapshot, ladder.Current))!;

        // As the window asks: its widest request is 256 columns, and the group's 40 process lanes come in the same pass.
        int lanes = 0;
        result["timelineL1FortyLanesAtWindowColumns"] = Distribution(TimelineBudgetMs, viewports, viewport =>
        {
            SessionFocusedTimeline focused = SessionTimelineQuery.Focused(store, viewport, WindowColumns, focus);
            lanes = focused.ProcessLanes.Count;
        });
        result["processLanesCounted"] = lanes;

        // At §12's 2,000 columns the 40 lanes would need 40,000 cells: they are counted in the 500 columns the bound
        // allows, in the same pass as the group's own 2,000.
        int laneColumns = 0;
        result["timelineL1At2000Columns"] = Distribution(TimelineBudgetMs, viewports, viewport =>
        {
            SessionFocusedTimeline focused = SessionTimelineQuery.Focused(store, viewport, Columns, focus);
            Assert.Null(focused.ProcessLaneProblem);
            laneColumns = focused.ProcessLanes[0].Buckets.Count;
        });
        result["processLaneColumnsAt2000Columns"] = laneColumns;
        Retain("timeline");

        // The bounded graph and the top 100 rows of the machine and group rungs, from the overview in memory.
        result["rankingWhole"] = Distribution(RankingBudgetMs, Enumerable.Range(0, 20).ToArray(), run =>
        {
            _ = GraphProjection.Project(snapshot);
            _ = LadderProjection.Project(snapshot, root).Rows.Take(100).ToList();
            _ = LadderProjection.Project(snapshot, ladder.Current).Rows.Take(100).ToList();
        });

        // Brushed: the interval's records counted per process, edge and channel, then the same projection and ranking.
        TimeRange[] brushes = [.. Enumerable.Range(0, 10).Select(index => Slice(extent, index, 10, 2 + (index % 4)))];
        GraphDisplay whole = GraphProjection.Project(snapshot);
        result["rankingBrushed"] = Distribution(RankingBudgetMs, brushes, brush =>
        {
            SessionIntervalCounts counts = SessionIntervalQuery.Count(store, brush);
            WorkspaceSnapshot scoped = OverviewWorkspace.WithinInterval(snapshot, counts);
            _ = GraphProjection.Rescope(whole, snapshot, scoped);
            _ = LadderProjection.Project(scoped, root).Rows.Take(100).ToList();
        });
        Retain("brushed");

        // The first evidence page of ten processes, busiest first, and of ten channels.
        ProcessInstanceId[] processes = [.. snapshot.Processes.OrderByDescending(node => node.Records).Take(10).Select(node => node.Id)];
        result["firstEvidencePageProcess"] = Distribution(DetailBudgetMs, processes,
            process => Assert.NotEmpty(SessionEvidenceQuery.Read(store, ownerProcessScope: process).Records));
        string[] channels = [.. snapshot.Channels.Take(10).Select(channel => channel.Key)];
        result["firstEvidencePageChannel"] = Distribution(DetailBudgetMs, channels,
            channel => Assert.NotEmpty(SessionEvidenceQuery.Read(store, channelKey: channel).Records));
        Retain("evidence");
        result["retained"] = retained;

        result["workingSetMiB"] = Environment.WorkingSet / 1024 / 1024;
        result["cachedReaders"] = store.SegmentReaderCache.Entries;
        return result;

        // What stays reachable after a phase: the managed heap after a full collection, and what the store's reader cache
        // admits, which is part of it.
        void Retain(string phase)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            retained[phase] = new Dictionary<string, long>
            {
                ["heapMiB"] = GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024,
                ["readerPayloadMiB"] = store.SegmentReaderCache.AdmittedPayloadBytes / 1024 / 1024,
                ["readers"] = store.SegmentReaderCache.Entries,
                ["workingSetMiB"] = Environment.WorkingSet / 1024 / 1024,
            };
        }
    }

    /// <summary>
    /// The <paramref name="index"/>th of <paramref name="count"/> windows sliding across the extent, each
    /// <paramref name="fraction"/> times narrower than it.
    /// </summary>
    private static TimeRange Slice(TimeRange extent, int index, int count, int fraction)
    {
        long width = Math.Max(1, extent.SpanTicks / fraction);
        long start = extent.StartTicks + ((extent.SpanTicks - width) * index / Math.Max(1, count - 1));
        return new(start, start + width);
    }

    /// <summary>The first answer, cold, and the rest as a distribution, against one budget for the warm p95.</summary>
    private static Dictionary<string, object?> Distribution<T>(double budgetMs, IReadOnlyList<T> inputs, Action<T> measure)
    {
        var times = new List<double>(inputs.Count);
        foreach (T input in inputs)
        {
            var clock = Stopwatch.StartNew();
            measure(input);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        double[] warm = [.. times.Skip(1).Order()];
        double p95 = warm.Length == 0 ? times[0] : warm[Math.Min(warm.Length - 1, (int)Math.Ceiling(0.95 * warm.Length) - 1)];
        return new()
        {
            ["coldMs"] = Round(times[0]),
            ["warmP50Ms"] = warm.Length == 0 ? null : Round(warm[warm.Length / 2]),
            ["warmP95Ms"] = Round(p95),
            ["warmMaxMs"] = warm.Length == 0 ? null : Round(warm[^1]),
            ["samples"] = times.Count,
            ["budgetMs"] = budgetMs,
            ["warmP95WithinBudget"] = p95 <= budgetMs,
            ["coldWithinBudget"] = times[0] <= budgetMs,
        };
    }

    private static Dictionary<string, object?> Gate(double measuredMs, double budgetMs, Dictionary<string, object?> detail)
    {
        detail["ms"] = Round(measuredMs);
        detail["budgetMs"] = budgetMs;
        detail["withinBudget"] = measuredMs <= budgetMs;
        return detail;
    }

    private static double Round(double value) => Math.Round(value, 2);

    /// <summary>
    /// Rows [first, first + count) of the synthetic capture: the 400 creations first, then each conversation in turn
    /// sending, receiving, answering and receiving over TCP, and sending one UDP datagram off the machine.
    /// </summary>
    private static ObservationRowV1[] Rows(long first, int count)
    {
        var rows = new ObservationRowV1[count];
        for (int offset = 0; offset < count; offset++)
        {
            long index = first + offset;
            long ticks = 10 + (index * RowSpacingTicks);
            ulong ordinal = (ulong)index + 1;
            ObservationRowV1 row;
            if (index < 2 * Pairs)
            {
                bool client = index < Pairs;
                int pair = (int)(index % Pairs);
                string image = string.Create(CultureInfo.InvariantCulture,
                    $@"C:\Scale\{(client ? "client" : "server")}{pair % ExecutablesPerSide}.exe");
                row = Lifecycle(ticks, ObservationKind.Create, (client ? 10_000 : 20_000) + pair, ordinal) with { ResourceName = image };
            }
            else
            {
                long conversation = index - (2 * Pairs);
                int pair = (int)(conversation % Pairs);
                int clientPid = 10_000 + pair;
                int serverPid = 20_000 + pair;
                string clientEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{40_000 + pair}");
                string serverEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + pair}");
                row = ((conversation / Pairs) % 5) switch
                {
                    0 => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 512, clientPid, ordinal).Between(clientEnd, serverEnd),
                    1 => Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 512, serverPid, ordinal).Between(serverEnd, clientEnd),
                    2 => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 4_096, serverPid, ordinal).Between(serverEnd, clientEnd),
                    3 => Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 4_096, clientPid, ordinal).Between(clientEnd, serverEnd),
                    _ => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, clientPid, ordinal)
                        .Between(string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{30_000 + pair}"),
                            string.Create(CultureInfo.InvariantCulture, $"10.0.0.{1 + (pair % 250)}:53")) with { Mechanism = Mechanism.Udp },
                };
            }

            rows[offset] = row with { SessionRelativeTicks = ticks * 100 };
        }

        return rows;
    }
}
