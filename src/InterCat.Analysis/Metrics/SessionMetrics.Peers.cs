using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// `ActivePeers`: how many distinct processes are at the other end from a process, under the relation rule. A peer
/// is a process instance - an identity, never a PID or an address and port pair (R22) - so a record whose other end
/// is unresolved names no peer. Such a record is an unknown contribution with its reason, the count is a lower bound
/// beside them, and a remote peer, which is not a process instance of this capture, is never counted.
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>The peers of one focused process: the distinct counterparts of the records its filter keeps.</summary>
    private static MetricResult ActivePeers(Context context, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        ProcessRoles roles = context.Roles!;
        ProcessFilter filter = context.Filter!;
        var peers = new HashSet<int>();
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
            bool[] kept = context.ProcessMasks[reader];
            SegmentRoles segmentRoles = roles.For(reader);
            for (int row = 0; row < reader.RowCount; row++)
            {
                // A lifecycle record has no other end: it is neither a peer nor an unknown one.
                if (!inScope[row] || !kept[row] || !segmentRoles.Communicates[row])
                {
                    continue;
                }

                ProcessBinding counterpart = filter.Counterpart(segmentRoles, row);
                if (counterpart.IsAdmittedUnder(request.EvidencePolicy))
                {
                    known++;
                    peers.Add(counterpart.Instance);
                    if (evidence.Count < context.EvidenceLimit)
                    {
                        evidence.Add(Evidence(name, reader, row));
                    }
                }
                else
                {
                    ProcessBindingReason reason = UnknownReason(counterpart);
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
        };

        // No resolved other end among records that have one is not "no peers": every such record may have one the
        // capture cannot name, so the count is unavailable rather than an observed zero (R21, P1).
        if (peers.Count == 0 && unknown > 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation =
                    $"None of the {unknown:N0} records in scope that have another end resolves it ("
                    + Reasons(unknownReasons) + "), so no peer can be named. They may involve remote processes, which "
                    + "are not instances of this capture, or local ones whose records it does not hold; zero would be "
                    + "a guess.",
                Unit = null,
            };
        }

        var caveats = new List<string> { PeersCaveat(request) };
        caveats.AddRange(FocusCaveats(request, context.UnresolvedCounterparts));
        if (unknown > 0)
        {
            caveats.Add(
                $"At least {peers.Count:N0}: {unknown:N0} records in scope have another end this session does not resolve "
                + $"({Reasons(unknownReasons)}), and each may add a peer it cannot name. A remote process is never "
                + "counted, because it is not a process instance of this capture.");
        }

        return answer with { Value = peers.Count, Caveats = caveats };
    }

    /// <summary>
    /// Each process's peers, ranked. A record between two resolved processes is a peer of each, so the groups overlap
    /// and do not add up to the total, which is the number of distinct processes with at least one peer.
    /// </summary>
    private static MetricResult GroupedPeers(Context context, LaneGrouping grouping, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
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

        // One tally per group key: an instance's index, or an executable's path folded case-insensitively.
        var tallies = new Dictionary<string, PeerTally>(StringComparer.Ordinal);
        var unattributed = new Dictionary<ProcessBindingReason, long>();
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
                if (ownerKnown && peerKnown && KeyOf(owner) is { } shared && shared == KeyOf(peer))
                {
                    // Both ends are in one group - a process connected to itself, or two instances of one executable:
                    // the record is one contribution to that group, and each end is a peer of it.
                    PeerTally tally = TallyFor(shared, owner);
                    tally.Known++;
                    tally.Peers.Add(peer.Instance);
                    tally.Peers.Add(owner.Instance);
                    tally.Bindings[peer.Strength] = tally.Bindings.GetValueOrDefault(peer.Strength) + 1;
                    continue;
                }

                if (ownerKnown)
                {
                    Record(owner, peerKnown ? peer : null, UnknownReason(peer));
                }

                if (peerKnown)
                {
                    Record(peer, ownerKnown ? owner : null, UnknownReason(owner));
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

        var built = tallies.Values
            .Select(tally => (Tally: tally, Group: tally.ToGroup()))
            .ToList();
        List<(PeerTally Tally, MetricGroup Group)> ranked =
        [
            .. built.Where(entry => entry.Group.Value is not null)
                .OrderByDescending(entry => entry.Group.Value)
                .ThenBy(entry => entry.Tally.StableKey, StringComparer.Ordinal),
        ];
        List<(PeerTally Tally, MetricGroup Group)> unmeasured =
        [
            .. built.Where(entry => entry.Group.Value is null)
                .OrderBy(entry => entry.Tally.StableKey, StringComparer.Ordinal),
        ];
        List<(PeerTally Tally, MetricGroup Group)> ordered =
        [
            .. ranked.Select((entry, index) => (entry.Tally, entry.Group with { Rank = index + 1 })),
            .. unmeasured,
        ];

        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            // The rest is one exact distinct count over the union of their peers, not a sum of overlapping counts.
            List<(PeerTally Tally, MetricGroup Group)> rest = [.. ordered.Skip(rows)];
            var restPeers = new HashSet<int>(rest.SelectMany(entry => entry.Tally.Peers));
            long restUnknown = rest.Sum(entry => entry.Group.UnknownContributions);
            remainder = new MetricGroup
            {
                Kind = MetricGroupKind.Remainder,
                Value = restPeers.Count > 0 || restUnknown == 0 ? restPeers.Count : null,
                KnownContributions = rest.Sum(entry => entry.Group.KnownContributions),
                UnknownContributions = restUnknown,
                GroupsMerged = rest.Count,
            };
            ordered = [.. ordered.Take(rows)];
        }

        var allPeers = new HashSet<int>(tallies.Values.SelectMany(tally => tally.Peers));
        long known = tallies.Values.Sum(tally => tally.Known);
        long unknown = tallies.Values.Sum(tally => tally.UnknownReasons.Values.Sum());
        var unknownReasons = new Dictionary<ProcessBindingReason, long>();
        foreach ((ProcessBindingReason reason, long count) in tallies.Values.SelectMany(tally => tally.UnknownReasons))
        {
            unknownReasons[reason] = unknownReasons.GetValueOrDefault(reason) + count;
        }

        var caveats = new List<string>
        {
            PeersCaveat(request),
            "A record between two resolved processes makes each the other's peer, so a process's peers are counted "
                + "in its own row and it is counted in its peers' rows: the rows overlap and do not add up to the "
                + "total, which is the number of distinct processes with at least one resolved peer.",
        };
        if (unknown > 0)
        {
            caveats.Add(
                $"Each count is a lower bound: {unknown:N0} of the groups' records have another end this session does "
                + $"not resolve ({Reasons(unknownReasons)}). A process whose records resolve no peer is unmeasured "
                + "rather than ranked at zero, and a remote process is never counted (R21).");
        }

        caveats.AddRange(GroupingCaveats(roles, request, []));
        return context.Answer(request) with
        {
            Value = allPeers.Count,
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

        PeerTally TallyFor(string key, ProcessBinding subject)
        {
            if (!tallies.TryGetValue(key, out PeerTally? tally))
            {
                ProcessInstance instance = processes.Instances[subject.Instance];
                tally = new(key, byExecutable ? null : instance, byExecutable ? instance.ImagePath : null);
                tallies[key] = tally;
            }

            return tally;
        }

        void Record(ProcessBinding subject, ProcessBinding? counterpart, ProcessBindingReason unknownReason)
        {
            if (KeyOf(subject) is not { } key)
            {
                unattributed[ProcessBindingReason.ExecutableUnknown] =
                    unattributed.GetValueOrDefault(ProcessBindingReason.ExecutableUnknown) + 1;
                return;
            }

            PeerTally tally = TallyFor(key, subject);
            if (counterpart is { } other)
            {
                tally.Known++;
                tally.Peers.Add(other.Instance);
                tally.Bindings[other.Strength] = tally.Bindings.GetValueOrDefault(other.Strength) + 1;
            }
            else
            {
                tally.UnknownReasons[unknownReason] = tally.UnknownReasons.GetValueOrDefault(unknownReason) + 1;
            }
        }
    }

    private static ProcessBindingReason UnknownReason(ProcessBinding binding) =>
        binding.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : binding.Reason;

    private static string Reasons(IReadOnlyDictionary<ProcessBindingReason, long> reasons) =>
        string.Join(", ", reasons.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key} {entry.Value:N0}"));

    private static string PeersCaveat(MetricRequest request) =>
        $"A peer is a process instance at the other end of a record, found by {TransportRelationIndex.RelationRule} "
        + $"under {request.EvidencePolicy}; a process connected to itself is its own peer, as a peer grouping lists it. "
        + "Peers are counted once however many records they share, and never from a PID or an address and port pair "
        + "(R22).";

    /// <summary>One group's peers while the rows are read.</summary>
    private sealed class PeerTally(string stableKey, ProcessInstance? process, string? executable)
    {
        public string StableKey { get; } = stableKey;

        public HashSet<int> Peers { get; } = [];

        public long Known { get; set; }

        public Dictionary<ProcessBindingReason, long> UnknownReasons { get; } = [];

        public Dictionary<RelationStrength, long> Bindings { get; } = [];

        /// <summary>The group a reader sees: unmeasured, not zero, when no record resolved a peer (R21).</summary>
        public MetricGroup ToGroup() => new()
        {
            Kind = process is null ? MetricGroupKind.Executable : MetricGroupKind.ProcessInstance,
            Process = process,
            Executable = executable,
            Value = Peers.Count > 0 ? Peers.Count : null,
            KnownContributions = Known,
            UnknownContributions = UnknownReasons.Values.Sum(),
            Bindings = Bindings,
        };
    }
}
