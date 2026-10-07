using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// Answers on the logical-operations basis (`contracts/metrics-v1.md` §8a). An operation is a call `rpc-call-operation-v1`
/// derives, the only operation this version has. A count takes the calls whose counted record - a start for a started
/// count, a stop for a completed or failed one - is in scope, and states every other call in scope by what its records
/// establish, never guessing one into the number or quietly out of it.
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>
    /// What a logical-operations request needs that this version's operations do not have, or null when the calls answer
    /// it. The matrix accepts every one of these requests; each means something no derived operation can say yet.
    /// </summary>
    private static MetricResult? WhatOperationsNeed(MetricRequest request, long generation)
    {
        Metric effective = request.Metric == Metric.Rate ? request.RateNumerator!.Value : request.Metric;
        if (effective is Metric.BytesSent or Metric.BytesReceived or Metric.EndpointActivityBytes or Metric.RequestedIoBytes
            or Metric.ApplicationPayloadBytes or Metric.CapturedContentBytes)
        {
            return Unavailable(
                request,
                generation,
                MetricUnavailableReason.NothingMeasured,
                $"No operation this version derives measures bytes: an RPC call's records carry no length "
                + $"(operations-v1 §2), so there is no {request.ByteDomain} contribution to sum, and a sum of nothing is "
                + "not an observed zero (R21, P1). Byte totals are answered on the source-observations basis, from the "
                + "records that measure them.");
        }

        string? missing = effective switch
        {
            Metric.Duration when request.DurationInterval is not (DurationInterval.ClientCall or DurationInterval.ServerExecution) =>
                $"No {request.DurationInterval} operation is derived at this version. RPC calls, the only operations, give a "
                + "client call's duration and a server execution's (operations-v1); no other interval is answered from "
                + "operations of another name (§5).",
            Metric.ActiveChannels =>
                "ActiveChannels counts connection incarnations (metrics-v1 §6.1). An RPC channel - one process's calls on "
                + "one side to one interface (operations-v1 §5a) - is another thing, and counting it as a channel is not "
                + "defined at this version; icat operations lists them.",
            Metric.ActivePeers when request.Focus is null =>
                "A peer is the process at a call's other end, counted from one process: name it with owner(P) or "
                + "participant(P) (§6.1).",
            Metric.ActivePeers when request.Grouping is not null =>
                "Counting each group's peers of calls is not defined at this version. Count one process's peers with a "
                + "focus, or group its calls by peer, which lists each peer with its calls (§8a).",
            Metric.Errors when request.AccountingSide is not null =>
                "An RPC call is made at its client and served at its server, and neither is the end a transfer was sent "
                + "or received at, so no call is accounted to a side. Ask for Errors without one.",
            _ => null,
        };

        // A call is made at a client and served at a server: it carries no data direction for a role or a direction to
        // select it by, where its other end, linked through ALPC, answers participant(P), peer(P,Q) and between(A,B).
        string? directed = request.Focus is { Role: ProcessRole.Sender or ProcessRole.Receiver } focus ? $"{Role(focus.Role)}(P)"
            : request.Between is { Direction: not BetweenDirection.Either } ? "A directional between(A,B)"
            : null;
        missing ??= directed is null
            ? null
            : $"{directed} selects data that flowed one way, and a call is made at a client and served at a server with no "
                + "data direction, so it is not defined for calls. participant(P) keeps the calls P made and those made to "
                + "or by it, and between(A,B) either way keeps the calls joining the sets (§8a).";
        missing ??= request.Grouping == LaneGrouping.Peer && request.Focus is null
            ? "Grouping calls by peer reads each call's other end from one process: name it with owner(P) or "
                + "participant(P) (§6)."
            : null;

        missing ??= request.Mechanism is { } mechanism && mechanism != Mechanism.Rpc
            ? $"Only RPC calls are derived as operations at this version (operations-v1). No correlator derives {mechanism} "
                + "operations, so there is nothing to count, which is not a count of zero (R21)."
            : request.Layer is { } layer && layer != ObservationLayer.Application
                ? "An RPC call is an application-layer operation, with whatever carried it beneath it (§5.1). No "
                    + $"{layer}-layer operation is derived at this version, so there is nothing to count, which is not a "
                    + "count of zero (R21)."
                : null;
        return missing is null
            ? null
            : Unavailable(request, generation, MetricUnavailableReason.NoLogicalOperations, missing);
    }

    private static string Role(ProcessRole role) => role switch
    {
        ProcessRole.Participant => "participant",
        ProcessRole.Sender => "sender",
        ProcessRole.Receiver => "receiver",
        _ => "owner",
    };

    /// <summary>Counts the calls a logical-operations request takes, ungrouped or grouped (`metrics-v1` §8a).</summary>
    private static MetricResult Operations(Context context, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        if (context.Clock is not { } clock)
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.NoEntityBindings,
                "An RPC call is joined within one process, and a process instance is identified within the host and boot "
                + "its capture's clock scopes. This session does not describe its clock, so no call can be derived "
                + "(identity-v1).");
        }

        SegmentReaderV1[] readers = [.. context.Segments.Select(segment => segment.Reader)];
        ProcessInstanceIndex processes = ProcessesOf(context, cancellationToken);
        int owner = request.Owner is { } owned ? IndexOf(processes, owned) : -1;
        ProcessInstanceId[] named =
        [
            .. request.Focus is { } focused ? [focused.Instance] : Array.Empty<ProcessInstanceId>(),
            .. request.Peer is { } narrowed ? [narrowed] : Array.Empty<ProcessInstanceId>(),
            .. request.Between?.First ?? [],
            .. request.Between?.Second ?? [],
        ];
        int absent = Array.FindIndex(named, instance => IndexOf(processes, instance) < 0);
        if (absent >= 0)
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.ProcessInstanceNotFound,
                $"Process instance {named[absent]} is not present in generation {context.Generation}. Select an instance id "
                + "from this generation's process grouping; a PID alone is not an instance identity.");
        }

        if (request.Grouping is not null && WhatGroupingNeeds(request, context.Generation, clock) is { } ungroupable)
        {
            return ungroupable;
        }

        if (request.Grouping == LaneGrouping.Executable
            && !processes.Instances.Any(instance => !string.IsNullOrWhiteSpace(instance.ImagePath)))
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.GroupingNotDerived,
                "No process lifecycle record in this generation carries a full image name. An executable cannot be "
                + "identified from a PID or an exit basename; import evidence with admitted process image names.");
        }

        RpcCallIndex calls = context.Derived is { } derived && ReferenceEquals(processes, derived.Processes)
            ? derived.Calls(cancellationToken)
            : RpcCallIndex.Derive(readers, context.FieldSegments, processes, clock, cancellationToken);

        // A filter, grouping or count that reads a call's other end rests on rpc-call-peer-v1's links (§8a).
        CallEnds? ends = null;
        if (NeedsOtherEnds(request))
        {
            RpcPeerIndex peers = context.Derived is { Peers: { } followed } given && ReferenceEquals(processes, given.Processes)
                ? followed(cancellationToken)
                : RpcPeerIndex.Derive(calls, readers, context.FieldSegments, cancellationToken);
            if (!peers.CollectedAlpc)
            {
                return Unavailable(request, context.Generation, MetricUnavailableReason.NoLogicalOperations,
                    $"{OtherEndsAskedFor(request)} needs the process at a call's other end, and this generation holds no "
                    + "ALPC record to link a call to the call that served it (operations-v1 §5c). A capture with the RPC "
                    + "peers profile collects ALPC: icat record --profile rpc-peers.");
            }

            ends = CallEnds.Of(calls, peers, request, processes);
        }

        if (request.Metric == Metric.Duration)
        {
            return Durations(context, calls, processes, clock, owner, ends, cancellationToken);
        }

        Metric effective = request.Metric == Metric.Rate ? request.RateNumerator!.Value : request.Metric;
        if (effective == Metric.ActivePeers)
        {
            return CallPeers(context, calls, ends!, cancellationToken);
        }

        var tally = new OperationTally(effective, processes, request.EvidencePolicy, request.Grouping);
        var evidence = new List<RpcCallMark>();
        var disclosed = new Dictionary<ProcessBindingReason, long>();
        var unlinked = new Dictionary<RpcPeerState, long>();
        int index = -1;
        foreach (RpcCallOutcome call in calls.Outcomes())
        {
            index++;
            RpcCallMark? mark = tally.CountsByStart ? call.Start : call.Stop;
            if (mark is not { } counted)
            {
                continue;
            }

            if (request.Interval is { } interval && !interval.Contains(counted.NativeTicks))
            {
                tally.OutsideInterval++;
                continue;
            }

            if (ends?.Kept is { } kept)
            {
                if (!kept[index])
                {
                    tally.OutsideProcess++;
                    if (ends.Undecided![index])
                    {
                        Disclose(ends, index, disclosed, unlinked);
                    }

                    continue;
                }
            }
            else if (owner >= 0 && !(call.Process.Instance == owner && call.Process.IsAdmittedUnder(request.EvidencePolicy)))
            {
                tally.OutsideProcess++;
                continue;
            }

            // Grouped by peer, a call belongs to the process at its other end from the focus (§6).
            ProcessBinding? counterpart = request.Grouping == LaneGrouping.Peer ? ends!.Counterpart(index) : null;
            if (counterpart is { IsBound: false } && ends!.Unlinked(index) is { } state)
            {
                unlinked[state] = unlinked.GetValueOrDefault(state) + 1;
            }

            if (tally.Add(call, counterpart) && context.EvidenceLimit > 0)
            {
                evidence.Add(counted);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        MetricResult answer = context.Answer(request) with
        {
            Unit = MeasurementUnit.Count,
            KnownContributions = tally.Known,
            UnknownContributions = tally.Unknown,
            ExcludedByProcessFilter = tally.OutsideProcess,
            ExcludedOutsideInterval = tally.OutsideInterval,
            UnresolvedCounterparts = disclosed,
            UnlinkedCalls = unlinked,
            BindingRule = ProcessInstanceIndex.BindingRule,
            OperationRule = RpcCallIndex.OperationRule,
            RelationRule = ends is null ? null : RpcPeerIndex.PeerRule,
            Operations = new()
            {
                CountedRecord = tally.CountsByStart ? ObservationKind.RequestStart : ObservationKind.RequestEnd,
                InScope = tally.InScope,
                UnpairedFailures = tally.UnpairedFailures,
                Calls = calls.Totals.Calls,
            },
            Evidence =
            [
                .. evidence
                    .OrderBy(mark => mark.Segment)
                    .ThenBy(mark => mark.Row)
                    .Take(context.EvidenceLimit)
                    .Select(mark => Evidence(context.Segments[mark.Segment].Name, context.Segments[mark.Segment].Reader, mark.Row)),
            ],
        };

        // An error count over calls none of which says how it ended is not a zero: it would report every one of them as
        // having succeeded (R3, R21).
        if (effective == Metric.Errors && tally.Known == 0 && tally.Unknown > 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation =
                    $"All {tally.Unknown:N0} completed calls in scope carried no status, so none is known to have failed or "
                    + "succeeded. They are counted as unknown and never as zero (R3).",
                Unit = null,
            };
        }

        var caveats = new List<string> { OperationCaveat(request) };
        caveats.AddRange(OperationCountCaveats(effective, tally));
        if (request.Focus is { Role: ProcessRole.Owner } focus)
        {
            caveats.Add(
                $"Only calls bound to owner instance {focus.Instance} under {request.EvidencePolicy} contribute: a call binds "
                + "by its first record's reading to the process that raised it (ADR-030). A call of another instance, or a "
                + "binding this policy excludes, is outside this filter.");
        }

        if (ends is not null)
        {
            caveats.Add(OtherEndCaveat(disclosed.Values.Sum()));
        }

        if (tally.InScope.Count == 0)
        {
            caveats.Add(
                "No call is in scope. That is a count of derived calls, not a finding that no call happened: coverage is "
                + "stated separately from data (R21).");
        }

        long value = tally.Value;
        if (request.Grouping is not null)
        {
            (IReadOnlyList<MetricGroup> ordered, MetricGroup? remainder, IReadOnlyList<MetricGroup> unattributed) =
                tally.Groups(request.RequestedRows, context);
            caveats.AddRange(OperationGroupingCaveats(request, unattributed));
            answer = answer with
            {
                Groups = ordered,
                Remainder = remainder,
                Unattributed = unattributed,
                GroupsPartitionTotal = true,
            };
        }

        if (request.Metric != Metric.Rate)
        {
            return answer with { Value = value, Caveats = caveats };
        }

        MetricRate rate = RateOf(value, MeasurementUnit.Count, context)!;
        caveats.Add(
            "The denominator is the whole selected interval"
            + (request.Grouping is null ? string.Empty : ", the same for every group")
            + ". A rate is never divided by a shorter healthy-looking part of it (§19.2).");
        caveats.Add(
            "The numerator counts the calls that were observed. This is an observed rate and not a corrected one, even "
            + "when the capture ledger reports a gap; no missing call or healthy-time denominator is invented.");
        return answer with { Value = null, Unit = rate.Unit, Rate = rate, Caveats = caveats };
    }

    /// <summary>
    /// A duration over one named interval's calls and one cohort (`metrics-v1` §8a): the calls of the interval's side whose
    /// counted record - the stop for the calls completed in range, the start for those started in range - is in scope, each
    /// measured when both its start and its stop are in the evidence, and stated by what its records establish when not.
    /// </summary>
    private static MetricResult Durations(
        Context context,
        RpcCallIndex calls,
        ProcessInstanceIndex processes,
        SourceClockDescriptor clock,
        int owner,
        CallEnds? ends,
        CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        RpcCallSide side = request.DurationInterval == DurationInterval.ClientCall ? RpcCallSide.Client : RpcCallSide.Server;
        bool byStart = request.Cohort == OperationCohort.StartedInRange;
        DurationStatistic statistic = request.Statistic!.Value;
        var tally = new OperationTally(Metric.Duration, processes, request.EvidencePolicy, request.Grouping, byStart, statistic);
        long otherInterval = 0;
        var evidence = new List<RpcCallMark>();
        var disclosed = new Dictionary<ProcessBindingReason, long>();
        var unlinked = new Dictionary<RpcPeerState, long>();
        int index = -1;
        foreach (RpcCallOutcome call in calls.Outcomes())
        {
            index++;
            RpcCallMark? mark = byStart ? call.Start : call.Stop;
            if (mark is not { } counted)
            {
                continue;
            }

            // The interval is read first, then the named interval's side as a projection, then the process filter, so the
            // three exclusions never overlap (metrics-v1 §5).
            if (request.Interval is { } interval && !interval.Contains(counted.NativeTicks))
            {
                tally.OutsideInterval++;
                continue;
            }

            if (call.Side != side)
            {
                otherInterval++;
                continue;
            }

            if (ends?.Kept is { } kept)
            {
                if (!kept[index])
                {
                    tally.OutsideProcess++;
                    if (ends.Undecided![index])
                    {
                        Disclose(ends, index, disclosed, unlinked);
                    }

                    continue;
                }
            }
            else if (owner >= 0 && !(call.Process.Instance == owner && call.Process.IsAdmittedUnder(request.EvidencePolicy)))
            {
                tally.OutsideProcess++;
                continue;
            }

            ProcessBinding? counterpart = request.Grouping == LaneGrouping.Peer ? ends!.Counterpart(index) : null;
            if (counterpart is { IsBound: false } && ends!.Unlinked(index) is { } state)
            {
                unlinked[state] = unlinked.GetValueOrDefault(state) + 1;
            }

            (long Start, long Stop)? span = call.State == RpcCallState.Completed
                ? ((long)SourceClockMath.SessionNanoseconds(clock, call.Start!.Value.NativeTicks),
                    (long)SourceClockMath.SessionNanoseconds(clock, call.Stop!.Value.NativeTicks))
                : null;
            if (tally.AddTimed(call, span, counterpart) && context.EvidenceLimit > 0)
            {
                evidence.Add(counted);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        MetricDistribution? distribution = tally.Distribution();
        MetricResult answer = context.Answer(request) with
        {
            Unit = MeasurementUnit.Nanoseconds,
            KnownContributions = tally.Known,
            UnknownContributions = tally.Unknown,
            ExcludedByProjection = otherInterval,
            ExcludedByProcessFilter = tally.OutsideProcess,
            ExcludedOutsideInterval = tally.OutsideInterval,
            UnresolvedCounterparts = disclosed,
            UnlinkedCalls = unlinked,
            BindingRule = ProcessInstanceIndex.BindingRule,
            OperationRule = RpcCallIndex.OperationRule,
            RelationRule = ends is null ? null : RpcPeerIndex.PeerRule,
            Operations = new()
            {
                CountedRecord = byStart ? ObservationKind.RequestStart : ObservationKind.RequestEnd,
                InScope = tally.InScope,
                Calls = calls.Totals.Calls,
            },
            Distribution = distribution,
            Evidence =
            [
                .. evidence
                    .OrderBy(mark => mark.Segment)
                    .ThenBy(mark => mark.Row)
                    .Take(context.EvidenceLimit)
                    .Select(mark => Evidence(context.Segments[mark.Segment].Name, context.Segments[mark.Segment].Reader, mark.Row)),
            ],
        };

        List<string> caveats = [OperationCaveat(request), .. DurationCaveats(request, tally, distribution, otherInterval)];
        if (request.Focus is { Role: ProcessRole.Owner } focus)
        {
            caveats.Add(
                $"Only calls bound to owner instance {focus.Instance} under {request.EvidencePolicy} contribute: a call binds "
                + "by its first record's reading to the process that raised it (ADR-030).");
        }

        if (ends is not null)
        {
            caveats.Add(OtherEndCaveat(disclosed.Values.Sum()));
        }

        if (request.Grouping is not null)
        {
            (IReadOnlyList<MetricGroup> ordered, MetricGroup? remainder, IReadOnlyList<MetricGroup> unattributed) =
                tally.Groups(request.RequestedRows, context);
            caveats.AddRange(OperationGroupingCaveats(request, unattributed));
            caveats.Add(
                $"Groups are ranked by their {Words(statistic)}, the slowest first. Each call is in exactly one group, but a "
                + "median or a percentile does not add, so the groups' values are not parts of the total's.");
            answer = answer with { Groups = ordered, Remainder = remainder, Unattributed = unattributed };
        }

        // A duration of nothing is not zero: with no call in scope measured, there is no value to report (R21).
        if (distribution is null)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = tally.Unknown > 0
                    ? string.Create(CultureInfo.CurrentCulture, $"None of the {tally.Unknown:N0} ")
                        + $"{Words(side)} calls in scope has both its start and its stop in the evidence, so no duration "
                        + "was measured; they are stated by what their records establish, never given a duration (R3)."
                    : $"No {Words(side)} call is in scope, so no duration was measured. A duration of nothing is not zero (R21).",
                Unit = null,
                Caveats = caveats,
            };
        }

        return answer with { Value = distribution.Of(statistic), Caveats = caveats };
    }

    /// <summary>What a duration measured and what it could not, beside the number.</summary>
    private static List<string> DurationCaveats(
        MetricRequest request,
        OperationTally tally,
        MetricDistribution? distribution,
        long otherInterval)
    {
        bool client = request.DurationInterval == DurationInterval.ClientCall;
        var caveats = new List<string>
        {
            client
                ? "A client call runs from its start to its stop in the calling process: it spans the transport and the "
                    + "server's work, and is never compared with a server execution (§5)."
                : "A server execution runs from a served call's start to its stop in the serving process: the server's part "
                    + "of a call, never its client's wait (§5).",
        };
        if (request.Cohort == OperationCohort.StartedInRange)
        {
            List<string> unmeasured = Parts(
                (tally.InScope.GetValueOrDefault(RpcCallState.OpenAtCaptureEnd), "still open at capture end (right-censored: longer than the capture shows, never counted as short)"),
                (tally.InScope.GetValueOrDefault(RpcCallState.NoActivityId), "with no activity id"),
                (tally.InScope.GetValueOrDefault(RpcCallState.Ambiguous), "of an activity id reused before its stop"));
            caveats.Add(
                "The cohort is the calls started in scope, by their start's reading (§19.2)."
                + (unmeasured.Count == 0 ? string.Empty : $" Of them, {Join(unmeasured)}; none has a duration."));
        }
        else
        {
            List<string> unmeasured = Parts(
                (tally.InScope.GetValueOrDefault(RpcCallState.StartNotObserved), "whose start is not in the evidence (left-censored: it began before the capture or its start was lost, and no duration is invented)"),
                (tally.InScope.GetValueOrDefault(RpcCallState.NoActivityId), "with no activity id"),
                (tally.InScope.GetValueOrDefault(RpcCallState.Ambiguous), "of an activity id reused before its stop"));
            caveats.Add(
                "The cohort is the calls completed in scope, by their stop's reading, the default of §19.2."
                + (unmeasured.Count == 0 ? string.Empty : $" Stops in scope paired with no start have no duration: {Join(unmeasured)}."));
        }

        if (distribution is { } measured)
        {
            caveats.Add(
                string.Create(CultureInfo.CurrentCulture, $"The value is the {Words(request.Statistic!.Value)} of {measured.Count:N0} ")
                + "durations, a duration one call took (nearest rank), never an interpolation.");
            caveats.Add(
                "Summed call time and busy time differ: calls that ran at once make the sum exceed the time the calls kept "
                + "their side busy, which is their union (§19.2).");
        }

        if (otherInterval > 0)
        {
            caveats.Add(
                $"{CountText.Of(otherInterval, "call")} in the interval {CountText.Agree(otherInterval, "was", "were")} made on the other side and "
                + $"{CountText.Agree(otherInterval, "measures", "measure")} {(client ? "server execution" : "client calls")}, another named interval; "
                + $"{CountText.Agree(otherInterval, "it is", "they are")} left out, never mixed in.");
        }

        return caveats;
    }

    private static string Words(DurationStatistic statistic) => statistic switch
    {
        DurationStatistic.Median => "median",
        DurationStatistic.Percentile95 => "95th percentile",
        _ => "maximum",
    };

    private static string Words(RpcCallSide side) => side == RpcCallSide.Client ? "client" : "served";

    /// <summary>What an operation is at this version, said beside every count of them.</summary>
    private static string OperationCaveat(MetricRequest request) =>
        $"Operations are RPC calls, paired under {RpcCallIndex.OperationRule}: a call's start and its stop on one side of "
        + "one process, joined by activity id and never by time."
        + (request.Mechanism is null
            ? " No other mechanism derives an operation at this version, so no other record is counted as one (operations-v1)."
            : string.Empty);

    /// <summary>What the count took and what it left in scope uncounted, by what the calls' records establish.</summary>
    private static List<string> OperationCountCaveats(Metric effective, OperationTally tally)
    {
        var caveats = new List<string>();
        long completed = tally.InScope.GetValueOrDefault(RpcCallState.Completed);
        long unpaired = tally.InScope.Where(entry => entry.Key != RpcCallState.Completed).Sum(entry => entry.Value);
        if (effective == Metric.OperationsStarted)
        {
            List<string> endings = Parts(
                (completed, "completed"),
                (tally.InScope.GetValueOrDefault(RpcCallState.OpenAtCaptureEnd), "open at capture end (censored, not failed)"),
                (tally.InScope.GetValueOrDefault(RpcCallState.NoActivityId), "with no activity id"),
                (tally.InScope.GetValueOrDefault(RpcCallState.Ambiguous), "of an activity id reused before its stop"));
            caveats.Add(
                "A call is counted when its start is in scope, however it ended."
                + (endings.Count == 0 ? string.Empty : $" Of these, {Join(endings)}."));
            return caveats;
        }

        caveats.Add(effective == Metric.Errors
            ? "A failed call is a completed call whose stop reports a status other than 0, the RPC status the provider "
                + "reports (0 is RPC_S_OK). It is counted when its stop is in scope."
            : "A call is counted when its stop is in scope and paired with its start: it completed in scope.");
        if (unpaired > 0)
        {
            List<string> reasons = Parts(
                (tally.InScope.GetValueOrDefault(RpcCallState.StartNotObserved), "whose start is not in the evidence"),
                (tally.InScope.GetValueOrDefault(RpcCallState.NoActivityId), "with no activity id"),
                (tally.InScope.GetValueOrDefault(RpcCallState.Ambiguous), "of an activity id reused before its stop"));
            string are = CountText.Agree(unpaired, "is", "are");
            long failures = tally.UnpairedFailures;
            caveats.Add(
                $"{CountText.Of(unpaired, "stop")} in scope {are} paired with no start and {are} not counted: {Join(reasons)}."
                + (effective == Metric.Errors && failures > 0
                    ? unpaired == 1
                        ? " It reports a failure status; its call is not complete in the evidence, so it is stated here rather "
                            + "than counted."
                        : string.Create(CultureInfo.CurrentCulture, $" {failures:N0} of them {CountText.Agree(failures, "reports", "report")} ")
                            + $"a failure status; {CountText.Agree(failures, "its call is", "their calls are")} not complete in the evidence, "
                            + $"so {CountText.Agree(failures, "it is", "they are")} stated here rather than counted."
                    : string.Empty));
        }

        if (effective == Metric.Errors && tally.Unknown > 0)
        {
            caveats.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{tally.Unknown:N0} of {CountText.Of(tally.Known + tally.Unknown, "completed call")} in scope carried no status. ")
                + $"{CountText.Agree(tally.Unknown, "It is", "They are")} counted as unknown, neither failed nor succeeded, and never as "
                + "zero (R3).");
        }

        return caveats;
    }

    private static List<string> OperationGroupingCaveats(MetricRequest request, IReadOnlyList<MetricGroup> unattributed)
    {
        var caveats = new List<string>();
        if (request.Grouping is not (LaneGrouping.InstanceOnly or LaneGrouping.Executable))
        {
            return caveats;
        }

        caveats.Add(
            $"Calls are grouped by the process instance each binds to under {ProcessInstanceIndex.BindingRule}: its first "
            + "record's, raised in the process it describes (ADR-030). A call bound to no instance is reported by the reason "
            + "and never attributed to the nearest instance (P6).");
        if (unattributed.FirstOrDefault(group => group.Reason == ProcessBindingReason.NotAdmittedByPolicy) is { } notAdmitted
            && request.EvidencePolicy < EvidencePolicy.IncludeCandidates)
        {
            long calls = notAdmitted.KnownContributions + notAdmitted.UnknownContributions;
            caveats.Add(request.EvidencePolicy == EvidencePolicy.DirectOnly
                ? NotDirect(calls, "call", "its first record's reading falls", "their first records' readings fall")
                : $"{CountText.Of(calls, "call")} {CountText.Agree(calls, "lies", "lie")} in the lifetime of a later instance of a PID "
                    + $"this capture reused, so {CountText.Agree(calls, "it is a candidate and stays", "they are candidates and stay")} "
                    + "unattributed under this evidence policy; ask for candidates to include "
                    + $"{CountText.Agree(calls, "it", "them")}, labelled (identity-v1).");
        }

        return caveats;
    }

    private static List<string> Parts(params (long Count, string Text)[] parts) =>
        [.. parts
            .Where(part => part.Count > 0)
            .Select(part => string.Create(CultureInfo.CurrentCulture, $"{part.Count:N0} {part.Text}"))];

    private static string Join(List<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
    };

    /// <summary>
    /// The calls one count takes, and the groups it would rank them in. A call joins the count when its counted record is
    /// in scope; the metric decides which of those it takes and which it only states.
    /// </summary>
    private sealed class OperationTally
    {
        private readonly Metric metric;
        private readonly ProcessInstanceIndex processes;
        private readonly EvidencePolicy policy;
        private readonly LaneGrouping? grouping;
        private readonly Dictionary<RpcCallState, long> inScope = [];
        private readonly Dictionary<string, OperationGroup> groups = new(StringComparer.Ordinal);
        private readonly bool? byStart;
        private readonly DurationStatistic? statistic;
        private readonly List<(long Start, long Stop)> spans = [];

        /// <param name="byStart">For a duration, whether its cohort is the calls started in scope; a count decides by its metric.</param>
        /// <param name="statistic">For a duration, the statistic its value and its ranking read; null for a count.</param>
        public OperationTally(
            Metric metric,
            ProcessInstanceIndex processes,
            EvidencePolicy policy,
            LaneGrouping? grouping,
            bool? byStart = null,
            DurationStatistic? statistic = null)
        {
            this.metric = metric;
            this.processes = processes;
            this.policy = policy;
            this.grouping = grouping;
            this.byStart = byStart;
            this.statistic = statistic;
        }

        /// <summary>Whether a call is put in scope by its start; otherwise by its stop.</summary>
        public bool CountsByStart => byStart ?? metric == Metric.OperationsStarted;

        public Dictionary<RpcCallState, long> InScope => inScope;

        public long Value { get; private set; }

        public long Known { get; private set; }

        public long Unknown { get; private set; }

        public long UnpairedFailures { get; private set; }

        public long OutsideInterval { get; set; }

        public long OutsideProcess { get; set; }

        /// <summary>
        /// Adds one call whose counted record is in scope; returns whether the count took it into its value. Grouped by peer,
        /// it belongs to <paramref name="counterpart"/>, the process at its other end from the focus.
        /// </summary>
        public bool Add(RpcCallOutcome call, ProcessBinding? counterpart = null)
        {
            inScope[call.State] = inScope.GetValueOrDefault(call.State) + 1;
            bool completed = call.State == RpcCallState.Completed;
            bool failed = call.Status is { } status && status != 0;
            if (!completed && failed)
            {
                UnpairedFailures++;
            }

            // A started count takes every start; a completed or failed count takes only a stop paired with its start.
            if (!CountsByStart && !completed)
            {
                return false;
            }

            bool known = metric != Metric.Errors || call.Status is not null;
            bool counts = metric != Metric.Errors || failed;
            Known += known ? 1 : 0;
            Unknown += known ? 0 : 1;
            Value += counts ? 1 : 0;
            if (grouping is not null)
            {
                GroupOf(call, counterpart).Add(call.Process, known, counts);
            }

            return counts;
        }

        /// <summary>
        /// Adds one call of a duration's cohort, measured when <paramref name="span"/> holds its start and stop in session
        /// nanoseconds; returns whether it was measured.
        /// </summary>
        public bool AddTimed(RpcCallOutcome call, (long Start, long Stop)? span, ProcessBinding? counterpart = null)
        {
            inScope[call.State] = inScope.GetValueOrDefault(call.State) + 1;
            if (span is { } measured)
            {
                Known++;
                spans.Add(measured);
            }
            else
            {
                Unknown++;
            }

            if (grouping is not null)
            {
                GroupOf(call, counterpart).AddTimed(call.Process, span);
            }

            return span is not null;
        }

        /// <summary>The distribution of every measured call of a duration's cohort; null when none was measured.</summary>
        public MetricDistribution? Distribution() => MetricDistribution.Of(spans);

        /// <summary>
        /// The ranked groups, then the unmeasured ones, cut to <paramref name="rows"/> with an exact remainder, and the
        /// calls no group could take, one group per reason. Together they hold every call the count took, once. A
        /// duration's groups rank by its statistic, and its remainder is the distribution of the calls it merges, never a
        /// sum of statistics.
        /// </summary>
        public (IReadOnlyList<MetricGroup> Ordered, MetricGroup? Remainder, IReadOnlyList<MetricGroup> Unattributed) Groups(
            int? rows,
            Context context)
        {
            bool isRate = context.Request.Metric == Metric.Rate;
            var attributed = new List<(MetricGroup Group, OperationGroup Source, string Key)>();
            var unattributed = new List<(MetricGroup Group, string Key)>();
            foreach ((string key, OperationGroup group) in groups)
            {
                MetricGroup built = group.ToGroup(isRate, context, statistic);
                if (built.Kind == MetricGroupKind.Unattributed)
                {
                    unattributed.Add((built, key));
                }
                else
                {
                    attributed.Add((built, group, key));
                }
            }

            List<(MetricGroup Group, OperationGroup Source)> ordered =
            [
                .. attributed
                    .Where(entry => entry.Group.Value is not null)
                    .OrderByDescending(entry => entry.Group.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select((entry, index) => (entry.Group with { Rank = index + 1 }, entry.Source)),
                .. attributed
                    .Where(entry => entry.Group.Value is null)
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => (entry.Group, entry.Source)),
            ];
            MetricGroup? remainder = null;
            if (rows is { } cut && ordered.Count > cut)
            {
                List<(MetricGroup Group, OperationGroup Source)> rest = ordered[cut..];
                long known = rest.Sum(entry => entry.Group.KnownContributions);
                long unknown = rest.Sum(entry => entry.Group.UnknownContributions);
                if (statistic is { } named)
                {
                    MetricDistribution? merged = MetricDistribution.Of([.. rest.SelectMany(entry => entry.Source.Spans)]);
                    remainder = new()
                    {
                        Kind = MetricGroupKind.Remainder,
                        Value = merged?.Of(named),
                        KnownContributions = known,
                        UnknownContributions = unknown,
                        GroupsMerged = rest.Count,
                        Distribution = merged,
                    };
                }
                else
                {
                    long value = rest.Sum(entry => entry.Group.Value ?? 0);
                    bool measured = known > 0;
                    remainder = new()
                    {
                        Kind = MetricGroupKind.Remainder,
                        Value = measured ? value : null,
                        KnownContributions = known,
                        UnknownContributions = unknown,
                        GroupsMerged = rest.Count,
                        Rate = isRate && measured ? RateOf(value, MeasurementUnit.Count, context) : null,
                    };
                }

                ordered = ordered[..cut];
            }

            return (
                [.. ordered.Select(entry => entry.Group)],
                remainder,
                [.. unattributed.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Group)]);
        }

        private OperationGroup GroupOf(RpcCallOutcome call, ProcessBinding? counterpart)
        {
            // A peer group is the process at the call's other end from the focus; any other process group is the call's own.
            ProcessBinding binding = grouping == LaneGrouping.Peer
                ? counterpart ?? throw new InvalidOperationException("A peer grouping needs each call's counterpart.")
                : call.Process;
            (string key, Func<OperationGroup> create) = grouping switch
            {
                LaneGrouping.Mechanism => (
                    ((int)Mechanism.Rpc).ToString("D3", CultureInfo.InvariantCulture),
                    () => new OperationGroup(MetricGroupKind.Mechanism) { Mechanism = Mechanism.Rpc }),
                _ when !binding.IsBound => Unattributed(binding.Reason),
                _ when !binding.IsAdmittedUnder(policy) => Unattributed(ProcessBindingReason.NotAdmittedByPolicy),
                LaneGrouping.Executable when string.IsNullOrWhiteSpace(processes.Instances[binding.Instance].ImagePath) =>
                    Unattributed(ProcessBindingReason.ExecutableUnknown),
                LaneGrouping.Executable => (
                    processes.Instances[binding.Instance].ImagePath!.ToUpperInvariant(),
                    () => new OperationGroup(MetricGroupKind.Executable) { Executable = processes.Instances[binding.Instance].ImagePath }),
                _ => (
                    processes.Instances[binding.Instance].Id.ToString(),
                    () => new OperationGroup(MetricGroupKind.ProcessInstance) { Process = processes.Instances[binding.Instance] }),
            };
            if (!groups.TryGetValue(key, out OperationGroup? group))
            {
                group = create();
                groups[key] = group;
            }

            return group;
        }

        private static (string Key, Func<OperationGroup> Create) Unattributed(ProcessBindingReason reason) => (
            $"unattributed/{(int)reason:D3}",
            () => new OperationGroup(MetricGroupKind.Unattributed) { Reason = reason });
    }

    /// <summary>One group of calls while a count is taken: its value, its known and unknown calls, and its bindings.</summary>
    private sealed class OperationGroup(MetricGroupKind kind)
    {
        private readonly Dictionary<RelationStrength, long> bindings = [];
        private readonly List<(long Start, long Stop)> spans = [];
        private long value;
        private long known;
        private long unknown;

        /// <summary>The measured calls of a duration's group, each from its start to its stop in session nanoseconds.</summary>
        public IReadOnlyList<(long Start, long Stop)> Spans => spans;

        public ProcessInstance? Process { get; init; }

        public string? Executable { get; init; }

        public Mechanism? Mechanism { get; init; }

        public ProcessBindingReason? Reason { get; init; }

        public void Add(ProcessBinding binding, bool isKnown, bool counts)
        {
            value += counts ? 1 : 0;
            known += isKnown ? 1 : 0;
            unknown += isKnown ? 0 : 1;
            Bind(binding);
        }

        public void AddTimed(ProcessBinding binding, (long Start, long Stop)? span)
        {
            if (span is { } measured)
            {
                known++;
                spans.Add(measured);
            }
            else
            {
                unknown++;
            }

            Bind(binding);
        }

        private void Bind(ProcessBinding binding)
        {
            if (kind is MetricGroupKind.ProcessInstance or MetricGroupKind.Executable)
            {
                bindings[binding.Strength] = bindings.GetValueOrDefault(binding.Strength) + 1;
            }
        }

        /// <summary>
        /// The group a reader sees. A group whose calls all carry no status is unmeasured, never ranked at zero; a group
        /// with no call the count took is never built (R21).
        /// </summary>
        public MetricGroup ToGroup(bool isRate, Context context, DurationStatistic? statistic)
        {
            if (statistic is { } named)
            {
                // A duration's group is measured when one of its calls was; one whose calls were all censored or unpaired
                // is unmeasured, and sorts after every ranked group rather than at zero.
                MetricDistribution? distribution = MetricDistribution.Of(spans);
                return new()
                {
                    Kind = kind,
                    Process = Process,
                    Executable = Executable,
                    Mechanism = Mechanism,
                    Reason = Reason,
                    Value = distribution?.Of(named),
                    KnownContributions = known,
                    UnknownContributions = unknown,
                    Bindings = bindings,
                    Distribution = distribution,
                };
            }

            bool measured = known > 0;
            return new()
            {
                Kind = kind,
                Process = Process,
                Executable = Executable,
                Mechanism = Mechanism,
                Reason = Reason,
                Value = measured ? value : null,
                KnownContributions = known,
                UnknownContributions = unknown,
                Bindings = bindings,
                Rate = isRate && measured ? RateOf(value, MeasurementUnit.Count, context) : null,
            };
        }
    }
}
