using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>Every process instance a session's evidence supports, with what each did.</summary>
internal sealed record ProcessesDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string BindingRule { get; init; }
    public required string EvidencePolicy { get; init; }
    public required ProcessesSummaryDocument Summary { get; init; }
    public required IReadOnlyList<ProcessActivityDocument> Instances { get; init; }
    public required IReadOnlyList<ProcessUnattributedDocument> Unattributed { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record ProcessesSummaryDocument
{
    public required int Instances { get; init; }
    public required int ProcessIds { get; init; }
    public required int ReusedProcessIds { get; init; }
    public required IReadOnlyDictionary<string, int> ByWitness { get; init; }
    public required long RecordsAttributed { get; init; }
    public required long RecordsUnattributed { get; init; }
}

/// <summary>
/// One instance and what it did. The byte totals are sender-accounted sent bytes and receiver-accounted received bytes
/// in the transport-observed domain: each is a record the instance itself made, never the other end's measurement.
/// </summary>
internal sealed record ProcessActivityDocument
{
    public required ProcessInstanceDocument Process { get; init; }
    public required long Records { get; init; }
    public required IReadOnlyDictionary<string, long> Bindings { get; init; }
    public required long? TransportBytesSent { get; init; }
    public required long? TransportBytesReceived { get; init; }
}

internal sealed record ProcessUnattributedDocument
{
    public required string Reason { get; init; }
    public required long Records { get; init; }
}

/// <summary>
/// Lists the process instances a published session's evidence supports: how each is witnessed, its lifetime, and how
/// many records and bytes bind to it. It answers from the same grouped metrics `icat metric --group-by process`
/// does, so the two never disagree about a number.
/// </summary>
internal static class ProcessesCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string? topOption = command.TakeOption("--top");
        string? pidOption = command.TakeOption("--pid");
        string? policyOption = command.TakeOption("--evidence-policy");
        string? outputOption = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A session directory is required: icat processes <directory>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        int top = 20;
        if (topOption is not null
            && (!int.TryParse(topOption, NumberStyles.None, CultureInfo.InvariantCulture, out top) || top > 100_000))
        {
            ConsoleUi.Failure($"--top expects a number of instances up to 100,000, or 0 for all; '{topOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        int? pid = null;
        if (pidOption is not null)
        {
            if (!int.TryParse(pidOption, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                ConsoleUi.Failure($"--pid expects a process id; '{pidOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            pid = parsed;
        }

        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated;
        if (policyOption is not null
            && (!Enum.TryParse(policyOption.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out policy)
                || !Enum.IsDefined(policy)))
        {
            ConsoleUi.Failure(
                $"--evidence-policy expects one of: {string.Join(", ", Enum.GetNames<EvidencePolicy>())}. '{policyOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputPath = outputOption is null ? null : Path.GetFullPath(outputOption);
        if (outputPath is not null && File.Exists(outputPath) && !overwrite)
        {
            ConsoleUi.Failure($"{outputPath} exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress($"Acquiring the current generation of {full} and verifying every dependency.");
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        if (store.Current is null)
        {
            ConsoleUi.Failure("This session has published no generation, so it holds no process evidence.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Progress("Deriving process instances and binding every record to one.");
        MetricResult records = Grouped(store, Metric.Observations, null, null, policy, cancellationToken);
        if (!records.IsAvailable)
        {
            ConsoleUi.Failure(records.UnavailableExplanation ?? records.Unavailable.ToString());
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        MetricResult sent = Grouped(store, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide, policy, cancellationToken);
        MetricResult received = Grouped(store, Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide, policy, cancellationToken);
        ProcessesDocument document = Describe(full, records, sent, received, policy);
        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, records.Clock, top, pid);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Process instances written to {outputPath}.");
        }

        return store.Recovery.RolledBackToLastKnownGood ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    private static MetricResult Grouped(
        SessionStore store,
        Metric metric,
        ByteDomain? domain,
        AccountingSide? side,
        EvidencePolicy policy,
        CancellationToken cancellationToken) =>
        SessionMetrics.Evaluate(
            store,
            new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = metric,
                ByteDomain = domain,
                AccountingSide = side,
                Grouping = LaneGrouping.InstanceOnly,
                EvidencePolicy = policy,
            },
            cancellationToken: cancellationToken);

    private static ProcessesDocument Describe(
        string path,
        MetricResult records,
        MetricResult sent,
        MetricResult received,
        EvidencePolicy policy)
    {
        Dictionary<ProcessInstanceId, long?> sentBy = ByInstance(sent);
        Dictionary<ProcessInstanceId, long?> receivedBy = ByInstance(received);
        List<ProcessActivityDocument> instances =
        [
            .. records.Groups
                .Where(group => group.Process is not null)
                .Select(group => new ProcessActivityDocument
                {
                    Process = ProcessInstanceDocument.From(group.Process!, records.Clock),
                    Records = group.Value ?? 0,
                    Bindings = group.Bindings.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
                    TransportBytesSent = sentBy.GetValueOrDefault(group.Process!.Id),
                    TransportBytesReceived = receivedBy.GetValueOrDefault(group.Process!.Id),
                })
                .OrderByDescending(item => (item.TransportBytesSent ?? 0) + (item.TransportBytesReceived ?? 0))
                .ThenByDescending(item => item.Records)
                .ThenBy(item => item.Process.ProcessId)
                .ThenBy(item => item.Process.LifecycleEpoch),
        ];

        var pids = instances.GroupBy(item => item.Process.ProcessId).ToList();
        long unattributed = records.Unattributed.Sum(group => group.Value ?? 0);
        var caveats = new List<string>
        {
            "An instance is identified by host, boot, PID, which of the PID's instances it is, and the record that "
            + "witnesses it - never by the PID alone (R22). One seen only in its own records is provisional: the "
            + "capture holds no lifecycle record of it, so its start is unknown rather than invented.",
            "Sent and received bytes are each instance's own transport records: sender-accounted for what it sent and "
            + "receiver-accounted for what it received. Added across instances they count a local transfer at both "
            + "ends.",
        };
        caveats.AddRange(records.Caveats.Where(caveat => caveat.Contains("process-binding", StringComparison.Ordinal)
            || caveat.Contains("reused", StringComparison.Ordinal)
            || caveat.Contains("no witnessed instance", StringComparison.Ordinal)));

        return new()
        {
            Contract = "entities-v1",
            Path = path,
            Generation = records.Generation,
            BindingRule = records.BindingRule ?? ProcessInstanceIndex.BindingRule,
            EvidencePolicy = policy.ToString(),
            Summary = new()
            {
                Instances = instances.Count,
                ProcessIds = pids.Count,
                ReusedProcessIds = pids.Count(group => group.Count() > 1),
                ByWitness = instances
                    .GroupBy(item => item.Process.Witness)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count()),
                RecordsAttributed = instances.Sum(item => item.Records),
                RecordsUnattributed = unattributed,
            },
            Instances = instances,
            Unattributed =
            [
                .. records.Unattributed.Select(group => new ProcessUnattributedDocument
                {
                    Reason = group.Reason!.Value.ToString(),
                    Records = group.Value ?? 0,
                }),
            ],
            Caveats = caveats,
        };
    }

    /// <summary>
    /// Each instance's value in one grouped answer. A session with nothing measured in the domain answers no groups,
    /// and every instance then has no byte value rather than zero bytes.
    /// </summary>
    private static Dictionary<ProcessInstanceId, long?> ByInstance(MetricResult result)
    {
        var values = new Dictionary<ProcessInstanceId, long?>();
        if (!result.IsAvailable)
        {
            return values;
        }

        foreach (MetricGroup group in result.Groups)
        {
            if (group.Process is { } process)
            {
                values[process.Id] = group.Value;
            }
        }

        return values;
    }

    private static void Render(ProcessesDocument document, SourceClockDescriptor? clock, int top, int? pid)
    {
        ConsoleUi.Heading("Process instances");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Binding rule", $"{document.BindingRule}, evidence policy {document.EvidencePolicy}");
        ConsoleUi.Field(
            "Instances",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.Summary.Instances:N0} across {document.Summary.ProcessIds:N0} PIDs, {document.Summary.ReusedProcessIds:N0} of them reused"));
        ConsoleUi.Field(
            "Witnessed",
            string.Join(
                ", ",
                document.Summary.ByWitness.Select(entry => string.Create(
                    CultureInfo.CurrentCulture,
                    $"{entry.Value:N0} {WitnessWords(entry.Key)}"))));
        long allRecords = document.Summary.RecordsAttributed + document.Summary.RecordsUnattributed;
        ConsoleUi.Field(
            "Records attributed",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.Summary.RecordsAttributed:N0} of {allRecords:N0}"));

        IReadOnlyList<ProcessActivityDocument> shown = pid is { } only
            ? [.. document.Instances.Where(item => item.Process.ProcessId == only)]
            : top == 0 ? document.Instances : [.. document.Instances.Take(top)];
        var reusedPids = document.Instances
            .GroupBy(item => item.Process.ProcessId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        ConsoleUi.Line();
        if (shown.Count == 0)
        {
            ConsoleUi.Line(pid is { } missing
                ? $"  No instance of PID {missing} is in this session's evidence."
                : "  No process instance is in this session's evidence.");
        }
        else
        {
            ConsoleUi.Table(
                ["PID", "Instance", "Records", "Sent (transport)", "Received (transport)"],
                [
                    .. shown.Select(item => new[]
                    {
                        reusedPids.Contains(item.Process.ProcessId)
                            ? string.Create(CultureInfo.InvariantCulture, $"{item.Process.ProcessId} #{item.Process.LifecycleEpoch}")
                            : item.Process.ProcessId.ToString(CultureInfo.InvariantCulture),
                        Lifetime(item.Process, clock),
                        ConsoleUi.Count(item.Records),
                        item.TransportBytesSent is { } sentBytes ? ConsoleUi.Bytes(sentBytes) : "-",
                        item.TransportBytesReceived is { } receivedBytes ? ConsoleUi.Bytes(receivedBytes) : "-",
                    }),
                ]);
        }

        if (pid is null && top != 0 && document.Instances.Count > shown.Count)
        {
            ConsoleUi.Line(string.Create(
                CultureInfo.CurrentCulture,
                $"  ... and {document.Instances.Count - shown.Count:N0} more. --top 0 lists every instance; --json always does."));
        }

        if (document.Unattributed.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Line("  Records not attributed to an instance:");
            ConsoleUi.Table(
                ["Reason", "Records"],
                [
                    .. document.Unattributed.Select(item => new[]
                    {
                        SessionText.Reason(Enum.Parse<ProcessBindingReason>(item.Reason)),
                        ConsoleUi.Count(item.Records),
                    }),
                ]);
        }

        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    private static string Lifetime(ProcessInstanceDocument process, SourceClockDescriptor? clock)
    {
        string start = process.CreatedNativeTicks is { } created
            ? $"created {SessionText.Instant(clock, created)}"
            : process.Witness switch
            {
                nameof(ProcessWitness.Rundown) => "running at capture start",
                nameof(ProcessWitness.ActivityOnly) => "seen only in its own records",
                _ => "running before its first record",
            };
        string end = process.ExitedNativeTicks is { } exited
            ? $", exited {SessionText.Instant(clock, exited)}"
                + (process.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" (code {code})") : string.Empty)
            : process.Gaps.Contains(nameof(ProcessEvidenceGaps.ExitNotWitnessed)) && process.LifetimeEndNativeTicks is { } end2
                ? $", replaced {SessionText.Instant(clock, end2)} with no exit seen"
                : string.Empty;
        return start + end;
    }

    private static string WitnessWords(string witness) => witness switch
    {
        nameof(ProcessWitness.Created) => "created in the capture",
        nameof(ProcessWitness.Rundown) => "running at capture start",
        nameof(ProcessWitness.ExitOnly) => "running before their first record",
        nameof(ProcessWitness.ActivityOnly) => "seen only in their own records",
        _ => witness,
    };

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat processes <directory> [--top <n>] [--pid <id>] [--evidence-policy <name>]");
        ConsoleUi.Line("                 [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("      Lists the process instances the session's evidence supports - created, exited,");
        ConsoleUi.Line("      running at capture start, or seen only in their own records - with each one's");
        ConsoleUi.Line("      lifetime, how many records bind to it, and the transport bytes it sent and");
        ConsoleUi.Line("      received. A PID is never an identity on its own: a reused PID is two instances.");
    }
}
