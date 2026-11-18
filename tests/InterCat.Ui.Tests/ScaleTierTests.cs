using System.Globalization;
using System.Text.Json;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// §12.1's tiers as M5's exit gate measures them: the scale gate (<see cref="ScaleGateTests"/>) run over a session in each of
/// T1, T2 and T3, each run in a process of its own that reused its session and asked nothing before it, the tiers taking
/// turns round by round - held here to the runs the repository keeps (bench/results/scale-tiers-*).
/// </summary>
public sealed class ScaleTierTests
{
    /// <summary>§12's reopen budget: an indexed session usable within 3 seconds.</summary>
    private const double ReopenBudgetMs = 3_000;

    /// <summary>S1's TUNABLE: a T3 reopen takes at most twice a T1 reopen.</summary>
    private const double ReopenRatio = 2;

    /// <summary>§12's memory budgets for analysis and cache, and for the UI and its layout: 512 MiB each.</summary>
    private const long AnalysisBudgetMiB = 512;
    private const long UiBudgetMiB = 512;

    /// <summary>
    /// How far a T3 session's steady state may sit from a T1 session's and still be the same within the budgets: less than
    /// twice it, where it would double were it held in proportion to a session fourteen times as large.
    /// </summary>
    private const double SteadyRatio = 2;

    /// <summary>The tiers M5's exit gate measures, each at least <see cref="RunsPerTier"/> times.</summary>
    private static readonly string[] Tiers = ["T1", "T2", "T3"];

    private const int RunsPerTier = 3;

    /// <summary>§12's timeline budget: a warm 2,000-column query with 40 lanes within 100 ms at the 95th percentile.</summary>
    private const double TimelineBudgetMs = 100;

    /// <summary>
    /// The queries a focus counts from its members' postings since revision 1064 (ADR-113): a 40-process group's lanes, and
    /// the busiest connection's two ends.
    /// </summary>
    private static readonly string[] PostedGates = ["timelineL1At2000Columns", "timelineL3ChannelEndsAt2000Columns"];

    [Fact(DisplayName = "S3: at every tier a group's 40 lanes and a connection's two ends answer within §12's 100 ms, warm, at the median of the runs' 95th percentiles")]
    public void AFocusAnswersWithinItsBudgetAtEveryTier()
    {
        IReadOnlyList<Run> runs = Runs();
        Assert.All(Tiers, tier => Assert.All(PostedGates, gate =>
        {
            double p95 = Median(runs, tier, run => run.WarmP95[gate]);
            Assert.True(p95 <= TimelineBudgetMs, $"{gate} took {p95} ms at {tier}'s 95th percentile, at the median of its runs, past §12's 100 ms.");
        }));
    }

    [Fact(DisplayName = "S1: a T3 session is usable as soon as a T1 session is - within twice its time and §12's 3 s, opening no segment and reading at most twice the bytes a T1 reopen reads from a session ten times as large or more")]
    public void AReopenIsIndependentOfTheSessionsSize()
    {
        IReadOnlyList<Run> runs = Runs();
        Assert.All(Tiers, tier => Assert.True(runs.Count(run => run.Tier == tier) >= RunsPerTier,
            $"The repository keeps {runs.Count(run => run.Tier == tier)} runs at {tier}, where M5's gate measures each tier at least {RunsPerTier} times."));
        Assert.All(runs, run =>
        {
            Assert.True(run.ReopenMs <= ReopenBudgetMs, $"{run.Name} reopened in {run.ReopenMs} ms, past §12's 3 s.");
            Assert.Equal(0, run.SegmentsOpenedBeforeFirstView);
        });

        double t1 = Median(runs, "T1", static run => run.ReopenMs);
        double t3 = Median(runs, "T3", static run => run.ReopenMs);
        Assert.True(t3 <= ReopenRatio * t1, $"A T3 reopen took {t3} ms at the median, past twice a T1 reopen's {t1} ms.");

        // What the reopen reads is the manifest, the overview's top level and the index heads it needs, never the
        // observations: a T3 session holds many times a T1 session's bytes, its reopen at most twice a T1 reopen's.
        double sizeRatio = Median(runs, "T3", static run => run.SessionBytes) / Median(runs, "T1", static run => run.SessionBytes);
        Assert.True(sizeRatio >= 10, $"The T3 session is only {sizeRatio:0.0} times the T1 session.");
        double readT1 = Median(runs, "T1", static run => run.ReopenBytesRead);
        double readT3 = Median(runs, "T3", static run => run.ReopenBytesRead);
        Assert.True(readT3 <= ReopenRatio * readT1,
            $"A T3 reopen read {readT3:0} bytes at the median, past twice a T1 reopen's {readT1:0}, while the session is {sizeRatio:0.0} times as large.");
    }

