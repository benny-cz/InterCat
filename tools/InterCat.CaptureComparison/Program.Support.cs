using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private static TcpCoverageResult BuildCoverage(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<AdmittedEvent> admitted,
        EventAdmissionTable table,
        CaptureId captureId)
    {
        var network = new List<NetworkTransferObservation>();
        foreach (AdmittedEvent record in admitted)
        {
            AdmittedEventPlan? plan = table.FindBySourceIndex(record.SourceIndex, record.EventId, record.Version);
            if (plan is null)
            {
                continue;
            }

            NetworkTransferObservation? observation =
                NetworkObservationBuilder.TryBuild(record, plan, captureId, streamId: 1, sourceEpoch: 1);
            if (observation is not null)
            {
                network.Add(observation);
            }
        }

        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(isElevated: true);
        return TcpCoverageEvaluator.Evaluate(
            truth,
            network,
            new()
            {
                FixtureId = "FX-TCP-001",
                BuildId = environment.BuildId,
                BuildIsSupported = environment.IsSupportedBuild,
                Reproduced = false,
            });
    }

    private static OwnedSessionPlan BuildSessionPlan(
        CaptureSessionIdentity identity,
        IReadOnlyList<SourceAdmissionPlan> sources,
        IReadOnlyList<ProviderEnablementRequest> providers,
        ComparisonSettings settings) => new()
    {
        Identity = identity,
        Sources = sources,
        Providers = providers,
        QueueCapacityRecords = settings.QueueCapacityRecords,
        ReorderGrace = TimeSpan.FromSeconds(settings.ReorderGraceSeconds),
        MaximumDuration = TimeSpan.FromMinutes(5),
        PreserveExtendedData = settings.PreserveExtendedData,
    };

    private static List<ProviderEnablementRequest> BuildProviderRequests(
        IReadOnlyList<SourceAdmissionPlan> plans,
        bool requestCallStacks)
    {
        var requests = new List<ProviderEnablementRequest>(plans.Count);
        foreach (SourceAdmissionPlan plan in plans)
        {
            WindowsSourceDefinition? definition = WindowsSourceCatalog.Find(plan.SourceId);
            if (definition is null)
            {
                continue;
            }

            requests.Add(new()
            {
                SourceId = definition.SourceId,
                ProviderName = definition.ProviderName,
                ProviderGuid = plan.ProviderGuid,
                Level = 4,
                MatchAnyKeyword = definition.MatchAnyKeyword,
                MatchAllKeyword = definition.MatchAllKeyword,
                EventIdsToEnable = [.. plan.Events.Select(item => item.EventId).Distinct().Order()],
                EventIdsToDisable = definition.DeniedEventIds,
                RequestCaptureState = definition.SupportsCaptureState,
                RequestCallStacks = requestCallStacks,
            });
        }

        return requests;
    }

    private static async Task<int> RunWorkloadAsync(
        string workload,
        string truthDirectory,
        ComparisonSettings settings,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
        Add(start, "tcp-loopback");
        Add(start, "--truth", truthDirectory);
        Add(start, "--seed", settings.Seed);
        Add(start, "--connections", settings.Connections);
        Add(start, "--messages", settings.MessagesPerConnection);
        Add(start, "--bytes", settings.MaximumMessageBytes);
        Add(start, "--delay", settings.InterMessageDelayMilliseconds);
        Add(start, "--concurrency", settings.Concurrency);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The truth workload process could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<IReadOnlyList<TruthRecord>> ReadTruthAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var records = new List<TruthRecord>();
        foreach (string path in Directory.EnumerateFiles(directory, "truth-*.jsonl"))
        {
            foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                TruthRecord? record = JsonSerializer.Deserialize<TruthRecord>(line, Json);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
        }

        if (records.Count == 0)
        {
            throw new InvalidDataException($"No truth records were produced in '{directory}'.");
        }

        return records;
    }

    private static void EnsureStarted(
        string name,
        bool started,
        string? reason,
        IReadOnlyList<ProviderEnablementResult> providers)
    {
        if (started)
        {
            return;
        }

        string refusals = string.Join(
            "; ",
            providers.Where(provider => !provider.Enabled).Select(provider =>
                $"{provider.SourceId}: {provider.FailureReason}"));
        throw new InvalidOperationException(
            $"The {name} variant did not start: {reason ?? "no reason reported"}. {refusals}");
    }

    private static string? ResolveWorkload(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        string root = RepositoryRoot();
        foreach (string configuration in (string[])["Release", "Debug"])
        {
            string candidate = Path.Combine(
                root,
                "src",
                "InterCat.TestWorkloads",
                "bin",
                configuration,
                "net10.0",
                "InterCat.TestWorkloads.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate InterCat.slnx from the current directory.");
    }

    private static void Add(ProcessStartInfo start, string value) => start.ArgumentList.Add(value);

    private static void Add(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static void Add(ProcessStartInfo start, string name, int value) =>
        Add(start, name, value.ToString(CultureInfo.InvariantCulture));
}
