using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private const int LabelWidth = 34;
    private const int ColumnWidth = 30;

    /// <summary>
    /// Prints the run as a side-by-side ledger a person can read without opening the JSON. Values are
    /// never summed across the two variants, a quantity a variant does not have prints as a stated
    /// reason rather than as zero, and every blocker is numbered so it can be referred to (section 6.8).
    /// </summary>
    private static void PrintSummary(CaptureComparisonResult result, string outputDirectory)
    {
        TimeSpan duration = result.CompletedUtc - result.StartedUtc;
        Console.Error.WriteLine();
        Console.Error.WriteLine("InterCat IC-009 - admitted journal versus diagnostic ETL");
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  build {result.Environment.BuildId} ({(result.Environment.IsSupportedBuild ? "supported" : "untested")})"
            + $"   seed {result.Settings.Seed}"
            + $"   {result.Settings.Connections} connections x {result.Settings.MessagesPerConnection} messages"
            + $"   {duration.TotalSeconds:F1} s"));
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  journal: {result.Journal.Name}   ETL: {result.Etl.Name}"
            + $"   extended data: {(result.Settings.PreserveExtendedData ? "copied" : "not requested")}"
            + $"   call stacks: {(result.Settings.RequestCallStacks ? "requested" : "not requested")}"));
        Console.Error.WriteLine();
        Row("", "journal", "ETL");
        Row(
            "truth operations",
            result.Journal.TruthRecords.ToString("N0", CultureInfo.InvariantCulture),
            result.Etl.TruthRecords.ToString("N0", CultureInfo.InvariantCulture));
        Row(
            "observed / admitted",
            Pair(result.Journal.Health.ObservedRecords, result.Journal.Health.AdmittedRecords),
            Pair(result.Etl.Health.ObservedRecords, result.Etl.Health.AdmittedRecords));
        Row(
            "coverage tier",
            result.Journal.Coverage.Assessment.Tier.ToString(),
            result.Etl.Coverage.Assessment.Tier.ToString());
        Row("loss provider/consumer/drops", Loss(result.Journal.Health), Loss(result.Etl.Health));
        Row("evidence", Evidence(result.Journal.Evidence), Evidence(result.Etl.Evidence));
        Row(
            "callback p50 / p99",
            CallbackLatency(result.Journal.Stages.Acquisition),
            CallbackLatency(result.Etl.Stages.Acquisition));
        Row(
            "callback cpu / allocations",
            CallbackCost(result.Journal.Stages.Acquisition),
            CallbackCost(result.Etl.Stages.Acquisition));
        Row("queue high-water", Queue(result.Journal.Stages.Acquisition), Queue(result.Etl.Stages.Acquisition));
        Row("writer cpu / flush p99", Writer(result.Journal.Stages.Writer), Writer(result.Etl.Stages.Writer));
        Row(
            "replay cpu / allocations",
            CallbackCost(result.Journal.Stages.Replay),
            CallbackCost(result.Etl.Stages.Replay));
        Row(
            "extended items read / kept",
            ExtendedItems(result.Journal),
            ExtendedItems(result.Etl));
        Row("source clock", Clock(result.Journal.Clock), Clock(result.Etl.Clock));
        Console.Error.WriteLine();

        if (result.Journal.ExtendedData.TypesObserved.Count > 0)
        {
            Console.Error.WriteLine(
                "  extended types seen: " + string.Join(", ", result.Journal.ExtendedData.TypesObserved));
        }

        foreach (string degradation in result.Journal.Degradations.Concat(result.Etl.Degradations).Distinct(StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"  degraded: {degradation}");
        }

        Console.Error.WriteLine();
        if (result.DecisionBlockers.Count == 0)
        {
            Console.Error.WriteLine("No decision blockers remain. Review ADR-008 before accepting a direction.");
            return;
        }

        Console.Error.WriteLine($"Decision blockers ({result.DecisionBlockers.Count}):");
        for (int index = 0; index < result.DecisionBlockers.Count; index++)
        {
            Console.Error.WriteLine($"  {index + 1}. {Wrap(result.DecisionBlockers[index])}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Artifacts: {outputDirectory}");
    }

    private static void Row(string label, string journal, string etl) =>
        Console.Error.WriteLine(
            "  " + label.PadRight(LabelWidth) + journal.PadRight(ColumnWidth) + etl);

    /// <summary>
    /// Items read out of callback memory and items persisted in an envelope. A variant that persists no
    /// envelope prints that, because zero kept items would otherwise read as a loss.
    /// </summary>
    private static string ExtendedItems(CaptureComparisonVariant variant)
    {
        string read = variant.ExtendedData.UnavailableReason is null
            ? variant.ExtendedData.CopiedInCallback.ToString("N0", CultureInfo.InvariantCulture)
            : "unreachable";
        string kept = variant.Clock.Descriptor is null
            ? "no envelope"
            : variant.ExtendedData.PersistedInEnvelopes.ToString("N0", CultureInfo.InvariantCulture);
        return $"{read} / {kept}";
    }

    private static string Pair(long left, long right) => string.Create(
        CultureInfo.InvariantCulture,
        $"{left:N0} / {right:N0}");

    private static string Loss(CaptureHealthSnapshot health) => string.Create(
        CultureInfo.InvariantCulture,
        $"{health.ProviderReportedEventLoss} / {health.ConsumerReportedBufferLoss} / {health.ApplicationDrops}");

    private static string Evidence(EvidenceArtifact evidence) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Bytes(evidence.Bytes)}, {evidence.DurableFlushes} flushes");

    private static string CallbackLatency(CaptureStageSnapshot? stage) => stage is null
        ? "not applicable"
        : $"{Bound(stage.CallbackLatency.Median)} / {Bound(stage.CallbackLatency.Percentile99)}";

    private static string CallbackCost(CaptureStageSnapshot? stage)
    {
        if (stage is null)
        {
            return "not applicable";
        }

        string allocated = stage.DeliveryThreadAllocatedBytes is { } bytes ? Bytes(bytes) : "unmeasured";
        return $"{Cpu(stage.DeliveryThreadCpu?.Total)} / {allocated}";
    }

    private static string Queue(CaptureStageSnapshot? stage) => stage is null
        ? "no in-process queue"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{stage.QueueHighWaterRecords:N0} / {stage.QueueCapacityRecords:N0}");

    private static string Writer(JournalProbeWriterMetrics? writer)
    {
        if (writer is null)
        {
            return "owned by Windows";
        }

        return $"{Cpu(writer.WriterThreadCpu)} / {Bound(writer.DurableFlushLatency.Percentile99)}";
    }

    /// <summary>
    /// Windows reports per-thread processor time in clock ticks of roughly 15.6 ms, so a zero reading
    /// means the stage stayed under one tick, not that it was free. The two readings say different things
    /// and are printed differently (R3).
    /// </summary>
    private static string Cpu(TimeSpan? time) => time switch
    {
        null => "cpu unreadable",
        { Ticks: 0 } => "under one clock tick",
        { } value => string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:F0} ms"),
    };

    private static string Clock(VariantClockEvidence clock)
    {
        if (clock.Descriptor is null)
        {
            return "not persisted";
        }

        string replayed = clock.ReplayedIdentical switch
        {
            true => "replayed identical",
            false => "replay differs",
            null => "not replayed",
        };
        return $"{clock.Confidence.ToString().ToLowerInvariant()}, {replayed}";
    }

    private static string Bound(LatencyBound? bound) => bound is { } value
        ? value.UpperNanoseconds >= 1_000_000
            ? string.Create(CultureInfo.InvariantCulture, $"<{value.UpperNanoseconds / 1_000_000} ms")
            : string.Create(CultureInfo.InvariantCulture, $"<{value.UpperNanoseconds / 1_000} us")
        : "no sample";

    private static string Bytes(long value) => value switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{value / (1024.0 * 1024.0):F1} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{value / 1024.0:F1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value} B"),
    };

    /// <summary>Indents continuation lines so a numbered blocker stays visually one item.</summary>
    private static string Wrap(string text)
    {
        const int width = 92;
        var builder = new System.Text.StringBuilder(text.Length + 16);
        int column = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (column > 0 && column + word.Length + 1 > width)
            {
                builder.Append("\n     ");
                column = 0;
            }
            else if (column > 0)
            {
                builder.Append(' ');
                column++;
            }

            builder.Append(word);
            column += word.Length;
        }

        return builder.ToString();
    }
}
