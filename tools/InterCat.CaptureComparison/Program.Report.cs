using System.Globalization;
using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private const int LabelWidth = 34;
    private const int ColumnWidth = 30;

    /// <summary>
    /// Prints the series as a ledger a person can read without opening the JSON: the machine it was taken
    /// on, one row per level, the detail of the last level, and every blocker numbered so it can be
    /// referred to. Values are never summed across variants, and a quantity a variant does not have prints
    /// as a stated reason rather than as zero (section 6.8, R21).
    /// </summary>
    private static void PrintSeriesSummary(CaptureSeriesResult series, string outputDirectory)
    {
        TimeSpan duration = series.CompletedUtc - series.StartedUtc;
        Console.Error.WriteLine();
        Console.Error.WriteLine("InterCat IC-009 - admitted journal versus diagnostic ETL, load series");
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  series '{series.SeriesName}'   seed {series.Seed}   {series.Levels.Count} levels   "
            + $"{duration.TotalSeconds:F0} s   call stacks: {(series.RequestCallStacks ? "requested" : "not requested")}"));
        PrintMachine(series.Machine, series.Environment);
        Console.Error.WriteLine();

        Console.Error.WriteLine(
            "  level        messages   admitted/s  queue        drops  loss  flush p99   outcome");
        foreach (CaptureComparisonResult level in series.Levels)
        {
            SaturationAssessment saturation = level.Journal.Saturation;
            CaptureStageSnapshot? stage = level.Journal.Stages.Acquisition;
            long loss = level.Journal.Health.ProviderReportedEventLoss
                + level.Journal.Health.ConsumerReportedBufferLoss;

            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {level.Level.Name,-12} {level.Level.DeclaredMessages,8:N0}   "
                + $"{saturation.AdmittedRecordsPerSecond,9:N0}  "
                + $"{QueueCell(stage),-11}  {level.Journal.Health.ApplicationDrops,5:N0}  "
                + $"{loss,4:N0}  "
                + $"{Bound(level.Journal.Stages.Writer?.DurableFlushLatency.Percentile99),-10}  "
                + $"{saturation.Outcome}"));
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  highest level with headroom   : {series.Saturation.HighestHeadroomLevel ?? "none"}");
        Console.Error.WriteLine($"  first level at the queue bound: {series.Saturation.FirstQueueSaturatedLevel ?? "none reached it"}");
        Console.Error.WriteLine($"  first level with source loss  : {series.Saturation.FirstSourceLossLevel ?? "none"}");
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  peak admitted rate            : {series.Saturation.PeakAdmittedRecordsPerSecond:N0} records/s at '{series.Saturation.PeakLevel}'"));
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  writer busiest share of window: "
            + $"{WriterBusy(series.Saturation)}"));

        if (series.Levels.Count > 0)
        {
            PrintLevelDetail(series.Levels[^1]);
        }

        Console.Error.WriteLine();
        if (series.DecisionBlockers.Count == 0)
        {
            Console.Error.WriteLine("No decision blockers remain. Review ADR-008 before accepting a direction.");
        }
        else
        {
            Console.Error.WriteLine($"Decision blockers ({series.DecisionBlockers.Count}):");
            for (int index = 0; index < series.DecisionBlockers.Count; index++)
            {
                Console.Error.WriteLine($"  {index + 1}. {Wrap(series.DecisionBlockers[index])}");
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Artifacts: {outputDirectory}");
    }

    private static string WriterBusy(SeriesSaturationSummary saturation) =>
        saturation.PeakWriterBusyFraction is { } fraction
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{fraction:P1} at '{saturation.PeakWriterBusyLevel}'"
                + $"{(saturation.WriterSetThePaceAtSomeLevel ? " - the writer set the pace there" : string.Empty)}")
            : "not measured";

    private static void PrintMachine(MachineDescriptor machine, ProbeEnvironment environment)
    {
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  build {environment.BuildId} ({environment.Support.Describe()})"
            + $"   {machine.LogicalProcessors} logical processors"
            + $"   {machine.TotalPhysicalMemoryBytes / (1024d * 1024 * 1024):F0} GiB"));
        StorageMeasurement storage = machine.Storage;
        string throughput = storage.UnavailableReason is { } reason
            ? $"not measured ({reason})"
            : $"{Rate(storage.SequentialWriteBytesPerSecond)} write, {Rate(storage.SequentialReadBytesPerSecond)} read";
        Console.Error.WriteLine(
            $"  volume {storage.Volume} ({storage.FileSystem}): {throughput}"
            + $" - reference device: {Reference(storage.MeetsReferenceDevice)}");
    }

    /// <summary>The side-by-side ledger for one level, printed for the last level the series ran.</summary>
    private static void PrintLevelDetail(CaptureComparisonResult result)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Level '{result.Level.Name}': {result.Level.Intent}");
        Console.Error.WriteLine();
        Row("", "journal", "ETL");
        Row(
            "truth records",
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
        Row("loss provider/consumer/drops", Loss(result.Journal), Loss(result.Etl));
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
        Row("extended items read / kept", ExtendedItems(result.Journal), ExtendedItems(result.Etl));
        Row(
            "allocation admission / adapter",
            Allocations(result.Journal.Allocations),
            Allocations(result.Etl.Allocations));
        Row("source clock", Clock(result.Journal.Clock), Clock(result.Etl.Clock));

        if (result.Journal.ExtendedData.TypesObserved.Count > 0)
        {
            Console.Error.WriteLine(
                "  extended types seen: " + string.Join(", ", result.Journal.ExtendedData.TypesObserved));
        }

        foreach (string degradation in result.Journal.Degradations
            .Concat(result.Etl.Degradations)
            .Distinct(StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"  degraded: {degradation}");
        }
    }

    private static void Row(string label, string journal, string etl) =>
        Console.Error.WriteLine("  " + label.PadRight(LabelWidth) + journal.PadRight(ColumnWidth) + etl);

    private static string QueueCell(CaptureStageSnapshot? stage) => stage is null
        ? "none"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{stage.QueueHighWaterRecords:N0}/{stage.QueueCapacityRecords:N0}");

    private static string Rate(double? bytesPerSecond) => bytesPerSecond is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000d:F2} GB/s")
        : "unmeasured";

    private static string Reference(bool? meets) => meets switch
    {
        true => "met",
        false => "not met",
        null => "unknown",
    };

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

    /// <summary>
    /// Bytes per record InterCat's admission allocated, and bytes per record the adapter allocated
    /// dispatching the same evidence. They are different costs and are never added (section 18.3).
    /// </summary>
    private static string Allocations(AllocationAttribution? attribution) => attribution is null
        ? "not attributed"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{attribution.AdmissionBytesPerRecord:N0} B / {attribution.AdapterBytesPerRecord:N0} B per record");

    private static string Pair(long left, long right) => string.Create(
        CultureInfo.InvariantCulture,
        $"{left:N0} / {right:N0}");

    /// <summary>
    /// The three loss quantities, never summed. An unreadable provider counter prints as unknown, because
    /// a counter that could not be read is not a counter that read zero (R3).
    /// </summary>
    private static string Loss(CaptureComparisonVariant variant)
    {
        CaptureHealthSnapshot health = variant.Health;
        string provider = variant.SourceLossKnown
            ? health.ProviderReportedEventLoss.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{provider} / {health.ConsumerReportedBufferLoss} / {health.ApplicationDrops}");
    }

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

    private static string Writer(JournalProbeWriterMetrics? writer) => writer is null
        ? "owned by Windows"
        : $"{Cpu(writer.WriterThreadCpu)} / {Bound(writer.DurableFlushLatency.Percentile99)}";

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

    /// <summary>
    /// A bucket's upper bound in the largest unit that does not round it away. A sub-microsecond bucket
    /// printed as "&lt;0 us" would read as zero, which is the one thing a bound must never say (R3).
    /// </summary>
    private static string Bound(LatencyBound? bound) => bound is { } value
        ? value.UpperNanoseconds switch
        {
            long.MaxValue => "unbounded",
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"<{value.UpperNanoseconds / 1_000_000} ms"),
            >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"<{value.UpperNanoseconds / 1_000} us"),
            _ => string.Create(CultureInfo.InvariantCulture, $"<{value.UpperNanoseconds} ns"),
        }
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
        var builder = new StringBuilder(text.Length + 16);
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
