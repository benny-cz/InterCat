using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// Grouped answers. A group's value is computed exactly as an ungrouped total is — the same scope, the same domain,
/// the same accounting — over the records that belong to it, and every record in scope belongs to exactly one group
/// or to one stated reason it could not be attributed. So the groups partition the total, and a result says so
/// rather than leaving a caller to add rows that might overlap (§3.2, §5.1).
/// </summary>
public static partial class SessionMetrics
{
    /// <summary>What a grouping needs that this session may not have. Null when it can be answered.</summary>
    private static MetricResult? WhatGroupingNeeds(MetricRequest request, long generation, SourceClockDescriptor? clock)
    {
        switch (request.Grouping)
        {
            case LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.Peer:
                // A cross-side total groups each record under the process at its other end, found through the relation
                // rule; a record whose other end it does not resolve is unattributed with the reason, never guessed.
                return clock is null
                    ? Unavailable(
                        request,
                        generation,
                        MetricUnavailableReason.NoEntityBindings,
                        "A process instance is identified within the host and boot its capture's clock scopes, and this "
                        + "session does not describe its clock, so no instance can be keyed (identity-v1).")
                    : null;
            case LaneGrouping.Mechanism:
                return null;
            default:
                return Unavailable(
                    request,
                    generation,
                    MetricUnavailableReason.GroupingNotDerived,
                    request.Grouping switch
                    {
                        LaneGrouping.Endpoint =>
                            "Grouping by endpoint needs endpoint instances with their lifetimes, and this session derives "
                            + "none. Grouping address and port pairs directly would merge reused ports (R22).",
                        _ => $"Grouping by {request.Grouping} needs a derivation this session does not have yet.",
                    });
        }
    }

    private static MetricResult Grouped(Context context, LaneGrouping grouping, CancellationToken cancellationToken)
    {
        MetricRequest request = context.Request;
        Metric effective = request.Metric == Metric.Rate ? request.RateNumerator!.Value : request.Metric;
        bool isCount = effective == Metric.Observations;
        ProcessRoles? roles = grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.Peer
            ? context.Roles ?? RolesFor(
                context,
                ProcessInstanceIndex.Derive(
                    [.. context.Segments.Select(segment => segment.Reader)],
                    context.Clock!.Value,
                    context.FieldSegments,
                    cancellationToken),
                NeedsRelations(request),
                cancellationToken)
            : null;
        ProcessInstanceIndex? processes = roles?.Processes;
        if (grouping == LaneGrouping.Executable && !processes!.Instances.Any(instance => !string.IsNullOrWhiteSpace(instance.ImagePath)))
        {
            return Unavailable(
                request,
                context.Generation,
                MetricUnavailableReason.GroupingNotDerived,
                "No process lifecycle record in this generation carries a full image name. An executable cannot be "
                + "identified from a PID or an exit basename; import evidence with admitted process image names.");
        }
        GroupLayout layout = roles is null
            ? new MechanismLayout()
            : new ProcessLayout(
                roles.Processes,
                request.EvidencePolicy,
                byExecutable: grouping == LaneGrouping.Executable,
                segment => AttributedBindings(context, roles, segment, grouping, effective));
        IReadOnlyList<AccountingSide> taken = isCount ? [] : MetricCompatibility.RowSidesTakenBy(request.AccountingSide!.Value);

        var totals = new SideTotal?[layout.Count, (int)AccountingSide.CanonicalOwner + 1];
        long[] counts = new long[layout.Count];
        long otherDomain = 0;
        long noSlot = 0;
        long outsideProjection = 0;
        long outsideProcess = 0;
        long outsideInterval = 0;
        MeasurementUnit? unit = null;
        foreach ((string _, SegmentReaderV1 reader) in context.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] groups = layout.Assign(reader);
            ReadOnlySpan<bool> processMask = context.ProcessMask(reader);
            if (isCount)
            {
                long[] segmentCounts = SegmentMeasurement.CountObservationsByGroup(
                    reader,
                    request.Layer,
                    request.Mechanism,
                    request.Interval,
                    groups,
                    layout.Count,
                    processMask);
                for (int group = 0; group < layout.Count; group++)
                {
                    counts[group] += segmentCounts[group];
                }

                ObservationCount scope = SegmentMeasurement.CountObservations(
                    reader, request.Layer, request.Mechanism, request.Interval, processMask: processMask);
                outsideProjection += scope.ExcludedByProjection;
                outsideProcess += scope.ExcludedByProcessFilter;
                outsideInterval += scope.ExcludedOutsideInterval;
                continue;
            }

            GroupedDomainMeasurement measured = SegmentMeasurement.MeasureDomainByGroup(
                reader,
                new()
                {
                    Domain = request.ByteDomain!.Value,
                    Layer = request.Layer,
                    Mechanism = request.Mechanism,
                    Interval = request.Interval,
                },
                groups,
                layout.Count,
                processMask);
            if (measured.Unit is { } segmentUnit)
            {
                if (unit is { } established && established != segmentUnit)
                {
                    throw new InvalidDataException(
                        $"Segments of generation {context.Generation} measure {request.ByteDomain} in {established} and "
                        + $"{segmentUnit}. Two units are not summed and are not converted here.");
                }

                unit = segmentUnit;
            }

            for (int group = 0; group < layout.Count; group++)
            {
                foreach (SideMeasurement side in measured.Groups[group])
                {
                    (totals[group, (int)side.Side] ??= new()).Add(side);
                }
            }

            otherDomain += measured.ExcludedOtherDomain;
            noSlot += measured.ExcludedNoDeclaredSlot;
            outsideProjection += measured.ExcludedByProjection;
            outsideProcess += measured.ExcludedByProcessFilter;
            outsideInterval += measured.ExcludedOutsideInterval;
        }

