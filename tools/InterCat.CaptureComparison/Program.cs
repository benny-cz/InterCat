using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Benchmarks;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] SeriesNotes =
    [
        "journal-probe-v0 and callback-envelope-candidate-v0 are disposable IC-009 formats, not journal-v1.",
        "The ETL is separately labeled original diagnostic evidence and is not claimed to be metadata-only.",
        "Coverage is evaluated independently against each level's own truth log; records from two runs are never merged.",
        "Loss, application drops and policy omissions remain separate counters and are never summed.",
        "Per-thread processor time comes from the Windows thread clock, whose tick is about 15.6 ms; a zero reading means below one tick, not free.",
        "Extended-data items are opt-in per provider enablement, so a run that does not request them observes none. That is a setting, not an absence of items.",
        "A constrained level shrinks a bound instead of raising the rate. Its drops are the bound working, not a source failure, and the constraint is recorded with them.",
    ];

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            Console.Error.WriteLine("Cancelling; each owned capture will stop before disposal.");
        };

        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Comparison cancelled; partial artifacts were retained and are not decision evidence.");
            return 5;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }

        if (!TryParse(args, out Options? options, out string? error))
        {
            Console.Error.WriteLine(error);
            PrintHelp();
            return 2;
        }

        var liveHost = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(liveHost.IsElevated);
        if (!environment.IsElevated)
        {
            Console.Error.WriteLine(
                "The journal-versus-ETL comparison requires an elevated Windows shell; no capture was started.");
            return 3;
        }

        string? workload = ResolveWorkload(options.WorkloadPath);
        if (workload is null)
        {
            Console.Error.WriteLine(
                "InterCat.TestWorkloads.exe was not found. Build it or pass --workload <path>.");
            return 2;
        }

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(outputDirectory))
        {
            Console.Error.WriteLine($"Refusing to replace the existing comparison directory '{outputDirectory}'.");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

        Console.Error.WriteLine("Measuring the output volume before any capture starts...");
        MachineDescriptor machine = MachineProbe.Describe(outputDirectory);

        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        string[] sourceIds =
        [
            WindowsSourceCatalog.KernelProcessSourceId,
            WindowsSourceCatalog.KernelNetworkSourceId,
        ];
        Console.Error.WriteLine("Compiling one admission plan for every level and both evidence variants...");
        IReadOnlyList<SourceAdmissionPlan> sources = probe.CompilePlans(
            sourceIds,
            out IReadOnlyList<string> refusals);
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("No requested source compiled into an admission plan.");
            return 3;
        }

        List<ProviderEnablementRequest> providers = BuildProviderRequests(sources, options.RequestCallStacks);
        if (providers.Count == 0)
        {
            Console.Error.WriteLine("No provider request could be built from the admitted sources.");
            return 3;
        }

        var levels = new List<CaptureComparisonResult>(options.Levels.Count);
        for (int index = 0; index < options.Levels.Count; index++)
        {
            LoadLevel level = options.Levels[index];
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Level {index + 1}/{options.Levels.Count} '{level.Name}': {level.DeclaredMessages:N0} messages, "
                + $"queue {level.QueueCapacityRecords:N0}, batch {level.JournalBatchRecords:N0}..."));
            levels.Add(await RunLevelAsync(
                outputDirectory,
                workload,
                environment,
                sources,
                providers,
                refusals,
                options,
                level,
                cancellationToken).ConfigureAwait(false));
        }

        SeriesSaturationSummary saturation = Summarize(levels);
        AllocationSlope? slope = MeasureAllocationSlope(levels);
        List<string> blockers = BuildSeriesBlockers(levels, saturation, machine, environment);
        var series = new CaptureSeriesResult
        {
            Schema = "intercat.capture-comparison-series.v0",
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            Machine = machine,
            SeriesName = options.SeriesName,
            Seed = options.Seed,
            RecordBudget = options.RecordBudget,
            PreserveExtendedData = options.PreserveExtendedData,
            RequestCallStacks = options.RequestCallStacks,
            PlanRefusals = refusals,
            Levels = levels,
            Saturation = saturation,
            Budgets = EvaluateBudgets(levels, slope),
            AllocationSlope = slope,
            DecisionReady = blockers.Count == 0,
            DecisionBlockers = blockers,
            Notes = SeriesNotes,
        };

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "series.json"),
            JsonSerializer.Serialize(series, Json),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(series, Json));
        PrintSeriesSummary(series, outputDirectory);
        return series.DecisionReady ? 0 : 1;
    }

    private static async Task<CaptureComparisonResult> RunLevelAsync(
        string outputDirectory,
        string workload,
        ProbeEnvironment environment,
        IReadOnlyList<SourceAdmissionPlan> sources,
        IReadOnlyList<ProviderEnablementRequest> providers,
        IReadOnlyList<string> refusals,
        Options options,
        LoadLevel level,
        CancellationToken cancellationToken)
    {
        string levelDirectory = Path.Combine(outputDirectory, "levels", level.Name);
        Directory.CreateDirectory(levelDirectory);
        var settings = new ComparisonSettings(
            options.Seed,
            level.Connections,
            level.MessagesPerConnection,
            level.MaximumMessageBytes,
            level.InterMessageDelayMilliseconds,
            level.Concurrency,
            options.GraceSeconds,
            level.QueueCapacityRecords,
            level.JournalBatchRecords,
            options.RecordBudget,
            options.PreserveExtendedData,
            options.RequestCallStacks);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        Console.Error.WriteLine("  admitted-journal callback envelope...");
        CaptureComparisonVariant journal = await RunJournalAsync(
            levelDirectory,
            workload,
            sources,
            providers,
            settings,
            cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine("  diagnostic ETL...");
        CaptureComparisonVariant etl = await RunEtlAsync(
            levelDirectory,
            workload,
            sources,
            providers,
            settings,
            cancellationToken).ConfigureAwait(false);

        var result = new CaptureComparisonResult
        {
            Schema = "intercat.capture-comparison.v1",
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            Level = level,
            Settings = settings,
            PlanRefusals = refusals,
            Journal = journal,
            Etl = etl,
            SameSeededWorkload = true,
            LevelBlockers = BuildLevelBlockers(level, journal, etl),
            Notes = SeriesNotes,
        };

        await File.WriteAllTextAsync(
            Path.Combine(levelDirectory, "comparison.json"),
            JsonSerializer.Serialize(result, Json),
            cancellationToken).ConfigureAwait(false);
        return result;
    }
}