    [Fact(DisplayName = "S2: a T3 session's steady state is a T1 session's - its managed heap, working set and private bytes within §12's 512 MiB analysis and cache and 512 MiB UI and layout budgets, none of them twice a T1 session's")]
    public void ResidentMemoryIsIndependentOfTheSessionsSize()
    {
        IReadOnlyList<Run> runs = Runs();
        Assert.All(runs, run =>
        {
            // Everything the process holds once every question has been asked, the runtime and the window's own included,
            // within both budgets together, and the segment readers' cache within the analysis and cache budget alone.
            Assert.True(run.SteadyWorkingSetMiB <= AnalysisBudgetMiB + UiBudgetMiB,
                $"{run.Name} held a working set of {run.SteadyWorkingSetMiB} MiB once every question was asked.");
            Assert.True(run.SteadyPrivateMiB <= AnalysisBudgetMiB + UiBudgetMiB,
                $"{run.Name} held {run.SteadyPrivateMiB} MiB of private bytes once every question was asked.");
            Assert.True(run.SteadyHeapMiB <= AnalysisBudgetMiB + UiBudgetMiB,
                $"{run.Name} kept {run.SteadyHeapMiB} MiB of managed heap once every question was asked.");
            Assert.True(run.SteadyReaderPayloadMiB <= AnalysisBudgetMiB,
                $"{run.Name}'s reader cache admitted {run.SteadyReaderPayloadMiB} MiB, past the analysis and cache budget.");
        });

        // Bounded by the viewport, the row budgets and the cache budgets, never by the records: a session many times as
        // large holds about what a T1 session's does - at the median, less than twice it by every measure.
        (string What, Func<Run, double> Of)[] measures =
        [
            ("managed heap", static run => run.SteadyHeapMiB),
            ("working set", static run => run.SteadyWorkingSetMiB),
            ("private bytes", static run => run.SteadyPrivateMiB),
        ];
        foreach ((string what, Func<Run, double> of) in measures)
        {
            double t1 = Median(runs, "T1", of);
            double t3 = Median(runs, "T3", of);
            Assert.True(t3 < SteadyRatio * t1, $"A T3 session's steady {what}, {t3} MiB at the median, is twice a T1 session's {t1} MiB or more.");
        }
    }

    private static double Median(IReadOnlyList<Run> runs, string tier, Func<Run, double> value)
    {
        double[] values = [.. runs.Where(run => run.Tier == tier).Select(value).Order()];
        Assert.True(values.Length >= RunsPerTier, $"The repository keeps {values.Length} runs at {tier}; S1 and S2 are read from at least {RunsPerTier}.");
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
    }

    /// <summary>
    /// The newest scale-tiers measurement's runs, each a report of one session the run reused, in an uncompressed folder,
    /// with the tier the product measured it in.
    /// </summary>
    private static IReadOnlyList<Run> Runs()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string[] measurements = [.. Directory.EnumerateDirectories(Path.Combine(root.FullName, "bench", "results"), "scale-tiers-*").Order(StringComparer.Ordinal)];
        Assert.NotEmpty(measurements);
        string[] reports = [.. Directory.EnumerateDirectories(measurements[^1])
            .Select(static run => Path.Combine(run, "report.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)];
        Assert.NotEmpty(reports);
        return [.. reports.Select(Run.Read)];
    }

    private sealed record Run(
        string Name,
        string Tier,
        double SessionBytes,
        double ReopenMs,
        double ReopenBytesRead,
        long SegmentsOpenedBeforeFirstView,
        double SteadyHeapMiB,
        double SteadyReaderPayloadMiB,
        double SteadyWorkingSetMiB,
        double SteadyPrivateMiB,
        IReadOnlyDictionary<string, double> WarmP95)
    {
        public static Run Read(string report)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
            JsonElement[] sessions = [.. document.RootElement.GetProperty("sessions").EnumerateArray()];
            string name = Path.GetFileName(Path.GetDirectoryName(report))!;
            JsonElement session = Assert.Single(sessions);

            // A session generated by the measuring process carries what generating it left there; a compressed folder
            // decompresses every cold read. Neither is a session's own reopen.
            Assert.True(session.GetProperty("generatedSeconds").ValueKind == JsonValueKind.Null, $"{name} generated its session.");
            Assert.False(session.GetProperty("filesCompressed").GetBoolean(), $"{name}'s session was in a compressed folder.");
            JsonElement size = session.GetProperty("size");
            JsonElement reopen = session.GetProperty("reopen");
            JsonElement steady = session.GetProperty("steadyState");
            return new(
                name,
                size.GetProperty("tier").GetString()!,
                size.GetProperty("bytes").GetInt64(),
                reopen.GetProperty("ms").GetDouble(),
                reopen.GetProperty("bytesRead").GetInt64(),
                reopen.GetProperty("segmentsOpenedBeforeFirstView").GetInt64(),
                steady.GetProperty("heapMiB").GetInt64(),
                steady.GetProperty("readerPayloadMiB").GetInt64(),
                steady.GetProperty("workingSetMiB").GetInt64(),
                steady.GetProperty("privateMiB").GetInt64(),
                PostedGates.ToDictionary(static gate => gate, gate => session.GetProperty(gate).GetProperty("warmP95Ms").GetDouble()));
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Tier})");
    }
}