        // Fold the layout's slots into the groups a reader sees: a process instance is one group whatever strength
        // each of its records was bound with, and the strengths are kept beside it rather than lost.
        var built = new List<GroupAccumulator>();
        foreach (GroupDefinition definition in layout.Groups)
        {
            var accumulator = new GroupAccumulator(definition);
            foreach ((int slot, RelationStrength? strength) in definition.Slots)
            {
                long records = 0;
                if (isCount)
                {
                    records = counts[slot];
                    accumulator.Count += counts[slot];
                }
                else
                {
                    for (int side = 1; side <= (int)AccountingSide.CanonicalOwner; side++)
                    {
                        if (totals[slot, side] is not { } total)
                        {
                            continue;
                        }

                        SideMeasurement measurement = total.ToMeasurement((AccountingSide)side);
                        accumulator.Add(measurement);
                        if (taken.Contains(measurement.Side))
                        {
                            records += measurement.DeclaredContributions;
                        }
                    }
                }

                if (strength is { } bound && records > 0)
                {
                    accumulator.Bindings[bound] = (accumulator.Bindings.TryGetValue(bound, out long already) ? already : 0) + records;
                }
            }

            built.Add(accumulator);
        }

        var groupsOut = new List<(MetricGroup Group, string StableKey)>();
        var unattributed = new List<MetricGroup>();
        long totalValue = 0;
        long totalKnown = 0;
        long totalUnknown = 0;
        var allSides = new SortedDictionary<AccountingSide, SideTotal>();
        foreach (GroupAccumulator accumulator in built)
        {
            // The breakdown by side covers every group, listed or not: a group with nothing the accounting takes is
            // left out of the ranking, and its contributions on other sides are still what the total left out.
            foreach (SideMeasurement side in accumulator.Measured)
            {
                if (!allSides.TryGetValue(side.Side, out SideTotal? running))
                {
                    running = new();
                    allSides[side.Side] = running;
                }

                running.Add(side);
            }

            MetricGroup? group = accumulator.ToGroup(isCount, taken, context, request);
            if (group is null)
            {
                continue;
            }

            totalValue = checked(totalValue + (group.Value ?? 0));
            totalKnown += group.KnownContributions;
            totalUnknown += group.UnknownContributions;
            if (group.Kind == MetricGroupKind.Unattributed)
            {
                unattributed.Add(group);
            }
            else
            {
                groupsOut.Add((group, accumulator.Definition.StableKey));
            }
        }

