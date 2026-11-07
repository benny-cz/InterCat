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
/// of the processes sharing one reads its holders, and a capacity the largest extent its views mapped. None of it is
/// traffic: a handle or a view proves who held a resource, never what moved through it (P27).
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
            Metric.Observations =>
                "Counting resources' membership and lifecycle records is not defined at this version: a resource is counted "
                + "once, over its known lifetime, never by its records. The source-observations basis counts the records "
                + "themselves.",
            Metric.Duration =>
                "A mapping lifetime is not measured at this version: a section's views are counted and the largest extent one "
                + "mapped is given, but no view's mapping to its unmapping is a duration yet.",
            _ => null,
        };
        missing ??= request.Grouping is not null
            ? "A grouped topology answer is not defined at this version: resources are counted in total or for one process "
                + "focus. icat resources lists each resource with the processes holding it."
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

    /// <summary>Counts the resources a topology request takes, the processes sharing them, or their capacity (§8b).</summary>
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

        ResourceInstanceIndex index = ResourceInstanceIndex.Derive(
            [.. context.Segments.Select(segment => segment.Reader)],
            context.FieldSegments,
            processes,
            request.EvidencePolicy,
            cancellationToken);

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
            "Each resource is one instance resource-instances-v3 derives, counted once over its known lifetime, never by its "
            + "records: a handle or a view proves which processes held it, never traffic through it (P27). A section found by "
            + "its handles and by its views is two resources, as no record joins them (ADR-092).",
        };
        if (request.Interval is not null)
        {
            caveats.Add("A resource is in scope where its known lifetime meets the interval: from its first record to its last, "
                + "open before the first where it was made before the capture and after the last where the stop's rundown held it.");
        }

        if (unresolvedResources > 0)
        {
            caveats.Add($"{CountText.Of(unresolvedResources, "resource")} in scope {CountText.Agree(unresolvedResources, "has", "have")} "
                + $"records bound to no process ({Reasons(unresolved)}), so the process filter could hold "
                + $"{CountText.Agree(unresolvedResources, "it", "them")} unseen: disclosed, never counted.");
        }

        if (context.EvidenceLimit > 0)
        {
            caveats.Add("A topology answer counts resources, not records, so it lists no record. icat resources lists each "
                + "resource with the processes holding it and its ends.");
        }

        return request.Metric switch
        {
            Metric.ActiveChannels => ResourceCount(answer, taken, caveats),
            Metric.ActivePeers => MembershipPeers(answer, taken, request.Focus!.Value.Instance, caveats),
            Metric.MappingCapacity => Capacity(answer, taken, caveats),
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
}
