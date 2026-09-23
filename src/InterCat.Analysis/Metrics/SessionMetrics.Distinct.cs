using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// Distinct counts over relations: `ActivePeers`, the process instances at the other end from a process, and
/// `ActiveChannels`, the connection incarnations records belong to. Each counts instances - identities, never a PID or
/// an address and port pair (R22) - so a record that names none is an unknown contribution with its reason, and the
/// count is a lower bound beside those records rather than a total that silently omits them.
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>
    /// One distinct count: a focus's peers or channels, or every channel in scope. Only records with another end take
    /// part; a lifecycle record is neither counted nor unknown.
    /// </summary>
    private static MetricResult DistinctCount(Context context, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        bool peers = request.Metric == Metric.ActivePeers;
        ProcessRoles roles = context.Roles ?? RolesFor(
            context,
            ProcessInstanceIndex.Derive(
                [.. context.Segments.Select(segment => segment.Reader)],
                context.Clock!.Value,
                context.FieldSegments,
                cancellationToken),
            withRelations: true,
            cancellationToken);
        ProcessFilter? filter = context.Filter;
        var counted = new HashSet<int>();
        var unknownReasons = new Dictionary<ProcessBindingReason, long>();
        var evidence = new List<MetricEvidence>();
        long known = 0;
        long outsideProjection = 0;
        long outsideProcess = 0;
        long outsideInterval = 0;
        foreach ((string name, SegmentReaderV1 reader) in context.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservationCount scope = SegmentMeasurement.CountObservations(
                reader, request.Layer, request.Mechanism, request.Interval, processMask: context.ProcessMask(reader));
            outsideProjection += scope.ExcludedByProjection;
            outsideProcess += scope.ExcludedByProcessFilter;
            outsideInterval += scope.ExcludedOutsideInterval;

            bool[] inScope = SegmentMeasurement.RowsInScope(reader, request.Layer, request.Mechanism, request.Interval);
            bool[]? kept = context.ProcessMasks.GetValueOrDefault(reader);
            SegmentRoles segmentRoles = roles.For(reader);
            ChannelBinding[]? channels = peers ? null : roles.Relations!.ChannelsOf(reader);
            for (int row = 0; row < reader.RowCount; row++)
            {
                if (!inScope[row] || (kept is not null && !kept[row]) || !segmentRoles.Communicates[row])
                {
                    continue;
                }

                (int? element, ProcessBindingReason reason) = peers
                    ? PeerElement(filter!.Counterpart(segmentRoles, row), request.EvidencePolicy)
                    : ChannelElement(channels![row]);
                if (element is { } counts)
                {
                    known++;
                    counted.Add(counts);
                    if (evidence.Count < context.EvidenceLimit)
                    {
                        evidence.Add(Evidence(name, reader, row));
                    }
                }
                else
                {
                    unknownReasons[reason] = unknownReasons.GetValueOrDefault(reason) + 1;
                }
            }
        }

        long unknown = unknownReasons.Values.Sum();
        MetricResult answer = context.Answer(request) with
        {
            Unit = MeasurementUnit.Count,
            KnownContributions = known,
            UnknownContributions = unknown,
            UnknownCounterparts = unknownReasons,
            ExcludedByProjection = outsideProjection,
            ExcludedByProcessFilter = outsideProcess,
            ExcludedOutsideInterval = outsideInterval,
            Evidence = evidence,
            RelationRule = TransportRelationIndex.RelationRule,
        };

        // Nothing identified among records that have another end is not "none": each of them may belong to an instance
        // the capture cannot name, so the count is unavailable rather than an observed zero (R21, P1).
        string noun = peers ? "peer" : "channel";
        if (counted.Count == 0 && unknown > 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation =
                    $"None of the {unknown:N0} records in scope that have another end identifies a {noun} ("
                    + Reasons(unknownReasons) + "), so none can be counted. Zero would be a guess about what the "
                    + "capture could not resolve.",
                Unit = null,
            };
        }

        var caveats = new List<string> { peers ? PeersCaveat(request) : ChannelsCaveat() };
        caveats.AddRange(FocusCaveats(request, context.UnresolvedCounterparts));
        if (unknown > 0)
        {
            caveats.Add(
                $"At least {counted.Count:N0}: {unknown:N0} records in scope identify no {noun} ({Reasons(unknownReasons)}), "
                + $"and each may add one this session cannot name. "
                + (peers
                    ? "A remote process is never counted, because it is not a process instance of this capture."
                    : "A record of a mechanism no relation rule covers belongs to a channel nothing here identifies."));
        }

        return answer with { Value = counted.Count, Caveats = caveats };
    }

    /// <summary>
    /// Each process's peers or channels, ranked. A record between two resolved processes counts for each, so the groups
    /// overlap and do not add up to the total, which says so.
    /// </summary>
    private static MetricResult GroupedDistinct(Context context, LaneGrouping grouping, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        bool peers = request.Metric == Metric.ActivePeers;
        EvidencePolicy policy = request.EvidencePolicy;
        ProcessRoles roles = context.Roles ?? RolesFor(
            context,
            ProcessInstanceIndex.Derive(
                [.. context.Segments.Select(segment => segment.Reader)],
                context.Clock!.Value,
                context.FieldSegments,
                cancellationToken),
            withRelations: true,
            cancellationToken);
        ProcessInstanceIndex processes = roles.Processes;
        bool byExecutable = grouping == LaneGrouping.Executable;
        if (byExecutable && !processes.Instances.Any(instance => !string.IsNullOrWhiteSpace(instance.ImagePath)))
        {
            return Unavailable(
                request,
                context.Generation,
                MetricUnavailableReason.GroupingNotDerived,
                "No process lifecycle record in this generation carries a full image name. An executable cannot be "
                + "identified from a PID or an exit basename; import evidence with admitted process image names.");
        }

        // One tally per group key: an instance's identity, or an executable's path folded case-insensitively.
        var tallies = new Dictionary<string, DistinctTally>(StringComparer.Ordinal);
        var unattributed = new Dictionary<ProcessBindingReason, long>();
        var everything = new HashSet<int>();
        long outsideProjection = 0;
        long outsideInterval = 0;
        foreach ((string _, SegmentReaderV1 reader) in context.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservationCount scope = SegmentMeasurement.CountObservations(reader, request.Layer, request.Mechanism, request.Interval);
            outsideProjection += scope.ExcludedByProjection;
            outsideInterval += scope.ExcludedOutsideInterval;
            bool[] inScope = SegmentMeasurement.RowsInScope(reader, request.Layer, request.Mechanism, request.Interval);
            SegmentRoles segmentRoles = roles.For(reader);
            ChannelBinding[]? channels = peers ? null : roles.Relations!.ChannelsOf(reader);
            for (int row = 0; row < reader.RowCount; row++)
            {
                if (!inScope[row] || !segmentRoles.Communicates[row])
                {
                    continue;
                }

                ProcessBinding owner = segmentRoles.Owner(row);
                ProcessBinding peer = segmentRoles.Peer(row);
                bool ownerKnown = owner.IsAdmittedUnder(policy);
                bool peerKnown = peer.IsAdmittedUnder(policy);

                // What the row adds to its owner's group and to its peer's: the process at the other end, or the one
                // channel both ends share.
                (int? ForOwner, ProcessBindingReason OwnerUnknown, int? ForPeer, ProcessBindingReason PeerUnknown) adds = peers
                    ? (peerKnown ? peer.Instance : null, UnknownReason(peer), ownerKnown ? owner.Instance : null, UnknownReason(owner))
                    : channels![row] is { IsKnown: true } channel
                        ? (channel.Channel, ProcessBindingReason.Bound, channel.Channel, ProcessBindingReason.Bound)
                        : (null, channels[row].Reason, null, channels[row].Reason);
                if (!peers && channels![row].IsKnown)
                {
                    everything.Add(channels[row].Channel);
                }

                if (ownerKnown && peerKnown && KeyOf(owner) is { } shared && shared == KeyOf(peer))
                {
                    // Both ends are in one group - a process connected to itself, or two instances of one executable:
                    // the record is one contribution to that group, with everything it adds.
                    DistinctTally tally = TallyFor(shared, owner);
                    tally.Known++;
                    tally.Elements.Add(adds.ForOwner!.Value);
                    tally.Elements.Add(adds.ForPeer!.Value);
                    if (peers)
                    {
                        tally.Bindings[peer.Strength] = tally.Bindings.GetValueOrDefault(peer.Strength) + 1;
                    }

                    continue;
                }

                if (ownerKnown)
                {
                    Record(owner, adds.ForOwner, adds.OwnerUnknown, peers ? peer.Strength : null);
                }

                if (peerKnown)
                {
                    Record(peer, adds.ForPeer, adds.PeerUnknown, peers ? owner.Strength : null);
                }

                if (!ownerKnown && !peerKnown)
                {
                    // Neither end is a process this answer can name, so the record belongs to no group; it is
                    // reported by why its own process is unknown.
                    ProcessBindingReason reason = UnknownReason(owner);
                    unattributed[reason] = unattributed.GetValueOrDefault(reason) + 1;
                }
            }
        }

        List<(DistinctTally Tally, MetricGroup Group)> built = [.. tallies.Values.Select(tally => (tally, tally.ToGroup()))];
        List<(DistinctTally Tally, MetricGroup Group)> ordered =
        [
            .. built.Where(entry => entry.Group.Value is not null)
                .OrderByDescending(entry => entry.Group.Value)
                .ThenBy(entry => entry.Tally.StableKey, StringComparer.Ordinal)
                .Select((entry, index) => (entry.Tally, entry.Group with { Rank = index + 1 })),
            .. built.Where(entry => entry.Group.Value is null)
                .OrderBy(entry => entry.Tally.StableKey, StringComparer.Ordinal),
        ];

        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            // The rest is one exact distinct count over the union of what they count, not a sum of overlapping counts.
            List<(DistinctTally Tally, MetricGroup Group)> rest = [.. ordered.Skip(rows)];
            var restElements = new HashSet<int>(rest.SelectMany(entry => entry.Tally.Elements));
            long restUnknown = rest.Sum(entry => entry.Group.UnknownContributions);
            remainder = new MetricGroup
            {
                Kind = MetricGroupKind.Remainder,
                Value = restElements.Count > 0 || restUnknown == 0 ? restElements.Count : null,
                KnownContributions = rest.Sum(entry => entry.Group.KnownContributions),
                UnknownContributions = restUnknown,
                GroupsMerged = rest.Count,
            };
            ordered = [.. ordered.Take(rows)];
        }

        if (peers)
        {
            everything.UnionWith(tallies.Values.SelectMany(tally => tally.Elements));
        }

        long known = tallies.Values.Sum(tally => tally.Known);
        var unknownReasons = new Dictionary<ProcessBindingReason, long>();
        foreach ((ProcessBindingReason reason, long count) in tallies.Values.SelectMany(tally => tally.UnknownReasons))
        {
            unknownReasons[reason] = unknownReasons.GetValueOrDefault(reason) + count;
        }

        long unknown = unknownReasons.Values.Sum();
        var caveats = new List<string>
        {
            peers ? PeersCaveat(request) : ChannelsCaveat(),
            peers
                ? "A record between two resolved processes makes each the other's peer, so a process's peers are counted "
                    + "in its own row and it is counted in its peers' rows: the rows overlap and do not add up to the "
                    + "total, which is the number of distinct processes with at least one resolved peer."
                : "A channel between two processes counts in both of their rows, so the rows overlap and do not add up to "
                    + "the total, which is the number of distinct channels in scope, whoever holds them.",
        };
        if (unknown > 0)
        {
            caveats.Add(
                $"Each count is a lower bound: {unknown:N0} of the groups' records identify no {(peers ? "peer" : "channel")} "
                + $"({Reasons(unknownReasons)}). A process whose records identify none is unmeasured rather than ranked "
                + "at zero (R21).");
        }

        caveats.AddRange(GroupingCaveats(roles, request, []));
        return context.Answer(request) with
        {
            Value = everything.Count,
            Unit = MeasurementUnit.Count,
            KnownContributions = known,
            UnknownContributions = unknown,
            UnknownCounterparts = unknownReasons,
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = outsideInterval,
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
            RelationRule = TransportRelationIndex.RelationRule,
            Caveats = caveats,
        };

        // The group a bound process belongs to: its instance, or its witnessed image path. An instance with no path
        // has no executable group.
        string? KeyOf(ProcessBinding subject)
        {
            ProcessInstance instance = processes.Instances[subject.Instance];
            return !byExecutable
                ? instance.Id.ToString()
                : string.IsNullOrWhiteSpace(instance.ImagePath) ? null : instance.ImagePath.ToUpperInvariant();
        }

        DistinctTally TallyFor(string key, ProcessBinding subject)
        {
            if (!tallies.TryGetValue(key, out DistinctTally? tally))
            {
                ProcessInstance instance = processes.Instances[subject.Instance];
                tally = new(key, byExecutable ? null : instance, byExecutable ? instance.ImagePath : null);
                tallies[key] = tally;
            }

            return tally;
        }

        void Record(ProcessBinding subject, int? element, ProcessBindingReason unknownReason, RelationStrength? strength)
        {
            if (KeyOf(subject) is not { } key)
            {
                unattributed[ProcessBindingReason.ExecutableUnknown] =
                    unattributed.GetValueOrDefault(ProcessBindingReason.ExecutableUnknown) + 1;
                return;
            }

            DistinctTally tally = TallyFor(key, subject);
            if (element is { } counted)
            {
                tally.Known++;
                tally.Elements.Add(counted);
                if (strength is { } bound)
                {
                    tally.Bindings[bound] = tally.Bindings.GetValueOrDefault(bound) + 1;
                }
            }
            else
            {
                tally.UnknownReasons[unknownReason] = tally.UnknownReasons.GetValueOrDefault(unknownReason) + 1;
            }
        }
    }

    private static (int? Element, ProcessBindingReason Reason) PeerElement(ProcessBinding counterpart, EvidencePolicy policy) =>
        counterpart.IsAdmittedUnder(policy) ? (counterpart.Instance, ProcessBindingReason.Bound) : (null, UnknownReason(counterpart));

    private static (int? Element, ProcessBindingReason Reason) ChannelElement(ChannelBinding channel) =>
        channel.IsKnown ? (channel.Channel, ProcessBindingReason.Bound) : (null, channel.Reason);

    private static ProcessBindingReason UnknownReason(ProcessBinding binding) =>
        binding.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : binding.Reason;

    private static string Reasons(IReadOnlyDictionary<ProcessBindingReason, long> reasons) =>
        string.Join(", ", reasons.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value:N0}"));

    private static string PeersCaveat(MetricRequest request) =>
        $"A peer is a process instance at the other end of a record, found by {TransportRelationIndex.RelationRule} "
        + $"under {request.EvidencePolicy}; a process connected to itself is its own peer, as a peer grouping lists it. "
        + "Peers are counted once however many records they share, and never from a PID or an address and port pair "
        + "(R22).";

    private static string ChannelsCaveat() =>
        $"A channel is a connection incarnation under {TransportRelationIndex.RelationRule}: the two ends of one "
        + "connection are one channel, a port reused by a later connection is another, and a connection whose other end "
        + "no record holds is a one-sided channel. Channels are never counted from address and port pairs alone (R22); "
        + "where the capture lost a connect, accept or disconnect, two connections on one port are one channel here.";

    /// <summary>One group's counted instances while the rows are read.</summary>
    private sealed class DistinctTally(string stableKey, ProcessInstance? process, string? executable)
    {
        public string StableKey { get; } = stableKey;

        public HashSet<int> Elements { get; } = [];

        public long Known { get; set; }

        public Dictionary<ProcessBindingReason, long> UnknownReasons { get; } = [];

        /// <summary>For peers: how strongly each counted record's other end is bound.</summary>
        public Dictionary<RelationStrength, long> Bindings { get; } = [];

        /// <summary>The group a reader sees: unmeasured, not zero, when no record identified anything (R21).</summary>
        public MetricGroup ToGroup() => new()
        {
            Kind = process is null ? MetricGroupKind.Executable : MetricGroupKind.ProcessInstance,
            Process = process,
            Executable = executable,
            Value = Elements.Count > 0 ? Elements.Count : null,
            KnownContributions = Known,
            UnknownContributions = UnknownReasons.Values.Sum(),
            Bindings = Bindings,
        };
    }
}
