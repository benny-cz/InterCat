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
        QueueCapacityRecords = Math.Min(settings.RecordBudget, 65_536),
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

    private static List<string> BuildDecisionBlockers(
        CaptureComparisonVariant journal,
        CaptureComparisonVariant etl)
    {
        var blockers = new List<string>
        {
            "This run measures one configured load point. ADR-008 also requires a declared load series "
                + "that reaches queue and disk saturation, on the reference storage device.",
        };
        AddExtendedDataBlockers(journal, blockers);
        AddClockBlockers(journal, blockers);
        AddStageBlockers(journal, blockers);
        if (journal.ExactAdmittedProjectionReplay != true)
        {
            blockers.Add("The admitted-journal envelope did not replay with an identical ordered fingerprint.");
        }

        AddHealthBlockers("journal", journal, blockers);
        AddHealthBlockers("ETL", etl, blockers);
        AddCoverageBlockers("journal", journal, blockers);
        AddCoverageBlockers("ETL", etl, blockers);

        return blockers;
    }

    private static void AddExtendedDataBlockers(CaptureComparisonVariant journal, List<string> blockers)
    {
        if (journal.ExtendedData.UnavailableReason is { } reason)
        {
            blockers.Add($"Extended-data items could not be read during capture: {reason}");
            return;
        }

        if (journal.ExtendedData.CopiedInCallback == 0)
        {
            blockers.Add(
                "Every delivered record declared no extended-data item, because extended data is opt-in per "
                + "provider enablement. Envelope fidelity for extended items is therefore untested at this "
                + "load point; rerun with --request-stacks true to exercise it.");
            return;
        }

        if (journal.ExtendedData.PersistedInEnvelopes != journal.ExtendedData.ReplayedIdentical)
        {
            blockers.Add(
                $"Replay returned {journal.ExtendedData.ReplayedIdentical} extended items for "
                + $"{journal.ExtendedData.PersistedInEnvelopes} persisted ones.");
        }
    }

    private static void AddClockBlockers(CaptureComparisonVariant journal, List<string> blockers)
    {
        if (journal.Clock.Confidence != SourceClockConfidence.Confirmed)
        {
            blockers.Add(
                "The capture's source clock is "
                + $"{journal.Clock.Confidence.ToString().ToLowerInvariant()}: "
                + (journal.Clock.RefusalReason ?? "no delivered record was checked against it."));
        }

        if (journal.Clock.ReplayedIdentical != true)
        {
            blockers.Add("The persisted source clock descriptor did not read back identically from the journal.");
        }
    }

    private static void AddStageBlockers(CaptureComparisonVariant journal, List<string> blockers)
    {
        CaptureStageSnapshot? acquisition = journal.Stages.Acquisition;
        if (acquisition is null || acquisition.CallbackLatency.Samples == 0)
        {
            blockers.Add("No callback latency sample was recorded for the journal variant.");
            return;
        }

        if (acquisition.DeliveryThreadCpu is null)
        {
            blockers.Add("The delivery thread's processor time could not be read on this host.");
        }

        if (acquisition.DeliveryThreadAllocatedBytes is null)
        {
            blockers.Add("The delivery thread's allocation total was not recorded.");
        }

        if (journal.Stages.Writer is not { } writer)
        {
            blockers.Add("The journal writer stage produced no metrics.");
            return;
        }

        if (writer.DurableFlushLatency.Samples == 0)
        {
            blockers.Add("No durable flush latency sample was recorded for the journal writer.");
        }

        if (writer.WriterThreadCpu is null)
        {
            blockers.Add("The journal writer thread's processor time could not be read on this host.");
        }
    }

    /// <summary>
    /// A coverage blocker names the criterion that failed. The unsupported-build gap is reported
    /// separately, because a met threshold on an untested build is not a missed threshold (P27).
    /// </summary>
    private static void AddCoverageBlockers(
        string name,
        CaptureComparisonVariant variant,
        List<string> blockers)
    {
        var criterionGaps = new HashSet<string>(StringComparer.Ordinal);
        foreach (TierCriterion criterion in variant.Coverage.Assessment.Criteria)
        {
            if (criterion.Satisfied)
            {
                continue;
            }

            if (criterion.Gap is { } gap)
            {
                criterionGaps.Add(gap);
            }

            blockers.Add($"The {name} variant missed {criterion.Name}: {criterion.Gap ?? criterion.Requirement}");
        }

        // The remaining gaps are the assessment's own, such as an unsupported build. A gap already stated
        // as a missed criterion is not repeated, so the count of blockers stays the count of problems.
        foreach (string gap in variant.Coverage.Assessment.Gaps)
        {
            if (!criterionGaps.Contains(gap) && !blockers.Contains(gap, StringComparer.Ordinal))
            {
                blockers.Add(gap);
            }
        }
    }

    private static void AddHealthBlockers(
        string name,
        CaptureComparisonVariant variant,
        List<string> blockers)
    {
        if (!variant.Health.IsLossFree || variant.ReplaySourceEventsLost != 0 || variant.ReplayBudgetDrops != 0)
        {
            blockers.Add($"The {name} variant reported loss, a bounded-queue drop, or replay loss.");
        }

        if (!variant.Evidence.Complete)
        {
            blockers.Add($"The {name} evidence artifact is incomplete.");
        }
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
