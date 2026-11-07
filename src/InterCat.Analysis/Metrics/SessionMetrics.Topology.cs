using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// What an answer on the resource-topology basis read (`contracts/metrics-v1.md` §8b): the resources it took, by
/// mechanism, how many of them are sections by their views and how many an image's, how many were made before the capture,
/// held at its stop or ended unseen, and their memberships - each process holding each resource, once - beside every
/// resource the generation derives.
/// </summary>
public sealed record MetricResources
{
    /// <summary>The resources the answer took, by mechanism; a mechanism none of them is of is absent.</summary>
    public required IReadOnlyDictionary<Mechanism, long> Taken { get; init; }

    /// <summary>Of those, the sections found by their views, never joined to a section by its handles (ADR-092).</summary>
    public long ByViews { get; init; }

    /// <summary>Of the sections by their views, those whose views are an image's: a module loaded, not memory shared.</summary>
    public long Images { get; init; }

    /// <summary>How many of them were made before the capture: their first record made no handle, socket or view.</summary>
    public long MadeBefore { get; init; }

    /// <summary>How many of them the stop's rundown held.</summary>
    public long HeldAtStop { get; init; }

    /// <summary>How many of them ended unseen: their last record neither a close nor an unmapping, nor the stop's rundown.</summary>
    public long EndUnseen { get; init; }

    /// <summary>Their memberships: each process instance holding each of them, counted once a pair.</summary>
    public long Memberships { get; init; }

    /// <summary>
    /// Of the resources the process filter left out, those with records bound to no process: the filter's process could
    /// hold them unseen, so they are disclosed, never counted.
    /// </summary>
    public long Disclosed { get; init; }

    /// <summary>Every resource the generation derives, taken or not.</summary>
    public required long Resources { get; init; }
}

