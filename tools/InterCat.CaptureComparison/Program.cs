using System.Text.Json;
using System.Text.Json.Serialization;
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

        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        string[] sourceIds =
        [
            WindowsSourceCatalog.KernelProcessSourceId,
            WindowsSourceCatalog.KernelNetworkSourceId,
        ];
        Console.Error.WriteLine("Compiling one admission plan for both evidence variants...");
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

        var settings = new ComparisonSettings(
            options.Seed,
            options.Connections,
            options.Messages,
            options.MaximumBytes,
            options.GraceSeconds,
            options.JournalBatchRecords,
            options.RecordBudget,
            options.PreserveExtendedData,
            options.RequestCallStacks);

        Console.Error.WriteLine("Running admitted-journal projection variant...");
        CaptureComparisonVariant journal = await RunJournalAsync(
            outputDirectory,
            workload,
            sources,
            providers,
            settings,
            cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine("Running diagnostic ETL variant...");
        CaptureComparisonVariant etl = await RunEtlAsync(
            outputDirectory,
            workload,
            sources,
            providers,
            settings,
            cancellationToken).ConfigureAwait(false);

        List<string> blockers = BuildDecisionBlockers(journal, etl);
        var result = new CaptureComparisonResult
        {
            Schema = "intercat.capture-comparison.v0",
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            Settings = settings,
            PlanRefusals = refusals,
            Journal = journal,
            Etl = etl,
            SameSeededWorkload = true,
            DecisionReady = blockers.Count == 0,
            DecisionBlockers = blockers,
            Notes =
            [
                "journal-probe-v0 and callback-envelope-candidate-v0 are disposable IC-009 formats, not journal-v1.",
                "Extended-data items are copied as bounded byte prefixes; a longer item keeps its original length and a truncation flag.",
                "A denied extended type is a counted policy omission, not an unexplained gap; its bytes are never persisted.",
                "The ETL is separately labeled original diagnostic evidence and is not claimed to be metadata-only.",
                "Coverage is evaluated independently against each seeded run's truth log; records from the two runs are never merged.",
                "Loss, application drops and policy omissions remain separate counters and are never summed.",
                "Per-thread processor time comes from the Windows thread clock, whose tick is about 15.6 ms; a zero reading means below one tick, not free.",
                "Extended-data items are opt-in per provider enablement, so a run that does not request them observes none. That is a setting, not an absence of items.",
            ],
        };

        string resultPath = Path.Combine(outputDirectory, "comparison.json");
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, Json),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        PrintSummary(result, outputDirectory);
        return result.DecisionReady ? 0 : 1;
    }
}