        // Ranked by value, ties by a stable identity so a refresh never reorders equal rows; groups with nothing
        // measured sort in their own section rather than below a measured zero (§5.2).
        List<(MetricGroup Group, string StableKey)> ranked =
        [
            .. groupsOut
                .Where(entry => entry.Group.Value is not null)
                .OrderByDescending(entry => entry.Group.Value)
                .ThenBy(entry => entry.StableKey, StringComparer.Ordinal),
        ];
        List<(MetricGroup Group, string StableKey)> unmeasured =
        [
            .. groupsOut
                .Where(entry => entry.Group.Value is null)
                .OrderBy(entry => entry.StableKey, StringComparer.Ordinal),
        ];
        List<MetricGroup> ordered =
        [
            .. ranked.Select((entry, index) => entry.Group with { Rank = index + 1 }),
            .. unmeasured.Select(entry => entry.Group),
        ];

        MetricGroup? remainder = null;
        if (request.RequestedRows is { } rows && ordered.Count > rows)
        {
            remainder = Remainder(ordered.Skip(rows).ToList(), isCount, context, request);
            ordered = ordered.Take(rows).ToList();
        }

        IReadOnlyList<SideMeasurement> sides = [.. allSides.Select(entry => entry.Value.ToMeasurement(entry.Key))];
        MetricResult answer = context.Answer(request) with
        {
            Unit = isCount ? MeasurementUnit.Count : unit ?? MeasurementUnit.Bytes,
            TakenSides = taken,
            Sides = sides,
            KnownContributions = isCount ? totalValue : totalKnown,
            UnknownContributions = totalUnknown,
            ExcludedOtherDomain = otherDomain,
            ExcludedOtherSide = sides.Where(side => !taken.Contains(side.Side)).Sum(side => side.DeclaredContributions),
            ExcludedNoDeclaredSlot = noSlot,
            ExcludedByProjection = outsideProjection,
            ExcludedByProcessFilter = outsideProcess,
            ExcludedOutsideInterval = outsideInterval,
            Groups = ordered,
            Remainder = remainder,
            Unattributed = unattributed,
            GroupsPartitionTotal = true,
            BindingRule = processes is null && request.Focus is null ? null : ProcessInstanceIndex.BindingRule,
            RelationRule = (roles ?? context.Roles)?.Relations is null ? null : TransportRelationIndex.RelationRule,
        };

        if (!isCount && totalKnown == 0)
        {
            return answer with
            {
                Unavailable = MetricUnavailableReason.NothingMeasured,
                UnavailableExplanation = NothingMeasured(request with { Metric = effective }, answer),
                Unit = null,
            };
        }

        var caveats = new List<string>();
        if (!isCount)
        {
            caveats.AddRange(ByteCaveats(request with { Metric = effective }, answer));
        }
        else
        {
            caveats.AddRange(FocusCaveats(request, answer.UnresolvedCounterparts));
        }

        caveats.RemoveAll(caveat => caveat.StartsWith("This total spans every process", StringComparison.Ordinal));
        caveats.AddRange(GroupingCaveats(roles, request, unattributed));
        MetricRate? rate = request.Metric == Metric.Rate
            ? RateOf(totalValue, isCount ? MeasurementUnit.Count : unit ?? MeasurementUnit.Bytes, context)
            : null;
        if (rate is not null)
        {
            caveats.Add(
                "The denominator is the whole selected interval, the same for every group. A rate is never divided by "
                + "a shorter healthy-looking part of it (§19.2).");
        }

