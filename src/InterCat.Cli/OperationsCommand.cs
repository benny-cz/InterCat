using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>A session's RPC calls, grouped by process, side and interface, with every unpaired call's reason.</summary>
internal sealed record OperationsDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required long Generation { get; init; }
    public required string OperationRule { get; init; }
    public required string BindingRule { get; init; }

    /// <summary>The rule that found each client call's other end (`contracts/operations-v1.md` §5c).</summary>
    public required string PeerRule { get; init; }

    public required string EvidencePolicy { get; init; }
    public required long CallRecords { get; init; }
    public required long OtherRpcRecords { get; init; }

    /// <summary>The ALPC sends and receives the other ends were followed through; none when the capture collected none.</summary>
    public required long AlpcRecords { get; init; }

    public required RpcCallCounts Totals { get; init; }
    public required IReadOnlyList<OperationsGroupDocument> Groups { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record OperationsGroupDocument
{
    public required int ProcessId { get; init; }

    /// <summary>The instance the group's calls bind to, when the evidence policy admits it; null otherwise.</summary>
    public required ProcessInstanceDocument? Process { get; init; }

    /// <summary>Why no instance holds the group's calls under the evidence policy; null when one does.</summary>
    public required string? Unattributed { get; init; }

    public required string Side { get; init; }
    public required Guid? Interface { get; init; }
    public required RpcCallCounts Counts { get; init; }
    public required RpcCallDurations? DurationsNanoseconds { get; init; }

    /// <summary>
    /// Who is at the other end of the group's calls: for a client group, the server calls that served it, and why the
    /// rest are unresolved; for a server group, the client calls it served.
    /// </summary>
    public required OperationsOtherEndsDocument OtherEnds { get; init; }

    /// <summary>The group's first calls in reading order, when calls were asked for; null otherwise.</summary>
    public IReadOnlyList<OperationsCallDocument>? Calls { get; init; }
}

internal sealed record OperationsOtherEndsDocument
{
    /// <summary>Calls whose other end is resolved: served client calls, or server calls a client call reached.</summary>
    public required long Linked { get; init; }

    /// <summary>A client group's calls whose other end is unresolved, by reason; empty for a server group.</summary>
    public required IReadOnlyDictionary<RpcPeerState, long> Unresolved { get; init; }

    /// <summary>The calls at the other end, by process and interface, the most first.</summary>
    public required IReadOnlyList<OperationsPeerDocument> Peers { get; init; }
}

internal sealed record OperationsPeerDocument
{
    public required int ProcessId { get; init; }
    public required ProcessInstanceDocument? Process { get; init; }
    public required string? Unattributed { get; init; }
    public required Guid? Interface { get; init; }
    public required long Calls { get; init; }
}

internal sealed record OperationsCallDocument
{
    public required ObservationId Identity { get; init; }
    public required string State { get; init; }
    public required Guid? ActivityId { get; init; }
    public required long? Procedure { get; init; }
    public required long? Protocol { get; init; }
    public required long? Status { get; init; }
    public required long? DurationNanoseconds { get; init; }
    public required ObservationId? Start { get; init; }
    public required string? StartSeconds { get; init; }
    public required ObservationId? Stop { get; init; }
    public required string? StopSeconds { get; init; }

    /// <summary>A client call's other end, or why it is unresolved; null for a server call no client call reached.</summary>
    public required string? OtherEndState { get; init; }

    /// <summary>The call at the other end, when one is linked.</summary>
    public required ObservationId? OtherEnd { get; init; }

    public required int? OtherEndProcessId { get; init; }

    public required string? OtherEndImage { get; init; }
}

/// <summary>
/// Lists a session's RPC calls: each start paired with its stop through the activity id, on one side of one process
/// (`contracts/operations-v1.md`). Every call that could not be paired is counted by its reason, so the calls add up to
/// the call records read.
/// </summary>
internal static class OperationsCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string? pidOption = command.TakeOption("--pid");
        string? interfaceOption = command.TakeOption("--interface");
        string? callsOption = command.TakeOption("--calls");
        string? topOption = command.TakeOption("--top");
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
            ConsoleUi.Failure("A session directory is required: icat operations <directory>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
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

        Guid? rpcInterface = null;
        if (interfaceOption is not null)
        {
            if (!Guid.TryParse(interfaceOption, out Guid parsed))
            {
                ConsoleUi.Failure($"--interface expects an RPC interface UUID; '{interfaceOption}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            rpcInterface = parsed;
        }

        int calls = pid is null && rpcInterface is null ? 0 : 20;
        if (callsOption is not null
            && (!int.TryParse(callsOption, NumberStyles.None, CultureInfo.InvariantCulture, out calls) || calls > 10_000))
        {
            ConsoleUi.Failure($"--calls expects a number of calls per group up to 10,000; '{callsOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        int top = 30;
        if (topOption is not null
            && (!int.TryParse(topOption, NumberStyles.None, CultureInfo.InvariantCulture, out top) || top > 100_000))
        {
            ConsoleUi.Failure($"--top expects a number of groups up to 100,000, or 0 for all; '{topOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
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
            ConsoleUi.Failure("This session has published no generation, so it holds no calls.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        OperationsDocument document;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            if (SessionSegments.SourceClock(store.Root, manifest) is not { } clock)
            {
                ConsoleUi.Failure("The generation names no source clock, so no call has a process or a duration.");
                return InterCatExitCode.PermissionOrCapabilityFailure;
            }

            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
            ConsoleUi.Progress(
                "Deriving process instances, pairing every RPC call record by its activity id, and following each client "
                + "call's ALPC message to the call that served it.");
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields, cancellationToken);
            RpcCallIndex index = RpcCallIndex.Derive(segments, fields, processes, clock, cancellationToken);
            RpcPeerIndex peers = RpcPeerIndex.Derive(index, segments, fields, cancellationToken);
            document = Describe(full, manifest.Generation, index, peers, segments, clock, policy, pid, rpcInterface, calls);
        }

        string payload = JsonSerializer.Serialize(document, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(document, top);
        }

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"RPC calls written to {outputPath}.");
        }

        return store.Recovery.RolledBackToLastKnownGood ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success;
    }

    private static OperationsDocument Describe(
        string path,
        long generation,
        RpcCallIndex index,
        RpcPeerIndex peers,
        SegmentReaderV1[] segments,
        SourceClockDescriptor clock,
        EvidencePolicy policy,
        int? pid,
        Guid? rpcInterface,
        int calls)
    {
        List<OperationsGroupDocument> groups = [];
        foreach (RpcCallGroup group in index.Groups)
        {
            if ((pid is { } only && group.ProcessId != only) || (rpcInterface is { } wanted && group.Interface != wanted))
            {
                continue;
            }

            bool admitted = group.Process.IsAdmittedUnder(policy);
            RpcPeerCounts counts = peers.CountsOf(group);
            IReadOnlyList<RpcPeerTally> tallies = peers.PeersOf(group);
            groups.Add(new()
            {
                ProcessId = group.ProcessId,
                Process = admitted ? ProcessInstanceDocument.From(index.Processes.Instances[group.Process.Instance], clock) : null,
                Unattributed = admitted
                    ? null
                    : group.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy.ToString() : group.Process.Reason.ToString(),
                Side = group.Side.ToString(),
                Interface = group.Interface,
                Counts = group.Counts,
                DurationsNanoseconds = group.Durations,
                OtherEnds = new()
                {
                    Linked = group.Side == RpcCallSide.Client ? counts.Served : tallies.Sum(tally => tally.Calls),
                    Unresolved = counts.Unresolved,
                    Peers = [.. tallies.Select(tally => Peer(tally, index, clock, policy))],
                },
                Calls = calls == 0
                    ? null
                    : [.. index.CallsOf(group, segments, 0, calls).Select((call, position) =>
                        Call(call, peers.PeerOf(group, position, segments), index, clock, policy))],
            });
        }

        return new()
        {
            Contract = "operations-v1",
            Path = path,
            Generation = generation,
            OperationRule = RpcCallIndex.OperationRule,
            BindingRule = ProcessInstanceIndex.BindingRule,
            PeerRule = RpcPeerIndex.PeerRule,
            EvidencePolicy = policy.ToString(),
            CallRecords = index.CallRecords,
            OtherRpcRecords = index.OtherRpcRecords,
            AlpcRecords = peers.AlpcSends + peers.AlpcReceives,
            Totals = index.Totals,
            Groups = groups,
            Caveats =
            [
                "A call is its start and the stop that carries the same activity id, on one side of one process; nothing "
                + "is paired by time (P8). A start with no stop is open at capture end, which is not a failure.",
                "A client call and the server call that served it carry different activity ids (ADR-031). Its other end is "
                + "found only through the one ALPC message the call sent, when the capture collected ALPC "
                + "(rpc-call-peer-v1, ADR-034); otherwise it is stated unresolved, with its reason, never guessed (P7).",
                "An RPC record names no process; it belongs to the process that raised it (process-binding-v3, ADR-030).",
                "A duration is the session time between a call's start and stop records on one clock. Percentiles are "
                + "durations calls took, by nearest rank.",
            ],
        };
    }

    /// <summary>The calls at a group's other end in one process, named as the group's own process is.</summary>
    private static OperationsPeerDocument Peer(
        RpcPeerTally tally,
        RpcCallIndex index,
        SourceClockDescriptor clock,
        EvidencePolicy policy)
    {
        bool admitted = tally.Process.IsAdmittedUnder(policy);
        return new()
        {
            ProcessId = tally.ProcessId,
            Process = admitted ? ProcessInstanceDocument.From(index.Processes.Instances[tally.Process.Instance], clock) : null,
            Unattributed = admitted
                ? null
                : tally.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy.ToString() : tally.Process.Reason.ToString(),
            Interface = tally.Interface,
            Calls = tally.Calls,
        };
    }

    private static OperationsCallDocument Call(
        RpcCall call,
        (RpcPeerState State, RpcCall? Other) peer,
        RpcCallIndex index,
        SourceClockDescriptor clock,
        EvidencePolicy policy) => Call(call, clock) with
    {
        OtherEndState = peer.State == default ? null : peer.State.ToString(),
        OtherEnd = peer.Other?.Identity,
        OtherEndProcessId = peer.Other?.ProcessId,
        OtherEndImage = peer.Other is { Process: var binding } && binding.IsAdmittedUnder(policy)
            ? index.Processes.Instances[binding.Instance].ImageName
            : null,
    };

    private static OperationsCallDocument Call(RpcCall call, SourceClockDescriptor clock) => new()
    {
        OtherEndState = null,
        OtherEnd = null,
        OtherEndProcessId = null,
        OtherEndImage = null,
        Identity = call.Identity,
        State = call.State.ToString(),
        ActivityId = call.ActivityId,
        Procedure = call.Procedure,
        Protocol = call.Protocol,
        Status = call.Status,
        DurationNanoseconds = call.DurationNanoseconds,
        Start = call.Start?.Observation,
        StartSeconds = call.Start is { } start ? SessionText.Seconds(clock, start.NativeTicks) : null,
        Stop = call.Stop?.Observation,
        StopSeconds = call.Stop is { } stop ? SessionText.Seconds(clock, stop.NativeTicks) : null,
    };

    private static void Render(OperationsDocument document, int top)
    {
        ConsoleUi.Heading("RPC calls");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(document.Generation));
        ConsoleUi.Field("Rules", $"{document.OperationRule} over {document.BindingRule}, evidence policy {document.EvidencePolicy}");
        long clientCalls = document.Groups.Where(group => group.Side == "Client").Sum(group => group.Counts.Calls);
        long served = document.Groups.Where(group => group.Side == "Client").Sum(group => group.OtherEnds.Linked);
        ConsoleUi.Field(
            "Other ends",
            document.AlpcRecords == 0
                ? $"{document.PeerRule}: none resolved, the capture collected no ALPC (icat record --profile rpc-peers does)"
                : string.Create(CultureInfo.CurrentCulture,
                    $"{document.PeerRule}, through {document.AlpcRecords:N0} ALPC sends and receives: {served:N0} of the {clientCalls:N0} client calls listed were served"));
        ConsoleUi.Field(
            "Call records",
            document.OtherRpcRecords == 0
                ? ConsoleUi.Count(document.CallRecords)
                : string.Create(CultureInfo.CurrentCulture,
                    $"{document.CallRecords:N0}, and {document.OtherRpcRecords:N0} other RPC records that are no call"));
        RpcCallCounts totals = document.Totals;
        ConsoleUi.Field("Calls", string.Create(CultureInfo.CurrentCulture,
            $"{totals.Calls:N0}: {totals.Completed:N0} completed ({totals.Failed:N0} failed), {totals.OpenAtCaptureEnd:N0} open at capture end"));
        ConsoleUi.Field("Not paired", string.Create(CultureInfo.CurrentCulture,
            $"{totals.StartNotObserved:N0} without their start, {totals.NoActivityId:N0} without an activity id, {totals.Ambiguous:N0} ambiguous"));
        ConsoleUi.Line();
        if (document.Groups.Count == 0)
        {
            ConsoleUi.Note(document.CallRecords == 0
                ? "This session holds no RPC call record: its capture did not include the RPC source."
                : "No group matches the process or interface asked for.");
        }
        else
        {
            IReadOnlyList<OperationsGroupDocument> shown = top == 0 ? document.Groups : [.. document.Groups.Take(top)];
            ConsoleUi.Table(
                ["Process", "Side", "Interface", "Calls", "Done", "Failed", "Open", "Unpaired", "Median", "p95", "Max", "Other end"],
                [.. shown.Select(group => (IReadOnlyList<string>)
                [
                    ProcessName(group),
                    group.Side.ToLowerInvariant(),
                    group.Interface?.ToString() ?? "(no start seen)",
                    ConsoleUi.Count(group.Counts.Calls),
                    ConsoleUi.Count(group.Counts.Completed),
                    ConsoleUi.Count(group.Counts.Failed),
                    ConsoleUi.Count(group.Counts.OpenAtCaptureEnd),
                    ConsoleUi.Count(group.Counts.StartNotObserved + group.Counts.NoActivityId + group.Counts.Ambiguous),
                    Duration(group.DurationsNanoseconds?.Median),
                    Duration(group.DurationsNanoseconds?.Percentile95),
                    Duration(group.DurationsNanoseconds?.Maximum),
                    OtherEnd(group),
                ])]);
            if (shown.Count < document.Groups.Count)
            {
                ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                    $"{document.Groups.Count - shown.Count:N0} more groups; --top 0 lists every one."));
            }

            foreach (OperationsGroupDocument group in shown.Where(group => group.Calls is { Count: > 0 }))
            {
                ConsoleUi.Line();
                ConsoleUi.Line(string.Create(CultureInfo.CurrentCulture,
                    $"  {ProcessName(group)}, {group.Side.ToLowerInvariant()} calls to {group.Interface?.ToString() ?? "an interface no start named"}: the first {group.Calls!.Count:N0} of {group.Counts.Calls:N0}"));
                ConsoleUi.Table(
                    ["Start", "Duration", "Status", "State", "Procedure", "Other end"],
                    [.. group.Calls.Select(call => (IReadOnlyList<string>)
                    [
                        call.StartSeconds is { } seconds ? LocalSeconds(seconds) : "not observed",
                        Duration(call.DurationNanoseconds),
                        call.Status?.ToString(CultureInfo.CurrentCulture) ?? "none",
                        OperationText.State(Enum.Parse<RpcCallState>(call.State)),
                        call.Procedure?.ToString(CultureInfo.CurrentCulture) ?? "none",
                        OtherEnd(call),
                    ])]);
            }
        }

        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    /// <summary>
    /// A group's other end in a cell: for a client group, its most frequent server and how many of its calls were served;
    /// for a server group, how many client processes it served.
    /// </summary>
    private static string OtherEnd(OperationsGroupDocument group)
    {
        OperationsOtherEndsDocument ends = group.OtherEnds;
        if (group.Side != "Client")
        {
            return ends.Peers.Count == 0
                ? "-"
                : string.Create(CultureInfo.CurrentCulture,
                    $"{ends.Linked:N0} for {ends.Peers.Count:N0} client {(ends.Peers.Count == 1 ? "process" : "processes")}");
        }

        if (ends.Peers.Count == 0)
        {
            return ends.Unresolved.Count == 1 && ends.Unresolved.ContainsKey(RpcPeerState.NoAlpcEvidence) ? "no ALPC" : "unresolved";
        }

        OperationsPeerDocument first = ends.Peers[0];
        string name = first.Process?.ImageName ?? $"PID {first.ProcessId}";
        string more = ends.Peers.Count > 1 ? string.Create(CultureInfo.CurrentCulture, $" +{ends.Peers.Count - 1:N0}") : "";
        return string.Create(CultureInfo.CurrentCulture, $"{name}{more}: {ends.Linked:N0}/{group.Counts.Calls:N0}");
    }

    /// <summary>One call's other end in a cell: the process that served or called it, or why none is known.</summary>
    private static string OtherEnd(OperationsCallDocument call) => call switch
    {
        { OtherEndProcessId: { } other } => string.Create(CultureInfo.CurrentCulture, $"{call.OtherEndImage ?? "PID"} · {other}"),
        { OtherEndState: { } state } => OperationText.PeerState(Enum.Parse<RpcPeerState>(state)),
        _ => "-",
    };

    private static string ProcessName(OperationsGroupDocument group) => group.Process is { } process
        ? string.Create(CultureInfo.CurrentCulture, $"{process.ImageName ?? "executable not witnessed"} · {process.ProcessId}")
        : string.Create(CultureInfo.CurrentCulture, $"PID {group.ProcessId} · {group.Unattributed}");

    /// <summary>A session time the document states invariantly, written as the rest of the table is.</summary>
    private static string LocalSeconds(string seconds) => string.Create(CultureInfo.CurrentCulture,
        $"{decimal.Parse(seconds, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture):0.000000} s");

    private static string Duration(long? nanoseconds) => nanoseconds is { } value
        ? OperationText.Duration(value, CultureInfo.CurrentCulture)
        : "-";

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat operations <directory> [--pid <id>] [--interface <uuid>] [--calls <n>] [--top <n>]");
        ConsoleUi.Line("                [--evidence-policy <name>] [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Lists the session's RPC calls: each start paired with its stop through the activity id, on one");
        ConsoleUi.Line("  side of one process (contracts/operations-v1.md). Calls are grouped by process, side and");
        ConsoleUi.Line("  interface, with completions, failures, open calls and durations, and every call that could");
        ConsoleUi.Line("  not be paired is counted by its reason. When the capture collected ALPC (--profile rpc-peers),");
        ConsoleUi.Line("  each client call's other end is the server call its one ALPC message reached (ADR-034).");
        ConsoleUi.Line();
        ConsoleUi.Line("  --pid <id>             Only the groups of this PID, with their first calls.");
        ConsoleUi.Line("  --interface <uuid>     Only the groups of this RPC interface, with their first calls.");
        ConsoleUi.Line("  --calls <n>            How many calls to list per group (default 20 with --pid or --interface).");
        ConsoleUi.Line("  --top <n>              How many groups to show (default 30; 0 shows every one).");
        ConsoleUi.Line("  --evidence-policy      Which bindings name a process: DirectOnly, IncludeCorrelated (default),");
        ConsoleUi.Line("                         IncludeCandidates or AllIncludingConflicting.");
    }
}