/// <summary>
/// Answers on the resource-topology basis (`contracts/metrics-v1.md` §8b, ADR-098). A resource is an instance
/// `resource-instances-v3` derives - a named synchronization object, a section by its handles or by its views, an AF_UNIX
/// socket - in scope where its known lifetime meets the request's interval, and counted once, never by its records. A count
/// of the processes sharing one reads its holders, a capacity the largest extent its views mapped, and a count of
/// observations its membership and lifecycle records, each read in scope. None of it is traffic: a handle or a view proves
/// who held a resource, never what moved through it (P27).
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>
    /// What a topology request needs that this version's resources do not answer, or null when they answer it. The matrix
    /// accepts every one of these requests; each means something no resource says yet, or something no membership says.
    /// </summary>
    private static MetricResult? WhatTopologyNeeds(MetricRequest request, long generation)
    {
        string? missing = request.Metric switch
        {
            Metric.Duration when request.Grouping is { } grouped && !ProcessGrouping.ByInstanceFact(grouped) =>
                $"Grouping a mapping lifetime by {grouped} is not defined at this version: it is grouped by a fact of the "
                + "process holding each mapping - each process, its executable, its session, its package or its service "
                + "container (§8b).",
            _ => null,
        };
        missing ??= request.Grouping is { } grouping && !ProcessGrouping.ByInstanceFact(grouping)
                && grouping is not (LaneGrouping.Mechanism or LaneGrouping.Peer)
            ? $"Grouping resources by {grouping} is not defined at this version: they are grouped by their mechanism, by the "
                + "processes sharing them with a focus, or by a fact of the processes holding them - each process, its "
                + "executable, its session, its package or its service container (§8b)."
            : null;
        string? directed = request.Focus is { Role: ProcessRole.Sender or ProcessRole.Receiver } focus ? $"{Role(focus.Role)}(P)"
            : request.Between is { Direction: not BetweenDirection.Either } ? "A directional between(A,B)"
            : null;
        missing ??= directed is null
            ? null
            : $"{directed} selects data that flowed one way, and holding a resource is undirected: a handle or a view says who "
                + "held it, never which way anything moved. owner(P) or participant(P) keeps the resources P held, and "
                + "between(A,B) either way those held on both sides (§8b).";
        return missing is null ? null : Unavailable(request, generation, MetricUnavailableReason.NoResourceTopology, missing);
    }

    /// <summary>Counts the resources a topology request takes, their records, the processes sharing them, or their capacity (§8b).</summary>
    private static MetricResult Topology(Context context, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        if (context.Clock is null)
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.NoEntityBindings,
                "A resource's records are bound to the process instances holding it, which are identified within the host and "
                + "boot the capture's clock scopes. This session does not describe its clock, so no resource can be derived "
                + "(identity-v1).");
        }

        ProcessInstanceIndex processes = ProcessesOf(context, cancellationToken);
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

        // A count of records reads each one's own reading against the interval, in the pass that derives the resources.
        bool records = request.Metric == Metric.Observations;
        ResourceInstanceIndex index = ResourceInstanceIndex.Derive(
            [.. context.Segments.Select(segment => segment.Reader)],
            context.FieldSegments,
            processes,
            request.EvidencePolicy,
            records ? request.Interval : null,
            cancellationToken);

        // A mapping lifetime is a view's, each mapping in scope by its own readings rather than its section's lifetime.
        if (request.Metric == Metric.Duration)
        {
            return MappingLifetimes(context, context.Clock.Value, index, processes);
        }

        // A resource is in scope where its mechanism and layer are the projection's and its known lifetime meets the
        // interval: from its first record to its last, open before the first where it was made before the capture and after
        // the last where the stop's rundown held it. A process filter keeps those its holders are; one it leaves out whose
        // records bound to no process could be the focus's, so it is disclosed by those records' reasons, never guessed in.
        var taken = new List<ResourceInstance>();
        var unresolved = new Dictionary<ProcessBindingReason, long>();
        long outsideProjection = 0;
        long outsideInterval = 0;
        long outsideProcess = 0;
        long unresolvedResources = 0;
        foreach (ResourceInstance resource in index.Instances)
        {
            if ((request.Mechanism is { } mechanism && resource.Mechanism != mechanism)
                || (request.Layer is { } layer && resource.Layer != layer))
            {
                outsideProjection++;
                continue;
            }

            if (request.Interval is { } interval
                && ((resource.FirstHandleMade && resource.FirstNativeTicks >= interval.EndTicks)
                    || (!resource.HeldAtStop && resource.LastNativeTicks < interval.StartTicks)))
            {
                outsideInterval++;
                continue;
            }

            if (!Held(request, resource))
            {
                outsideProcess++;
                if (resource.Unbound.Count > 0)
                {
                    unresolvedResources++;
                    Add(unresolved, resource.Unbound);
                }

                continue;
            }

            taken.Add(resource);
        }

        MetricResult answer = context.Answer(request) with
        {
            ResourceRule = ResourceInstanceIndex.Rule,
            Resources = new MetricResources
            {
                Taken = taken.GroupBy(resource => resource.Mechanism).OrderBy(group => group.Key)
                    .ToDictionary(group => group.Key, group => (long)group.Count()),
                ByViews = taken.Count(resource => resource.ByViews),
                Images = taken.Count(resource => resource.Image),
                MadeBefore = taken.Count(resource => !resource.FirstHandleMade),
                HeldAtStop = taken.Count(resource => resource.HeldAtStop),
                EndUnseen = taken.Count(resource => !resource.HeldAtStop && !resource.LastHandleClosed),
                Memberships = taken.Sum(resource => (long)resource.Holders.Count),
                Disclosed = unresolvedResources,
                Resources = index.Instances.Count,
            },
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = outsideInterval,
            ExcludedByProcessFilter = outsideProcess,
            UnresolvedCounterparts = unresolved,
        };

        var caveats = new List<string>
        {
            records
                ? "Each record counted is a membership or lifecycle record of a resource resource-instances-v3 derives - a handle "
                    + "made, duplicated, closed or held at the stop, a view mapped, unmapped or listed by a rundown, or a socket's "
                    + "create, bind, connect, accept or close - whichever process it binds to; a socket's sends and receives are "
                    + "traffic, which the source-observations basis counts. A record proves which process held a resource, never "
                    + "traffic through it (P27)."
                : "Each resource is one instance resource-instances-v3 derives, counted once over its known lifetime, never by "
                    + "its records: a handle or a view proves which processes held it, never traffic through it (P27). A section "
                    + "found by its handles and by its views is two resources, as no record joins them (ADR-092).",
        };
        if (request.Interval is not null)
        {
            caveats.Add("A resource is in scope where its known lifetime meets the interval: from its first record to its last, "
                + "open before the first where it was made before the capture and after the last where the stop's rundown held it."
                + (records ? " Of its records, those read in the interval count." : string.Empty));
        }

        // The records of a resource in scope read outside the interval are left out, each by its own reading.
        long readOutside = records && index.RecordsCountedIn is not null
            ? taken.Sum(resource => resource.Records - (resource.Traffic?.Sends ?? 0) - (resource.Traffic?.Receives ?? 0)
                - resource.MembershipRecords)
            : 0;
        if (readOutside > 0)
        {
            caveats.Add($"{CountText.Of(readOutside, "membership or lifecycle record")} of the resources in scope "
                + $"{CountText.Agree(readOutside, "was", "were")} read outside the interval and "
                + $"{CountText.Agree(readOutside, "is", "are")} left out.");
        }

        if (unresolvedResources > 0)
        {
            caveats.Add($"{CountText.Of(unresolvedResources, "resource")} in scope {CountText.Agree(unresolvedResources, "has", "have")} "
                + $"records bound to no process ({Reasons(unresolved)}), so the process filter could hold "
                + $"{CountText.Agree(unresolvedResources, "it", "them")} unseen: disclosed, never counted.");
        }

        if (context.EvidenceLimit > 0)
        {
            caveats.Add(records
                ? "A count of resources' records lists none of them here. icat evidence --channel lists one resource's records "
                    + "by its key, as the window's export of them names it."
                : "A topology answer counts resources, not records, so it lists no record. icat resources lists each "
                    + "resource with the processes holding it and its ends.");
        }

        if (request.Grouping == LaneGrouping.Mechanism)
        {
            return MechanismTopology(request, taken, answer, caveats);
        }

        // A peer grouping is the focus's, which the request check requires of it.
        if (request.Grouping == LaneGrouping.Peer)
        {
            return PeerTopology(request, processes, taken, answer, caveats);
        }

        if (request.Grouping is { } byProcess)
        {
            return GroupedTopology(context, byProcess, processes, taken, answer, caveats);
        }

        return request.Metric switch
        {
            Metric.ActiveChannels => ResourceCount(answer, taken, caveats),
            Metric.ActivePeers => MembershipPeers(answer, taken, request.Focus!.Value.Instance, caveats),
            Metric.MappingCapacity => Capacity(answer, taken, caveats),
            Metric.Observations => RecordCount(answer, taken, caveats),
            _ => Unavailable(request, context.Generation, MetricUnavailableReason.NoResourceTopology,
                $"{request.Metric} is not answered on the resource-topology basis at this version."),
        };
    }

    // Whether a resource's holders are the process filter's: the focus holds it, and the peer too where one narrows the
    // focus; or a process of each between set does. No filter keeps every resource.
    private static bool Held(MetricRequest request, ResourceInstance resource)
    {
        bool Holds(ProcessInstanceId instance) => resource.Holders.Any(holder => holder.Process == instance);
        return request.Focus is { } focus
            ? Holds(focus.Instance) && (request.Peer is not { } peer || Holds(peer))
            : request.Between is not { } between || (between.First.Any(Holds) && between.Second.Any(Holds));
    }

    private static void Add(Dictionary<ProcessBindingReason, long> into, IReadOnlyDictionary<ProcessBindingReason, long> counts)
    {
        foreach ((ProcessBindingReason reason, long count) in counts)
        {
            into[reason] = into.GetValueOrDefault(reason) + count;
        }
    }

    // ActiveChannels on the topology basis: the resources taken, each one channel. None taken is a count of the resources
    // this generation derives, not a finding that none existed (R21).
    private static MetricResult ResourceCount(MetricResult answer, List<ResourceInstance> taken, List<string> caveats)
    {
        if (taken.Count == 0)
        {
            caveats.Add("No resource this generation derives is in scope. That is a count of derived resources, not a finding "
                + "that none existed: a capture records them only under a profile admitting their sources (R21).");
        }

        if (answer.UnresolvedCounterparts.Count > 0)
        {
            caveats.Add($"At least {taken.Count:N0}: the resources disclosed above may be the filter's too.");
        }

        return answer with
        {
            Value = taken.Count,
            Unit = MeasurementUnit.Count,
            KnownContributions = taken.Count,
            Caveats = caveats,
        };
    }

    // Observations on the topology basis: the membership and lifecycle records of the resources taken, each read in scope by
    // its own reading, whichever process it binds to. None is a count of the records of the resources this generation
    // derives, not a finding that no process held one (R21).
    private static MetricResult RecordCount(MetricResult answer, List<ResourceInstance> taken, List<string> caveats)
    {
        long records = taken.Sum(resource => resource.MembershipRecords);
        if (records == 0)
        {
            caveats.Add("No membership or lifecycle record of a resource this generation derives is in scope. That is a count of "
                + "derived resources' records, not a finding that none was made: a capture records them only under a profile "
                + "admitting their sources (R21).");
        }

        if (answer.UnresolvedCounterparts.Count > 0)
        {
            caveats.Add($"At least {records:N0}: the records of the resources disclosed above may be the filter's too.");
        }

        return answer with
        {
            Value = records,
            Unit = MeasurementUnit.Count,
            KnownContributions = records,
            Caveats = caveats,
        };
    }

    // The ungrouped answer a grouped one's groups are parts or overlapping parts of: its resources, its records or its
    // sections' capacity.
    private static MetricResult Whole(Metric metric, MetricResult answer, List<ResourceInstance> taken, List<string> caveats) => metric switch
    {
        Metric.MappingCapacity => Capacity(answer, taken, caveats),
        Metric.Observations => RecordCount(answer, taken, caveats),
        _ => ResourceCount(answer, taken, caveats),
    };

    // ActivePeers on the topology basis: the process instances other than the focus holding a resource it holds. A resource
    // taken whose records bound to no process may have a holder nothing here names, so the count is a lower bound beside
    // those records; where they are all there is, it is unmeasured, never zero (R21).
    private static MetricResult MembershipPeers(MetricResult answer, List<ResourceInstance> taken, ProcessInstanceId focus, List<string> caveats)
    {
        var peers = new HashSet<ProcessInstanceId>();
        long memberships = 0;
        var unknown = new Dictionary<ProcessBindingReason, long>();
        foreach (ResourceInstance resource in taken)
        {
            foreach (ResourceHolder holder in resource.Holders.Where(holder => holder.Process != focus))
            {
                peers.Add(holder.Process);
                memberships++;
            }

            Add(unknown, resource.Unbound);
        }

        long unknownRecords = unknown.Values.Sum();
        MetricResult counted = answer with
        {
            Unit = MeasurementUnit.Count,
            KnownContributions = memberships,
            UnknownContributions = unknownRecords,
            UnknownCounterparts = unknown,
        };
        if (peers.Count == 0 && unknownRecords > 0)
        {
            return counted with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = $"No other process is bound to a resource the focus held, and "
                    + $"{CountText.Of(unknownRecords, "record")} of those resources bound to none ({Reasons(unknown)}): each may be "
                    + "another holder's, so the count is unmeasured rather than zero (R21, P1).",
                Unit = null,
                Caveats = caveats,
            };
        }

        caveats.Add("A peer here is a process instance holding a resource the focus held - a handle or a view of it - at any "
            + "time in scope, not at once with the focus, and never a process anything was sent to (P27).");
        if (taken.Count == 0)
        {
            caveats.Add("The focus held no resource in scope, so it shared none: a count over the resources this generation "
                + "derives, not a finding that it held none (R21).");
        }

        if (unknownRecords > 0)
        {
            caveats.Add($"At least {peers.Count:N0}: {CountText.Of(unknownRecords, "record")} of the resources the focus held "
                + $"{CountText.Agree(unknownRecords, "binds", "bind")} to no process ({Reasons(unknown)}), and "
                + $"{CountText.Agree(unknownRecords, "it", "each")} may be another holder's.");
        }

        return counted with { Value = peers.Count, Caveats = caveats };
    }

    // MappingCapacity: the largest extent a view of each section by its views mapped, summed once a section - a lower bound
    // of what the section holds, never bytes moved through it (P3). A section by its handles records no size, so it is an
    // unknown contribution; a synchronization object or a socket maps nothing and takes no part. No known extent is
    // unmeasured, never a capacity of zero (R21, P1).
    private static MetricResult Capacity(MetricResult answer, List<ResourceInstance> taken, List<string> caveats)
    {
        long capacity = 0;
        long known = 0;
        long images = 0;
        long imageCapacity = 0;
        long unsized = 0;
        foreach (ResourceInstance resource in taken.Where(resource => resource.Mechanism == Mechanism.SharedSection))
        {
            if (resource is { ByViews: true, LargestView: { } extent })
            {
                capacity = SegmentMeasurement.AddBytes(capacity, extent);
                known++;
                if (resource.Image)
                {
                    images++;
                    imageCapacity = SegmentMeasurement.AddBytes(imageCapacity, extent);
                }
            }
            else
            {
                unsized++;
            }
        }

        long others = taken.Count(resource => resource.Mechanism != Mechanism.SharedSection);
        MetricResult summed = answer with
        {
            Unit = MeasurementUnit.Bytes,
            KnownContributions = known,
            UnknownContributions = unsized,
            UnknownReasons = unsized > 0
                ? new Dictionary<FieldAvailability, long> { [FieldAvailability.NotExposed] = unsized }
                : new Dictionary<FieldAvailability, long>(),
        };
        if (others > 0)
        {
            caveats.Add($"{CountText.Of(others, "resource")} in scope {CountText.Agree(others, "is", "are")} no section - a "
                + "synchronization object or a socket maps nothing - and takes no part in a capacity.");
        }

        if (unsized > 0)
        {
            caveats.Add($"{CountText.Of(unsized, "section")} in scope recorded no size: a section by its handles names none, "
                + "and only a view's mapping states the extent it mapped.");
        }

        if (known == 0)
        {
            return summed with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = "No section in scope was found by its views with the extent one mapped, so no capacity "
                    + "is known, which is not a capacity of zero (R21, P1).",
                Unit = null,
                Caveats = caveats,
            };
        }

        caveats.Add("Each section's capacity is the largest extent a view of it mapped - a lower bound of its size - counted once "
            + "a section however many processes mapped it, never bytes moved through it (P3).");
        if (images > 0)
        {
            caveats.Add($"{CountText.Of(images, "section")} of it {CountText.Agree(images, "is", "are")} an image's, "
                + $"{imageCapacity:N0} bytes: a module's views, not memory a process shared, which icat resources lists only "
                + "with --images.");
        }

        return summed with { Value = capacity, Caveats = caveats };
    }

    /// <summary>
    /// A topology answer grouped by a fact of each process (§8b): each group's resources - those one of its processes held -
    /// their records, the other processes holding one of them, or their sections' capacity. A resource several groups'
    /// processes held counts in each, so the groups overlap and do not add up to the total, which is the count, the records
    /// or the capacity over every resource taken. A membership no group can take - a record bound to no process, or a
    /// process without the grouping's fact - is unattributed by its reason.
    /// </summary>
    private static MetricResult GroupedTopology(
        Context context,
        LaneGrouping grouping,
        ProcessInstanceIndex processes,
        List<ResourceInstance> taken,
        MetricResult answer,
        List<string> caveats)
    {
        MetricRequest request = context.Request;
        ServiceMatch? services = ServicesOf(context, processes);
        if (ProcessGrouping.Underived(processes, grouping, services) is { } underived)
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.GroupingNotDerived, underived);
        }

        bool peers = request.Metric == Metric.ActivePeers;
        bool capacity = request.Metric == Metric.MappingCapacity;

        // A capacity is a section's: no other resource takes part, in any group.
        List<ResourceInstance> counted = capacity ? [.. taken.Where(resource => resource.Mechanism == Mechanism.SharedSection)] : taken;
        var tallies = new Dictionary<string, DistinctTally>(StringComparer.Ordinal);
        var unattributed = new Dictionary<ProcessBindingReason, long>();
        var sharers = new HashSet<int>();
        for (int at = 0; at < counted.Count; at++)
        {
            ResourceInstance resource = counted[at];
            Add(unattributed, resource.Unbound);
            foreach (ResourceHolder holder in resource.Holders)
            {
                ProcessInstance instance = processes.Instances[processes.PositionOf(holder.Process)];
                if (ProcessGrouping.Unattributable(instance, grouping, services) is { } ungrouped)
                {
                    unattributed[ungrouped] = unattributed.GetValueOrDefault(ungrouped) + 1;
                    continue;
                }

                string key = ProcessGrouping.KeyOf(instance, grouping, services);
                if (!tallies.TryGetValue(key, out DistinctTally? tally))
                {
                    tally = grouping switch
                    {
                        LaneGrouping.Executable => new(key, MetricGroupKind.Executable) { Executable = instance.ImagePath },
                        LaneGrouping.UserSession => new(key, MetricGroupKind.UserSession) { Session = instance.SessionId },
                        LaneGrouping.Package => new(key, MetricGroupKind.Package) { Package = instance.PackageFullName },
                        LaneGrouping.ServiceContainer => services!.ContainerOf(instance.Id) is { } hosted
                            ? new(key, MetricGroupKind.ServiceContainer) { Process = instance, Services = hosted }
                            : new(key, MetricGroupKind.Executable) { Executable = instance.ImagePath },
                        _ => new(key, MetricGroupKind.ProcessInstance) { Process = instance },
                    };
                    tallies[key] = tally;
                }

                if (!peers)
                {
                    tally.Elements.Add(at);
                    continue;
                }

                // A process's peers are the other processes holding what it held; a record bound to none may be one more.
                foreach (ResourceHolder other in resource.Holders.Where(other => other.Process != holder.Process))
                {
                    int peer = processes.PositionOf(other.Process);
                    tally.Elements.Add(peer);
                    tally.Known++;
                    sharers.Add(peer);
                }

                foreach ((ProcessBindingReason reason, long records) in resource.Unbound)
                {
                    tally.UnknownReasons[reason] = tally.UnknownReasons.GetValueOrDefault(reason) + records;
                }
            }
        }

        // Each group's value: its resources counted, its peers counted - none where nothing it held binds another holder
        // and no record of it binds to none, unmeasured where one does - or its sections' capacity.
        MetricGroup GroupOf(DistinctTally tally) => peers
            ? tally.ToGroup() with { Value = tally.Elements.Count > 0 || tally.UnknownReasons.Count == 0 ? tally.Elements.Count : null }
            : Measured(tally.ToGroup() with { Bindings = new Dictionary<RelationStrength, long>() }, [.. tally.Elements.Select(at => counted[at])], request.Metric);
        List<(DistinctTally Tally, MetricGroup Group)> built = [.. tallies.Values.Select(tally => (tally, GroupOf(tally)))];
        List<(DistinctTally Tally, MetricGroup Group)> ordered =
        [
            .. built.Where(entry => entry.Group.Value is not null)
                .OrderByDescending(entry => entry.Group.Value)
                .ThenBy(entry => entry.Tally.StableKey, StringComparer.Ordinal)
                .Select((entry, index) => (entry.Tally, entry.Group with { Rank = index + 1 })),
            .. built.Where(entry => entry.Group.Value is null).OrderBy(entry => entry.Tally.StableKey, StringComparer.Ordinal),
        ];

        // The rest past the rows asked for is one exact answer over the union of what they hold, never a sum of overlapping ones.
        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            List<(DistinctTally Tally, MetricGroup Group)> rest = [.. ordered.Skip(rows)];
            var union = new HashSet<int>(rest.SelectMany(entry => entry.Tally.Elements));
            long restUnknown = rest.Sum(entry => entry.Group.UnknownContributions);
            remainder = peers
                ? new MetricGroup
                {
                    Kind = MetricGroupKind.Remainder,
                    Value = union.Count > 0 || restUnknown == 0 ? union.Count : null,
                    KnownContributions = rest.Sum(entry => entry.Group.KnownContributions),
                    UnknownContributions = restUnknown,
                    GroupsMerged = rest.Count,
                }
                : Measured(new MetricGroup { Kind = MetricGroupKind.Remainder, GroupsMerged = rest.Count },
                    [.. union.Select(at => counted[at])], request.Metric);
            ordered = [.. ordered.Take(rows)];
        }

        MetricGroup whole = peers
            ? new MetricGroup
            {
                Kind = MetricGroupKind.Remainder,
                Value = sharers.Count,
                KnownContributions = tallies.Values.Sum(tally => tally.Known),
                UnknownContributions = tallies.Values.Sum(tally => tally.UnknownReasons.Values.Sum()),
            }
            : Measured(new MetricGroup { Kind = MetricGroupKind.Remainder }, counted, request.Metric);
        caveats.Add(peers
            ? "Two processes holding one resource are each other's peers, so each counts in the other's group: the groups overlap "
                + "and do not add up to the total, the number of distinct processes sharing a resource with another."
            : capacity
            ? "A section several groups' processes mapped counts its capacity in each group: the groups overlap and do not add up "
                + "to the total, the capacity of every section in scope, once each."
            : request.Metric == Metric.Observations
            ? "A resource several groups' processes held counts its records in each group, whichever process each binds to: the "
                + "groups overlap and do not add up to the total, the records of every resource in scope, once each."
            : "A resource several groups' processes held counts in each group: the groups overlap and do not add up to the total, "
                + "the number of distinct resources in scope.");
        long unbound = unattributed.Values.Sum();
        if (unbound > 0)
        {
            caveats.Add($"{CountText.Of(unbound, "membership")} {CountText.Agree(unbound, "is", "are")} no group's "
                + $"({Reasons(unattributed)}): a record bound to no process, or a process without the grouping's fact, held "
                + $"{CountText.Agree(unbound, "it", "them")}.");
        }

        MetricResult grouped = answer with
        {
            Value = whole.Value,
            Unit = capacity ? MeasurementUnit.Bytes : MeasurementUnit.Count,
            KnownContributions = whole.KnownContributions,
            UnknownContributions = whole.UnknownContributions,
            UnknownReasons = capacity && whole.UnknownContributions > 0
                ? new Dictionary<FieldAvailability, long> { [FieldAvailability.NotExposed] = whole.UnknownContributions }
                : new Dictionary<FieldAvailability, long>(),
            Groups = [.. ordered.Select(entry => entry.Group)],
            Remainder = remainder,
            Unattributed =
            [
                .. unattributed.OrderBy(entry => entry.Key).Select(entry => new MetricGroup
                {
                    Kind = MetricGroupKind.Unattributed,
                    Reason = entry.Key,
                    UnknownContributions = entry.Value,
                }),
            ],
            GroupsPartitionTotal = false,
            BindingRule = ProcessInstanceIndex.BindingRule,
            Caveats = caveats,
        };

        // A capacity no extent is known of, or a count of peers where only records bound to no process could name one, is
        // unmeasured, its groups beside it, never zero (R21, P1).
        string? unmeasured = capacity && whole.Value is null
            ? "No section in scope was found by its views with the extent one mapped, so no capacity is known, which is not a "
                + "capacity of zero (R21, P1)."
            : peers && sharers.Count == 0 && whole.UnknownContributions > 0
            ? "No process is bound to a resource another process held, and records of those resources bound to none: each "
                + "may be another holder's, so the count is unmeasured rather than zero (R21, P1)."
            : null;
        return unmeasured is null
            ? grouped
            : grouped with
            {
                Value = null,
                Unit = null,
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = unmeasured,
            };
    }

    /// <summary>
    /// A topology answer grouped by mechanism (§8b): each mechanism's resources in scope, their records, or for a capacity
    /// its sections'. A resource is of one mechanism, so the groups partition the total - the resources in scope, their
    /// records, or every section's capacity once - and a capacity has one group, sections, as nothing else maps. A count of
    /// peers grouped so is refused before it reaches here, as on every basis: a count of peers needs a focus, and with one
    /// it is one count.
    /// </summary>
    private static MetricResult MechanismTopology(MetricRequest request, List<ResourceInstance> taken, MetricResult answer, List<string> caveats)
    {
        bool capacity = request.Metric == Metric.MappingCapacity;
        caveats.Add(capacity
            ? "Grouped by mechanism, a capacity is its sections' alone: a synchronization object or a socket maps nothing."
            : request.Metric == Metric.Observations
            ? "Grouped by mechanism, each record is of one resource, of one mechanism, so the groups partition the total, the "
                + "records in scope."
            : "Grouped by mechanism, each resource is of one, so the groups partition the total, the number of resources in scope.");
        MetricResult whole = Whole(request.Metric, answer, taken, caveats);
        List<ResourceInstance> counted = capacity ? [.. taken.Where(resource => resource.Mechanism == Mechanism.SharedSection)] : taken;
        List<MetricGroup> built =
        [
            .. counted.GroupBy(resource => resource.Mechanism)
                .Select(mechanism => Measured(new MetricGroup { Kind = MetricGroupKind.Mechanism, Mechanism = mechanism.Key },
                    [.. mechanism], request.Metric)),
        ];
        List<MetricGroup> ordered =
        [
            .. built.Where(group => group.Value is not null)
                .OrderByDescending(group => group.Value)
                .ThenBy(group => group.Mechanism)
                .Select((group, index) => group with { Rank = index + 1 }),
            .. built.Where(group => group.Value is null).OrderBy(group => group.Mechanism),
        ];

        // The rest past the rows asked for is its resources' answer, which the partition makes their groups' sum.
        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            HashSet<Mechanism?> rest = [.. ordered.Skip(rows).Select(group => group.Mechanism)];
            remainder = Measured(new MetricGroup { Kind = MetricGroupKind.Remainder, GroupsMerged = rest.Count },
                [.. counted.Where(resource => rest.Contains(resource.Mechanism))], request.Metric);
            ordered = [.. ordered.Take(rows)];
        }

        return whole with { Groups = ordered, Remainder = remainder, GroupsPartitionTotal = true, Caveats = caveats };
    }

    /// <summary>
    /// A topology answer grouped by peer (§8b): each process instance other than the focus holding a resource the focus held,
    /// its value the resources both held, or for a capacity the capacity of the sections both mapped. A resource three
    /// processes held counts in both of the focus's peers' groups, so the groups overlap and do not add up to the total - the
    /// focus's resources or its capacity - and one no other process held is in no group. A record of one bound to no process
    /// could be another peer's, so it is unattributed by its reason.
    /// </summary>
    private static MetricResult PeerTopology(
        MetricRequest request,
        ProcessInstanceIndex processes,
        List<ResourceInstance> taken,
        MetricResult answer,
        List<string> caveats)
    {
        bool capacity = request.Metric == Metric.MappingCapacity;
        ProcessInstanceId focus = request.Focus!.Value.Instance;
        List<ResourceInstance> counted = capacity ? [.. taken.Where(resource => resource.Mechanism == Mechanism.SharedSection)] : taken;
        var shared = new Dictionary<ProcessInstanceId, List<ResourceInstance>>();
        var unattributed = new Dictionary<ProcessBindingReason, long>();
        long alone = 0;
        foreach (ResourceInstance resource in counted)
        {
            Add(unattributed, resource.Unbound);
            ResourceHolder[] others = [.. resource.Holders.Where(holder => holder.Process != focus)];
            alone += others.Length == 0 ? 1 : 0;
            foreach (ResourceHolder other in others)
            {
                (shared.TryGetValue(other.Process, out List<ResourceInstance>? held) ? held : shared[other.Process] = []).Add(resource);
            }
        }

        caveats.Add(capacity
            ? "Grouped by peer, each group is the capacity of the sections the focus and that process both mapped: a section three "
                + "processes mapped counts in both of the focus's peers' groups, so the groups overlap and do not add up to the "
                + "total, the focus's capacity."
            : request.Metric == Metric.Observations
            ? "Grouped by peer, each group is the records of the resources the focus and that process both held, whichever process "
                + "each binds to: a resource three processes held counts its records in both of the focus's peers' groups, so the "
                + "groups overlap and do not add up to the total, the records of the focus's resources."
            : "Grouped by peer, each group is the resources the focus and that process both held: a resource three processes held "
                + "counts in both of the focus's peers' groups, so the groups overlap and do not add up to the total, the focus's "
                + "resources.");
        if (alone > 0)
        {
            caveats.Add($"{CountText.Of(alone, capacity ? "section" : "resource")} the focus held {CountText.Agree(alone, "was", "were")} "
                + $"held by no other process, so {CountText.Agree(alone, "it is", "they are")} in no peer's group.");
        }

        MetricResult whole = Whole(request.Metric, answer, taken, caveats);
        List<MetricGroup> built =
        [
            .. shared.Select(entry => Measured(
                new MetricGroup { Kind = MetricGroupKind.ProcessInstance, Process = processes.Instances[processes.PositionOf(entry.Key)] },
                entry.Value,
                request.Metric)),
        ];
        List<MetricGroup> ordered =
        [
            .. built.Where(group => group.Value is not null)
                .OrderByDescending(group => group.Value)
                .ThenBy(group => group.Process!.Id.ToString(), StringComparer.Ordinal)
                .Select((group, index) => group with { Rank = index + 1 }),
            .. built.Where(group => group.Value is null).OrderBy(group => group.Process!.Id.ToString(), StringComparer.Ordinal),
        ];

        // The rest past the rows asked for is one exact answer over the union of what its peers shared, never a sum.
        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            HashSet<ProcessInstanceId> rest = [.. ordered.Skip(rows).Select(group => group.Process!.Id)];
            remainder = Measured(new MetricGroup { Kind = MetricGroupKind.Remainder, GroupsMerged = rest.Count },
                [.. counted.Where(resource => resource.Holders.Any(holder => rest.Contains(holder.Process)))], request.Metric);
            ordered = [.. ordered.Take(rows)];
        }

        return whole with
        {
            Groups = ordered,
            Remainder = remainder,
            Unattributed =
            [
                .. unattributed.OrderBy(entry => entry.Key).Select(entry => new MetricGroup
                {
                    Kind = MetricGroupKind.Unattributed,
                    Reason = entry.Key,
                    UnknownContributions = entry.Value,
                }),
            ],
            GroupsPartitionTotal = false,
            BindingRule = ProcessInstanceIndex.BindingRule,
            Caveats = caveats,
        };
    }

    /// <summary>
    /// A mapping lifetime (§8b): each mapping of a view of a section by its views - a focus's alone where one is named - whose
    /// counted reading is in scope, its unmapping for the mappings completed in range and its mapping for those started;
    /// measured from its mapping to its unmapping where both were seen, and stated where an end was not - mapped before the
    /// capture, still mapped at its stop, or its unmapping unseen - never given a duration. How long a view stayed mapped,
    /// never a byte moved through it (P27).
    /// </summary>
    private static MetricResult MappingLifetimes(
        Context context,
        SourceClockDescriptor clock,
        ResourceInstanceIndex index,
        ProcessInstanceIndex processes)
    {
        MetricRequest request = context.Request;
        LaneGrouping? grouping = request.Grouping;
        ServiceMatch? services = grouping is null ? null : ServicesOf(context, processes);
        if (grouping is { } fact && ProcessGrouping.Underived(processes, fact, services) is { } underived)
        {
            return Unavailable(request, context.Generation, MetricUnavailableReason.GroupingNotDerived, underived);
        }

        // Grouped, each mapping is its holder's group's - in exactly one - and one no group can take is in its reason's, its
        // lifetime still measured: a mapping bound to no process is no less a mapping, only no group's.
        var tallies = new Dictionary<string, (MetricGroup Group, List<(long Start, long Stop)> Spans, long Censored)>(StringComparer.Ordinal);
        void Tally(ResourceView mapping, (long Start, long Stop)? span, LaneGrouping by)
        {
            ProcessInstance? instance = mapping.Holder is { } holder ? processes.Instances[processes.PositionOf(holder)] : null;
            ProcessBindingReason? why = instance is null
                ? mapping.Unbound ?? ProcessBindingReason.NoOwner
                : ProcessGrouping.Unattributable(instance, by, services);
            string key = why is { } reason ? $"unattributed/{(int)reason:D3}" : ProcessGrouping.KeyOf(instance!, by, services);
            if (!tallies.TryGetValue(key, out (MetricGroup Group, List<(long Start, long Stop)> Spans, long Censored) tally))
            {
                MetricGroup group = why is { } unattributed
                    ? new() { Kind = MetricGroupKind.Unattributed, Reason = unattributed }
                    : by switch
                    {
                        LaneGrouping.Executable => new() { Kind = MetricGroupKind.Executable, Executable = instance!.ImagePath },
                        LaneGrouping.UserSession => new() { Kind = MetricGroupKind.UserSession, TerminalSession = instance!.SessionId },
                        LaneGrouping.Package => new() { Kind = MetricGroupKind.Package, Package = instance!.PackageFullName },
                        LaneGrouping.ServiceContainer => services!.ContainerOf(instance!.Id) is { } hosted
                            ? new() { Kind = MetricGroupKind.ServiceContainer, Process = instance, Services = hosted }
                            : new() { Kind = MetricGroupKind.Executable, Executable = instance.ImagePath },
                        _ => new() { Kind = MetricGroupKind.ProcessInstance, Process = instance },
                    };
                tally = (group, [], 0);
            }

            if (span is { } measured)
            {
                tally.Spans.Add(measured);
            }
            else
            {
                tally.Censored++;
            }

            tallies[key] = tally;
        }

        bool byStart = request.Cohort == OperationCohort.StartedInRange;
        DurationStatistic statistic = request.Statistic!.Value;
        ProcessInstanceId? focus = request.Focus?.Instance;
        var spans = new List<(long Start, long Stop)>();
        long censored = 0;
        long outsideProjection = 0;
        long outsideInterval = 0;
        long outsideProcess = 0;
        long unbound = 0;
        long images = 0;
        foreach (ResourceInstance section in index.Instances.Where(resource => resource.ByViews))
        {
            bool projected = (request.Mechanism is not { } mechanism || mechanism == section.Mechanism)
                && (request.Layer is not { } layer || layer == section.Layer);
            foreach (ResourceView mapping in section.Mappings)
            {
                // A mapping is in the cohort by its counted reading, its unmapping or its mapping; one the capture did not see
                // that reading of is no member of it.
                if ((byStart ? mapping.MappedNativeTicks : mapping.UnmappedNativeTicks) is not { } counted)
                {
                    continue;
                }

                if (!projected)
                {
                    outsideProjection++;
                    continue;
                }

                if (request.Interval is { } interval && !interval.Contains(counted))
                {
                    outsideInterval++;
                    continue;
                }

                if (focus is { } held && mapping.Holder != held)
                {
                    outsideProcess++;
                    unbound += mapping.Holder is null ? 1 : 0;
                    continue;
                }

                (long Start, long Stop)? span = mapping is { MappedNativeTicks: { } mapped, UnmappedNativeTicks: { } unmapped }
                    ? ((long)SourceClockMath.SessionNanoseconds(clock, mapped), (long)SourceClockMath.SessionNanoseconds(clock, unmapped))
                    : null;
                if (span is { } measured)
                {
                    spans.Add(measured);
                    images += section.Image ? 1 : 0;
                }
                else
                {
                    censored++;
                }

                if (grouping is { } by)
                {
                    Tally(mapping, span, by);
                }
            }
        }

        MetricDistribution? distribution = MetricDistribution.Of(spans);
        var caveats = new List<string>
        {
            "A mapping lifetime runs from a view's mapping to its unmapping by one process: how long the view stayed mapped, "
            + "never a byte moved through it (P27). A view's records pair by the view gate's number and the process they bind to.",
            byStart
                ? "Started in range: the mappings whose mapping is in scope."
                : "Completed in range: the mappings whose unmapping is in scope.",
        };
        if (censored > 0)
        {
            caveats.Add($"{CountText.Of(censored, "mapping")} in scope had an end the capture did not see - mapped before it, or "
                + "still mapped at its stop or its unmapping unseen - and " + CountText.Agree(censored, "is", "are")
                + " stated, never given a duration.");
        }

        if (images > 0)
        {
            caveats.Add($"{CountText.Of(images, "mapping")} measured {CountText.Agree(images, "is", "are")} an image's view: a "
                + "module loaded, not memory a process shared.");
        }

        if (unbound > 0)
        {
            caveats.Add($"{CountText.Of(unbound, "mapping")} left out bound to no process, so the focus could have held "
                + $"{CountText.Agree(unbound, "it", "them")}: disclosed, never measured.");
        }

        MetricResult answer = context.Answer(request) with
        {
            ResourceRule = ResourceInstanceIndex.Rule,
            Unit = MeasurementUnit.Nanoseconds,
            Value = distribution?.Of(statistic),
            Distribution = distribution,
            KnownContributions = spans.Count,
            UnknownContributions = censored,
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = outsideInterval,
            ExcludedByProcessFilter = outsideProcess,
            BindingRule = focus is null ? null : ProcessInstanceIndex.BindingRule,
            Caveats = caveats,
        };

        // Grouped, each group is its mappings' statistic, the longest first, and one whose every mapping had an end unseen is
        // unmeasured, after them; a median does not add, so the groups' values are not parts of the total's, and the rest past
        // the rows asked for is one answer over the union of their mappings, never a sum of statistics.
        if (grouping is not null)
        {
            List<(string Key, MetricGroup Group, List<(long Start, long Stop)> Spans)> built =
            [
                .. tallies.Select(entry =>
                {
                    MetricDistribution? lifetimes = MetricDistribution.Of(entry.Value.Spans);
                    return (entry.Key, entry.Value.Group with
                    {
                        Value = lifetimes?.Of(statistic),
                        KnownContributions = entry.Value.Spans.Count,
                        UnknownContributions = entry.Value.Censored,
                        Distribution = lifetimes,
                    }, entry.Value.Spans);
                }),
            ];
            List<(string Key, MetricGroup Group, List<(long Start, long Stop)> Spans)> attributed =
                [.. built.Where(entry => entry.Group.Kind != MetricGroupKind.Unattributed)];
            List<(string Key, MetricGroup Group, List<(long Start, long Stop)> Spans)> ordered =
            [
                .. attributed.Where(entry => entry.Group.Value is not null)
                    .OrderByDescending(entry => entry.Group.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select((entry, rank) => (entry.Key, entry.Group with { Rank = rank + 1 }, entry.Spans)),
                .. attributed.Where(entry => entry.Group.Value is null).OrderBy(entry => entry.Key, StringComparer.Ordinal),
            ];
            MetricGroup? remainder = null;
            if (request.RequestedRows is { } rows && ordered.Count > rows)
            {
                List<(string Key, MetricGroup Group, List<(long Start, long Stop)> Spans)> rest = [.. ordered.Skip(rows)];
                MetricDistribution? merged = MetricDistribution.Of([.. rest.SelectMany(entry => entry.Spans)]);
                remainder = new MetricGroup
                {
                    Kind = MetricGroupKind.Remainder,
                    Value = merged?.Of(statistic),
                    KnownContributions = rest.Sum(entry => entry.Group.KnownContributions),
                    UnknownContributions = rest.Sum(entry => entry.Group.UnknownContributions),
                    GroupsMerged = rest.Count,
                    Distribution = merged,
                };
                ordered = [.. ordered.Take(rows)];
            }

            List<MetricGroup> unattributed =
            [
                .. built.Where(entry => entry.Group.Kind == MetricGroupKind.Unattributed)
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Group),
            ];
            caveats.Add($"Groups are ranked by their {Words(statistic)}, the longest first. Each mapping is in exactly one group, "
                + "its holder's, but a median or a percentile does not add, so the groups' values are not parts of the total's.");
            long ungrouped = unattributed.Sum(group => group.KnownContributions + group.UnknownContributions);
            if (ungrouped > 0)
            {
                Dictionary<ProcessBindingReason, long> reasons = unattributed.ToDictionary(
                    group => group.Reason!.Value,
                    group => group.KnownContributions + group.UnknownContributions);
                caveats.Add($"{CountText.Of(ungrouped, "mapping")} {CountText.Agree(ungrouped, "is", "are")} no group's, by "
                    + $"reason ({Reasons(reasons)}): bound to no process, or held by one without the grouping's fact; each is "
                    + "still measured, never put in the nearest group (P6).");
            }

            answer = answer with
            {
                Groups = [.. ordered.Select(entry => entry.Group)],
                Remainder = remainder,
                Unattributed = unattributed,
                BindingRule = ProcessInstanceIndex.BindingRule,
            };
        }

        // A lifetime of nothing is not zero: with no mapping in scope measured, there is no value to report (R21).
        return distribution is not null
            ? answer
            : answer with
            {
                Value = null,
                Unit = null,
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = censored > 0
                    ? "Every mapping in scope had an end the capture did not see, so none was measured, which is not a lifetime "
                        + "of zero (R21)."
                    : "No mapping of a section's view is in scope, so no lifetime was measured, which is not a lifetime of zero (R21).",
            };
    }

    // The value a set of resources gives a group: how many they are; for a count of records, their membership and lifecycle
    // records in scope; or for a capacity, the largest extent each section's views mapped, summed once a section, those that
    // recorded none unknown - unmeasured where none is known.
    private static MetricGroup Measured(MetricGroup group, List<ResourceInstance> resources, Metric metric)
    {
        if (metric == Metric.Observations)
        {
            long records = resources.Sum(resource => resource.MembershipRecords);
            return group with { Value = records, KnownContributions = records, UnknownContributions = 0 };
        }

        if (metric != Metric.MappingCapacity)
        {
            return group with { Value = resources.Count, KnownContributions = resources.Count, UnknownContributions = 0 };
        }

        long sum = 0;
        long known = 0;
        long unsized = 0;
        foreach (ResourceInstance section in resources)
        {
            if (section is { ByViews: true, LargestView: { } extent })
            {
                sum = SegmentMeasurement.AddBytes(sum, extent);
                known++;
            }
            else
            {
                unsized++;
            }
        }

        return group with { Value = known > 0 ? sum : null, KnownContributions = known, UnknownContributions = unsized };
    }
}