        return answer with
        {
            Value = rate is null ? totalValue : null,
            Rate = rate,
            Unit = rate?.Unit ?? answer.Unit,
            Caveats = caveats,
        };
    }

    private static MetricRate? RateOf(long numerator, MeasurementUnit unit, Context context) =>
        context.Request.Interval is { } interval
            ? new()
            {
                Numerator = numerator,
                NumeratorUnit = unit,
                IntervalTicks = interval.SpanTicks,
                TicksPerSecond = context.Clock?.TicksPerSecond,
            }
            : null;

    private static MetricGroup Remainder(
        List<MetricGroup> rest,
        bool isCount,
        Context context,
        MetricRequest request)
    {
        long known = rest.Sum(group => group.KnownContributions);
        long unknown = rest.Sum(group => group.UnknownContributions);
        bool measured = isCount || known > 0;
        long value = rest.Sum(group => group.Value ?? 0);
        return new()
        {
            Kind = MetricGroupKind.Remainder,
            Value = measured ? value : null,
            KnownContributions = known,
            UnknownContributions = unknown,
            GroupsMerged = rest.Count,
            Rate = request.Metric == Metric.Rate && measured
                ? RateOf(value, isCount ? MeasurementUnit.Count : MeasurementUnit.Bytes, context)
                : null,
        };
    }

    private static List<string> GroupingCaveats(
        ProcessRoles? roles,
        MetricRequest request,
        IReadOnlyList<MetricGroup> unattributed)
    {
        var caveats = new List<string>();
        if (roles is null)
        {
            return caveats;
        }

        caveats.Add(
            $"Records are bound to process instances by {ProcessInstanceIndex.BindingRule}: a record names its owner in "
            + "its own payload, and binds to the instance of that PID whose witnessed lifetime holds its reading. The "
            + "event header's process is never used (§4.1). Start keys distinguish lifecycle instances when present; "
            + "records without a key still rely on complete lifecycle evidence.");
        MetricGroup? notAdmitted = unattributed.FirstOrDefault(group => group.Reason == ProcessBindingReason.NotAdmittedByPolicy);
        if (notAdmitted is not null && request.EvidencePolicy < EvidencePolicy.IncludeCandidates)
        {
            caveats.Add(
                $"{notAdmitted.KnownContributions + notAdmitted.UnknownContributions:N0} contributions lie in the lifetime "
                + "of a later instance of a PID this capture reused. PID and time cannot tell a late record of the "
                + "earlier instance from a record of the later one, so they are candidates and stay unattributed under "
                + "this evidence policy; ask for candidates to include them, labelled (identity-v1).");
        }

        if (unattributed.Any(group => group.Reason is ProcessBindingReason.BeforeFirstEvidence
            or ProcessBindingReason.BetweenInstances or ProcessBindingReason.AfterExit))
        {
            caveats.Add(
                "Some contributions name a PID at a moment no witnessed instance held it - before its first lifecycle "
                + "record, between one instance's exit and the next one's creation, or after its last exit. They are "
                + "reported by that reason and never attributed to the nearest instance (P6).");
        }

        if (roles.Relations is not null)
        {
            caveats.Add(
                request.Grouping == LaneGrouping.Peer
                    ? $"Each record is grouped under the process at its other end from instance {request.Focus!.Value.Instance}, "
                        + $"found by {TransportRelationIndex.RelationRule}: the record holding the mirrored endpoint pair "
                        + "is the other end of the same connection, and every record there binds to one instance."
                    : $"A {(request.Metric == Metric.BytesSent || request.RateNumerator == Metric.BytesSent ? "sent" : "received")} "
                        + "total groups each record under the process the data left or reached. A record made at the other "
                        + $"end is grouped through {TransportRelationIndex.RelationRule}, which finds the process holding "
                        + "the mirrored endpoint pair.");
        }

        if (unattributed.Any(group => group.Reason is ProcessBindingReason.PeerNotObserved
            or ProcessBindingReason.PeerAmbiguous or ProcessBindingReason.PeerUnbound
            or ProcessBindingReason.PeerEndpointIncomplete or ProcessBindingReason.NoRelationRule))
        {
            caveats.Add(
                "Some contributions belong to the process at a record's other end, and that end is not resolved: no "
                + "record in this capture holds it, more than one process holds it, its records bind to no instance, the "
                + "record carries no complete endpoint pair, or no relation rule covers its mechanism. They are reported "
                + "by that reason and never attributed to a guessed peer (P6).");
        }

        return caveats;
    }

    /// <summary>
    /// The process each row of a segment belongs to under a grouping. A peer grouping takes the process at the other end
    /// from the focus. A sent total takes the process the data left - a receive record's other end - and a received total
    /// the process it reached - a send record's other end; any other total takes the process that made the record
    /// (`contracts/metrics-v1.md` §6).
    /// </summary>
    private static ProcessBinding[] AttributedBindings(
        Context context,
        ProcessRoles roles,
        SegmentReaderV1 segment,
        LaneGrouping grouping,
        Metric effective)
    {
        SegmentRoles segmentRoles = roles.For(segment);
        var bindings = new ProcessBinding[segment.RowCount];
        for (int row = 0; row < bindings.Length; row++)
        {
            bindings[row] = grouping == LaneGrouping.Peer
                ? context.Filter!.Counterpart(segmentRoles, row)
                : effective switch
                {
                    Metric.BytesSent when segmentRoles.Directions[row] < 0 => segmentRoles.Peer(row),
                    Metric.BytesReceived when segmentRoles.Directions[row] > 0 => segmentRoles.Peer(row),
                    _ => segmentRoles.Owner(row),
                };
        }

        return bindings;
    }

    /// <summary>A grouping's slots and the groups they fold into. A slot is what one row is assigned to.</summary>
    private abstract class GroupLayout
    {
        public abstract int Count { get; }

        public abstract IReadOnlyList<GroupDefinition> Groups { get; }

        /// <summary>The slot each row of a segment belongs to. Every row gets exactly one.</summary>
        public abstract int[] Assign(SegmentReaderV1 segment);
    }

    /// <summary>One reader-visible group and the slots whose contributions it holds, with the strength of each.</summary>
    private sealed record GroupDefinition(
        MetricGroupKind Kind,
        IReadOnlyList<(int Slot, RelationStrength? Strength)> Slots,
        string StableKey,
        ProcessInstance? Process = null,
        Mechanism? Mechanism = null,
        ProcessBindingReason? Reason = null,
        string? Executable = null);

    /// <summary>
    /// Slots by process instance. The first slots hold the reasons a record is unattributed; each instance then has
    /// one slot per binding strength, so how strongly its records are bound survives into its group. Which process a
    /// record belongs to - the one that made it, the one at its other end, or the one a transfer left or reached - is
    /// the binder's to say; the layout only folds bindings into groups.
    /// </summary>
    private sealed class ProcessLayout : GroupLayout
    {
        private const int ReasonSlots = (int)ProcessBindingReason.NoRelationRule + 1;
        private static readonly RelationStrength[] Strengths = [RelationStrength.Direct, RelationStrength.Correlated, RelationStrength.Candidate];
        private readonly ProcessInstanceIndex index;
        private readonly EvidencePolicy policy;
        private readonly bool byExecutable;
        private readonly Func<SegmentReaderV1, ProcessBinding[]> binder;

        public ProcessLayout(
            ProcessInstanceIndex index,
            EvidencePolicy policy,
            bool byExecutable,
            Func<SegmentReaderV1, ProcessBinding[]> binder)
        {
            this.index = index;
            this.policy = policy;
            this.byExecutable = byExecutable;
            this.binder = binder;
            var groups = new List<GroupDefinition>();
            for (int reason = 1; reason < ReasonSlots; reason++)
            {
                if (reason == (int)ProcessBindingReason.ExecutableUnknown && !byExecutable)
                {
                    continue;
                }

                groups.Add(new(
                    MetricGroupKind.Unattributed,
                    [(reason, null)],
                    $"unattributed/{reason}",
                    Reason: (ProcessBindingReason)reason));
            }

            if (byExecutable)
            {
                // A basename alone can denote different binaries. Only the full path witnessed at start/rundown
                // groups instances; an exit's name alone is retained on the instance but never merged as an identity.
                foreach (IGrouping<string, (ProcessInstance Item, int Index)> executable in index.Instances
                    .Select((item, at) => (Item: item, Index: at))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Item.ImagePath))
                    .GroupBy(entry => entry.Item.ImagePath!, StringComparer.OrdinalIgnoreCase))
                {
                    groups.Add(new(
                        MetricGroupKind.Executable,
                        [.. executable.SelectMany(entry => Strengths.Select((strength, offset) =>
                            (SlotOf(entry.Index, offset), (RelationStrength?)strength)))],
                        executable.Key.ToUpperInvariant(),
                        Executable: executable.Key));
                }
            }
            else
            {
                for (int instance = 0; instance < index.Instances.Count; instance++)
                {
                    groups.Add(new(
                        MetricGroupKind.ProcessInstance,
                        [.. Strengths.Select((strength, offset) => (SlotOf(instance, offset), (RelationStrength?)strength))],
                        index.Instances[instance].Id.ToString(),
                        Process: index.Instances[instance]));
                }
            }

            Groups = groups;
        }

        public override int Count => ReasonSlots + (index.Instances.Count * Strengths.Length);

        public override IReadOnlyList<GroupDefinition> Groups { get; }

        public override int[] Assign(SegmentReaderV1 segment)
        {
            ProcessBinding[] bindings = binder(segment);
            int[] slots = new int[segment.RowCount];
            for (int row = 0; row < segment.RowCount; row++)
            {
                ProcessBinding binding = bindings[row];
                slots[row] = !binding.IsBound
                    ? (int)binding.Reason
                    : !binding.IsAdmittedUnder(policy)
                        ? (int)ProcessBindingReason.NotAdmittedByPolicy
                        : byExecutable && string.IsNullOrWhiteSpace(index.Instances[binding.Instance].ImagePath)
                            ? (int)ProcessBindingReason.ExecutableUnknown
                        : SlotOf(binding.Instance, Array.IndexOf(Strengths, binding.Strength));
            }

            return slots;
        }

        private static int SlotOf(int instance, int strength) => ReasonSlots + (instance * Strengths.Length) + strength;
    }

    /// <summary>Slots by mechanism: a row's mechanism is a fact about the record, so every row has exactly one.</summary>
    private sealed class MechanismLayout : GroupLayout
    {
        private const int Codes = (int)Domain.Mechanism.UnknownMechanism + 1;

        public override int Count => Codes;

        public override IReadOnlyList<GroupDefinition> Groups { get; } =
        [
            .. Enum.GetValues<Mechanism>().Select(mechanism => new GroupDefinition(
                MetricGroupKind.Mechanism,
                [((int)mechanism, null)],
                ((int)mechanism).ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                Mechanism: mechanism)),
        ];

        public override int[] Assign(SegmentReaderV1 segment)
        {
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            int[] slots = new int[segment.RowCount];
            for (int row = 0; row < segment.RowCount; row++)
            {
                slots[row] = (int)mechanisms.UnsignedAt(row)!.Value;
            }

            return slots;
        }
    }

    /// <summary>One reader-visible group while its slots are being folded together.</summary>
    private sealed class GroupAccumulator(GroupDefinition definition)
    {
        private readonly SortedDictionary<AccountingSide, SideTotal> sides = [];

        public GroupDefinition Definition { get; } = definition;

        public long Count { get; set; }

        public Dictionary<RelationStrength, long> Bindings { get; } = [];

        /// <summary>Everything the group holds, by row side, taken by the accounting or not.</summary>
        public IEnumerable<SideMeasurement> Measured => sides.Select(entry => entry.Value.ToMeasurement(entry.Key));

        public void Add(SideMeasurement side)
        {
            if (!sides.TryGetValue(side.Side, out SideTotal? running))
            {
                running = new();
                sides[side.Side] = running;
            }

            running.Add(side);
        }

        /// <summary>The group a reader sees, or null when nothing in scope belongs to it.</summary>
        public MetricGroup? ToGroup(bool isCount, IReadOnlyList<AccountingSide> taken, Context context, MetricRequest request)
        {
            IReadOnlyList<SideMeasurement> measured = [.. sides.Select(entry => entry.Value.ToMeasurement(entry.Key))];
            long value;
            long known;
            long unknown;
            if (isCount)
            {
                if (Count == 0)
                {
                    return null;
                }

                value = Count;
                known = Count;
                unknown = 0;
            }
            else
            {
                IReadOnlyList<SideMeasurement> takenSides = [.. measured.Where(side => taken.Contains(side.Side))];
                known = takenSides.Sum(side => side.KnownContributions);
                unknown = takenSides.Sum(side => side.UnknownContributions);
                if (known + unknown == 0)
                {
                    // A group with nothing the accounting takes is absent, not a zero: a process that only received
                    // has no row in a sent-bytes ranking rather than a row of 0 B (R21).
                    return null;
                }

                value = takenSides.Sum(side => side.TotalBytes);
            }

            bool isMeasured = isCount || known > 0;
            return new()
            {
                Kind = Definition.Kind,
                Process = Definition.Process,
                Executable = Definition.Executable,
                Mechanism = Definition.Mechanism,
                Reason = Definition.Reason,
                Value = isMeasured ? value : null,
                KnownContributions = known,
                UnknownContributions = unknown,
                Sides = measured,
                Bindings = Bindings,
                Rate = request.Metric == Metric.Rate && isMeasured
                    ? RateOf(value, isCount ? MeasurementUnit.Count : MeasurementUnit.Bytes, context)
                    : null,
            };
        }
    }
}
