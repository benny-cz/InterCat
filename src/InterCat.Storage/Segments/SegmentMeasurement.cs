using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// Which contributions a byte sum is over. One domain and one side, never a set of them: two byte domains
/// are different quantities and summing them produces a number that means nothing (I6, P3, §5.3).
/// </summary>
public sealed record ByteSumSpec
{
    public required ByteDomain Domain { get; init; }

    public required AccountingSide Side { get; init; }

    /// <summary>Restricts the sum to one layer. Null means every layer the segment carries.</summary>
    public ObservationLayer? Layer { get; init; }

    /// <summary>Restricts the sum to one mechanism. Null means every mechanism the segment carries.</summary>
    public Mechanism? Mechanism { get; init; }

    /// <summary>
    /// Restricts the sum to rows whose native reading is inside this half-open interval on the segment's own
    /// clock (I3). Null means every row the segment holds.
    /// </summary>
    public TimeRange? Interval { get; init; }

    public string? Validate() =>
        !Enum.IsDefined(Domain)
            ? "A byte sum names a byte domain §23 defines."
            : !Enum.IsDefined(Side)
                ? "A byte sum names an accounting side §23 defines."
                : Layer is { } layer && !Enum.IsDefined(layer)
                    ? "A layer projection names a layer §23 defines."
                    : Mechanism is { } mechanism && !Enum.IsDefined(mechanism)
                        ? "A mechanism projection names a mechanism §23 defines."
                        : null;
}

/// <summary>
/// One byte sum and everything needed to read it honestly: the domain and side it covers, how many
/// contributions were known and how many were unknown and why, and how many rows were excluded and on
/// what grounds. Nothing here folds an unknown into a zero (R3), and nothing here is a percentage of all
/// traffic — the availability ratio's denominator is the declared slots this sum covers (§5).
/// </summary>
public sealed record ByteSumResult
{
    public required ByteDomain Domain { get; init; }

    public required AccountingSide Side { get; init; }

    /// <summary>The unit every contribution carried. A sum over two units is refused, not converted.</summary>
    public required MeasurementUnit? Unit { get; init; }

    /// <summary>The sum of the known contributions. Zero known contributions is a zero-byte sum of nothing.</summary>
    public required long TotalBytes { get; init; }

    public required long KnownContributions { get; init; }

    public required long UnknownContributions { get; init; }

    /// <summary>Rows in the projection whose declared slot is in another domain. Never summed here (P3).</summary>
    public required long ExcludedOtherDomain { get; init; }

    /// <summary>
    /// Rows whose declared slot is in this domain but accounted to another side. They remain visible as
    /// corroborating evidence and contribute nothing to this total (§5.3).
    /// </summary>
    public required long ExcludedOtherSide { get; init; }

    /// <summary>Rows whose descriptor declares no byte field at all. Not a missing measurement (§5).</summary>
    public required long ExcludedNoDeclaredSlot { get; init; }

    /// <summary>Rows outside the layer or mechanism this sum projects onto.</summary>
    public required long ExcludedByProjection { get; init; }

    /// <summary>Rows whose native reading is outside the interval this sum is scoped to.</summary>
    public long ExcludedOutsideInterval { get; init; }

    /// <summary>Why each unknown contribution is unknown, counted per reason (§7.3).</summary>
    public required IReadOnlyDictionary<FieldAvailability, long> UnknownReasons { get; init; }

    /// <summary>
    /// The share of declared slots in this domain and side that carried a value, or null when there are
    /// none. It is measurement availability and nothing else: it says how much of what could be measured
    /// was, not how much of the machine's traffic was captured (§5).
    /// </summary>
    public double? MeasurementAvailability =>
        KnownContributions + UnknownContributions == 0
            ? null
            : (double)KnownContributions / (KnownContributions + UnknownContributions);

    /// <summary>Whether the sum covers every declared slot it projects onto, with no unknown value.</summary>
    public bool IsComplete => UnknownContributions == 0 && KnownContributions > 0;
}

/// <summary>
/// Which rows one byte domain is measured over, and which of them to return as evidence. The accounting sides a
/// metric takes are the metric layer's decision (§5.3); this only says which row labels to hand back as the
/// records behind the number.
/// </summary>
public sealed record DomainMeasurementSpec
{
    public required ByteDomain Domain { get; init; }

    public ObservationLayer? Layer { get; init; }

    public Mechanism? Mechanism { get; init; }

    /// <summary>Half-open, on the segment's own clock. Null means every row the segment holds.</summary>
    public TimeRange? Interval { get; init; }

    /// <summary>The row sides whose contributions are returned as evidence rows. Empty returns none.</summary>
    public IReadOnlyList<AccountingSide> EvidenceSides { get; init; } = [];

    /// <summary>How many evidence rows to return at most, in segment order. Zero returns none.</summary>
    public int EvidenceLimit { get; init; }

    public string? Validate() =>
        !Enum.IsDefined(Domain)
            ? "A measurement names a byte domain §23 defines."
            : Layer is { } layer && !Enum.IsDefined(layer)
                ? "A layer projection names a layer §23 defines."
                : Mechanism is { } mechanism && !Enum.IsDefined(mechanism)
                    ? "A mechanism projection names a mechanism §23 defines."
                    : EvidenceSides.Any(side => !Enum.IsDefined(side))
                        ? "Evidence sides are §23 accounting-side codes."
                        : EvidenceLimit is < 0 or > SegmentMeasurement.MaximumEvidenceRows
                            ? $"An evidence listing returns between 0 and {SegmentMeasurement.MaximumEvidenceRows} rows."
                            : null;
}

/// <summary>What one row label contributed inside one byte domain: its known sum, and its unknowns with their reasons.</summary>
public sealed record SideMeasurement
{
    public required AccountingSide Side { get; init; }

    public required long TotalBytes { get; init; }

    public required long KnownContributions { get; init; }

    public required long UnknownContributions { get; init; }

    public required IReadOnlyDictionary<FieldAvailability, long> UnknownReasons { get; init; }

    public long DeclaredContributions => KnownContributions + UnknownContributions;
}

/// <summary>
/// One byte domain over one segment, broken down by the accounting side each row's measurement is labelled with.
/// Nothing is combined here: which sides a total takes is a metric's accounting rule, and the sides it does not
/// take stay in the breakdown so a caller can say what it left out (I6, §5.3, §19.2).
/// </summary>
public sealed record DomainMeasurement
{
    public required ByteDomain Domain { get; init; }

    /// <summary>The unit every contribution in this domain carried, or null when there were none.</summary>
    public required MeasurementUnit? Unit { get; init; }

    /// <summary>One entry per row side that carried a declared slot in this domain, in §23 code order.</summary>
    public required IReadOnlyList<SideMeasurement> Sides { get; init; }

    public required long ExcludedOtherDomain { get; init; }

    /// <summary>
    /// The rows counted in <see cref="ExcludedOtherDomain"/>, by the domain their slot is in. A caller that finds
    /// nothing measured in the requested domain can say which quantities the records in scope do measure.
    /// </summary>
    public required IReadOnlyDictionary<ByteDomain, long> OtherDomains { get; init; }

    public required long ExcludedNoDeclaredSlot { get; init; }

    public required long ExcludedByProjection { get; init; }

    public required long ExcludedOutsideInterval { get; init; }

    /// <summary>The rows behind the number, in segment order, bounded by the request's evidence limit.</summary>
    public required IReadOnlyList<int> EvidenceRows { get; init; }

    public SideMeasurement? Side(AccountingSide side) =>
        Sides.FirstOrDefault(measurement => measurement.Side == side);
}

/// <summary>
/// The metric contributions a published segment can answer directly. This is the accounting layer, not the
/// query engine: it fixes what a byte sum and an observation count mean over one segment, so the scheduler
/// and caches of IC-017 compose something already correct rather than defining the semantics themselves.
/// </summary>
public static class SegmentMeasurement
{
    /// <summary>The most evidence rows one measurement returns. A listing is an inspection path, not an export.</summary>
    public const int MaximumEvidenceRows = 10_000;

    /// <summary>
    /// Sums one byte domain and one accounting side over a segment. Every row is accounted for — summed,
    /// counted as an unknown with its reason, or excluded with the reason it was excluded — so a total can
    /// always be read against what it left out (R2, R3, I6).
    /// </summary>
    public static ByteSumResult SumBytes(SegmentReaderV1 segment, ByteSumSpec spec)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(spec);
        string? problem = spec.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(spec));
        }

        DomainMeasurement measured = MeasureDomain(
            segment,
            new()
            {
                Domain = spec.Domain,
                Layer = spec.Layer,
                Mechanism = spec.Mechanism,
                Interval = spec.Interval,
            });
        SideMeasurement? taken = measured.Side(spec.Side);
        long otherSide = measured.Sides
            .Where(side => side.Side != spec.Side)
            .Sum(side => side.DeclaredContributions);
        return new()
        {
            Domain = spec.Domain,
            Side = spec.Side,
            Unit = taken is null ? null : measured.Unit,
            TotalBytes = taken?.TotalBytes ?? 0,
            KnownContributions = taken?.KnownContributions ?? 0,
            UnknownContributions = taken?.UnknownContributions ?? 0,
            ExcludedOtherDomain = measured.ExcludedOtherDomain,
            ExcludedOtherSide = otherSide,
            ExcludedNoDeclaredSlot = measured.ExcludedNoDeclaredSlot,
            ExcludedByProjection = measured.ExcludedByProjection,
            ExcludedOutsideInterval = measured.ExcludedOutsideInterval,
            UnknownReasons = taken?.UnknownReasons ?? new Dictionary<FieldAvailability, long>(),
        };
    }

    /// <summary>
    /// Measures one byte domain over a segment in one pass, keeping each accounting side's contributions apart.
    /// Every row is accounted for: in the interval or not, in the projection or not, with a declared slot in this
    /// domain, in another, or in none. Accumulation is checked, so a long capture reports an overflow rather than
    /// wrapping into a smaller total that looks plausible (§10.2).
    /// </summary>
    public static DomainMeasurement MeasureDomain(SegmentReaderV1 segment, DomainMeasurementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(spec);
        string? problem = spec.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(spec));
        }

        (int first, int end) = spec.Interval is { } interval ? segment.RowsWithin(interval) : (0, segment.RowCount);

        // Indexed by §23 accounting-side code. A code past the table is refused by the row's own validation
        // before it can be written, so the bound is the enumeration, not a guess.
        const int Slots = (int)AccountingSide.CanonicalOwner + 1;
        var totals = new long[Slots];
        var known = new long[Slots];
        var unknown = new long[Slots];
        var reasons = new Dictionary<FieldAvailability, long>?[Slots];
        var otherDomains = new long[(int)ByteDomain.Capacity + 1];
        long otherDomain = 0;
        long noSlot = 0;
        long outsideProjection = 0;
        MeasurementUnit? unit = null;
        var evidence = new List<int>(Math.Min(spec.EvidenceLimit, 64));
        bool wantsEvidence = spec.EvidenceLimit > 0 && spec.EvidenceSides.Count > 0;

        // The columns are resolved and checksummed once, and the loop reads them as spans. This is the
        // accounting layer the query scheduler composes, so it walks bytes rather than re-resolving a column
        // per row (R11).
        SegmentColumnSlice layer = segment.Slice(SegmentColumnId.Layer);
        SegmentColumnSlice mechanism = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice domains = segment.Slice(SegmentColumnId.ByteDomain);
        SegmentColumnSlice sides = segment.Slice(SegmentColumnId.AccountingSide);
        SegmentColumnSlice units = segment.Slice(SegmentColumnId.MeasurementUnit);
        SegmentColumnSlice values = segment.Slice(SegmentColumnId.ByteValue);
        SegmentColumnSlice availability = segment.Slice(SegmentColumnId.ByteAvailability);

        for (int row = first; row < end; row++)
        {
            if ((spec.Layer is { } onlyLayer && (ObservationLayer)layer.UnsignedAt(row)!.Value != onlyLayer)
                || (spec.Mechanism is { } onlyMechanism
                    && (Mechanism)mechanism.UnsignedAt(row)!.Value != onlyMechanism))
            {
                outsideProjection++;
                continue;
            }

            if (domains.UnsignedAt(row) is not { } domainCode)
            {
                noSlot++;
                continue;
            }

            if ((ByteDomain)domainCode != spec.Domain)
            {
                otherDomain++;
                if (domainCode < (ulong)otherDomains.Length)
                {
                    otherDomains[domainCode]++;
                }

                continue;
            }

            ulong sideCode = sides.UnsignedAt(row)
                ?? throw new InvalidDataException(
                    $"Row {row} declares a byte domain and no accounting side, so nothing says which side of "
                    + "the exchange its measurement belongs to (§5.3).");
            if (sideCode is 0 or >= Slots)
            {
                throw new InvalidDataException(
                    $"Row {row} labels its measurement with accounting side {sideCode}, which §23 does not "
                    + "define. An unknown side is refused rather than summed under a guessed one.");
            }

            var rowUnit = (MeasurementUnit)(units.UnsignedAt(row)
                ?? throw new InvalidDataException($"Row {row} declares a byte slot with no unit (R2)."));
            if (unit is { } established && established != rowUnit)
            {
                throw new InvalidDataException(
                    $"Row {row} measures in {rowUnit} where earlier rows measure in {established}. Two units "
                    + "are not summed and are not converted into one another here.");
            }

            unit = rowUnit;
            int slot = (int)sideCode;
            if (values.SignedAt(row) is { } value)
            {
                totals[slot] = checked(totals[slot] + value);
                known[slot]++;
            }
            else
            {
                unknown[slot]++;
                var reason = (FieldAvailability)availability.UnsignedAt(row)!.Value;
                Dictionary<FieldAvailability, long> counted = reasons[slot] ??= [];
                counted[reason] = counted.TryGetValue(reason, out long count) ? count + 1 : 1;
            }

            if (wantsEvidence && evidence.Count < spec.EvidenceLimit && Takes(spec.EvidenceSides, (AccountingSide)slot))
            {
                evidence.Add(row);
            }
        }

        var measuredSides = new List<SideMeasurement>();
        for (int slot = 1; slot < Slots; slot++)
        {
            if (known[slot] + unknown[slot] == 0)
            {
                continue;
            }

            measuredSides.Add(new()
            {
                Side = (AccountingSide)slot,
                TotalBytes = totals[slot],
                KnownContributions = known[slot],
                UnknownContributions = unknown[slot],
                UnknownReasons = (IReadOnlyDictionary<FieldAvailability, long>?)reasons[slot]
                    ?? new Dictionary<FieldAvailability, long>(),
            });
        }

        var byDomain = new Dictionary<ByteDomain, long>();
        for (int code = 1; code < otherDomains.Length; code++)
        {
            if (otherDomains[code] > 0)
            {
                byDomain[(ByteDomain)code] = otherDomains[code];
            }
        }

        return new()
        {
            Domain = spec.Domain,
            Unit = unit,
            Sides = measuredSides,
            ExcludedOtherDomain = otherDomain,
            OtherDomains = byDomain,
            ExcludedNoDeclaredSlot = noSlot,
            ExcludedByProjection = outsideProjection,
            ExcludedOutsideInterval = segment.RowCount - (end - first),
            EvidenceRows = evidence,
        };
    }

    /// <summary>
    /// Counts observations over a projection. It is deliberately a separate metric from a byte sum: an
    /// observation count is sensitive to instrumentation density and says nothing about volume (§5).
    /// </summary>
    public static long CountObservations(
        SegmentReaderV1 segment,
        ObservationLayer? layer = null,
        Mechanism? mechanism = null) =>
        CountObservations(segment, layer, mechanism, interval: null).Counted;

    /// <summary>
    /// Counts observations over a projection and a half-open native-time interval, and returns the rows it did
    /// not count on each ground, so a count can be read against what it left out.
    /// </summary>
    public static ObservationCount CountObservations(
        SegmentReaderV1 segment,
        ObservationLayer? layer,
        Mechanism? mechanism,
        TimeRange? interval,
        int evidenceLimit = 0)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (layer is { } declaredLayer && !Enum.IsDefined(declaredLayer))
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "A layer projection names a §23 code.");
        }

        if (mechanism is { } declaredMechanism && !Enum.IsDefined(declaredMechanism))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mechanism),
                mechanism,
                "A mechanism projection names a §23 code.");
        }

        if (evidenceLimit is < 0 or > MaximumEvidenceRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceLimit),
                evidenceLimit,
                $"An evidence listing returns between 0 and {MaximumEvidenceRows} rows.");
        }

        (int first, int end) = interval is { } scoped ? segment.RowsWithin(scoped) : (0, segment.RowCount);
        SegmentColumnSlice layers = segment.Slice(SegmentColumnId.Layer);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        var evidence = new List<int>(Math.Min(evidenceLimit, 64));
        long count = 0;
        for (int row = first; row < end; row++)
        {
            if ((layer is null || (ObservationLayer)layers.UnsignedAt(row)!.Value == layer)
                && (mechanism is null || (Mechanism)mechanisms.UnsignedAt(row)!.Value == mechanism))
            {
                count++;
                if (evidence.Count < evidenceLimit)
                {
                    evidence.Add(row);
                }
            }
        }

        return new(count, (end - first) - count, segment.RowCount - (end - first), evidence);
    }

    /// <summary>
    /// Measures one byte domain over a segment in one pass, keeping every group and every accounting side apart. The
    /// caller says which group each row belongs to — a process instance, a mechanism, a reason it is unattributed —
    /// and this fixes nothing about what a group means; it only guarantees that every row in scope lands in exactly
    /// the group it was given, so the groups partition what an ungrouped measurement would count.
    /// </summary>
    public static GroupedDomainMeasurement MeasureDomainByGroup(
        SegmentReaderV1 segment,
        DomainMeasurementSpec spec,
        ReadOnlySpan<int> groupOfRow,
        int groupCount)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(spec);
        string? problem = spec.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(spec));
        }

        if (groupOfRow.Length != segment.RowCount)
        {
            throw new ArgumentException(
                $"A grouping names one group per row: this segment has {segment.RowCount} rows and the grouping "
                + $"{groupOfRow.Length}.",
                nameof(groupOfRow));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupCount);
        (int first, int end) = spec.Interval is { } interval ? segment.RowsWithin(interval) : (0, segment.RowCount);

        const int Slots = (int)AccountingSide.CanonicalOwner + 1;
        var totals = new long[groupCount * Slots];
        var known = new long[groupCount * Slots];
        var unknown = new long[groupCount * Slots];
        Dictionary<int, Dictionary<FieldAvailability, long>>? reasons = null;
        long otherDomain = 0;
        long noSlot = 0;
        long outsideProjection = 0;
        MeasurementUnit? unit = null;

        SegmentColumnSlice layer = segment.Slice(SegmentColumnId.Layer);
        SegmentColumnSlice mechanism = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice domains = segment.Slice(SegmentColumnId.ByteDomain);
        SegmentColumnSlice sides = segment.Slice(SegmentColumnId.AccountingSide);
        SegmentColumnSlice units = segment.Slice(SegmentColumnId.MeasurementUnit);
        SegmentColumnSlice values = segment.Slice(SegmentColumnId.ByteValue);
        SegmentColumnSlice availability = segment.Slice(SegmentColumnId.ByteAvailability);

        for (int row = first; row < end; row++)
        {
            if ((spec.Layer is { } onlyLayer && (ObservationLayer)layer.UnsignedAt(row)!.Value != onlyLayer)
                || (spec.Mechanism is { } onlyMechanism
                    && (Mechanism)mechanism.UnsignedAt(row)!.Value != onlyMechanism))
            {
                outsideProjection++;
                continue;
            }

            if (domains.UnsignedAt(row) is not { } domainCode)
            {
                noSlot++;
                continue;
            }

            if ((ByteDomain)domainCode != spec.Domain)
            {
                otherDomain++;
                continue;
            }

            int group = groupOfRow[row];
            if ((uint)group >= (uint)groupCount)
            {
                throw new ArgumentException(
                    $"Row {row} is given group {group}, outside the {groupCount} groups declared.",
                    nameof(groupOfRow));
            }

            ulong sideCode = sides.UnsignedAt(row)
                ?? throw new InvalidDataException(
                    $"Row {row} declares a byte domain and no accounting side, so nothing says which side of "
                    + "the exchange its measurement belongs to (§5.3).");
            if (sideCode is 0 or >= Slots)
            {
                throw new InvalidDataException(
                    $"Row {row} labels its measurement with accounting side {sideCode}, which §23 does not "
                    + "define. An unknown side is refused rather than summed under a guessed one.");
            }

            var rowUnit = (MeasurementUnit)(units.UnsignedAt(row)
                ?? throw new InvalidDataException($"Row {row} declares a byte slot with no unit (R2)."));
            if (unit is { } established && established != rowUnit)
            {
                throw new InvalidDataException(
                    $"Row {row} measures in {rowUnit} where earlier rows measure in {established}. Two units "
                    + "are not summed and are not converted into one another here.");
            }

            unit = rowUnit;
            int slot = (group * Slots) + (int)sideCode;
            if (values.SignedAt(row) is { } value)
            {
                totals[slot] = checked(totals[slot] + value);
                known[slot]++;
            }
            else
            {
                unknown[slot]++;
                var reason = (FieldAvailability)availability.UnsignedAt(row)!.Value;
                reasons ??= [];
                if (!reasons.TryGetValue(slot, out Dictionary<FieldAvailability, long>? counted))
                {
                    counted = [];
                    reasons[slot] = counted;
                }

                counted[reason] = counted.TryGetValue(reason, out long count) ? count + 1 : 1;
            }
        }

        var groups = new IReadOnlyList<SideMeasurement>[groupCount];
        for (int group = 0; group < groupCount; group++)
        {
            List<SideMeasurement>? measured = null;
            for (int side = 1; side < Slots; side++)
            {
                int slot = (group * Slots) + side;
                if (known[slot] + unknown[slot] == 0)
                {
                    continue;
                }

                (measured ??= []).Add(new()
                {
                    Side = (AccountingSide)side,
                    TotalBytes = totals[slot],
                    KnownContributions = known[slot],
                    UnknownContributions = unknown[slot],
                    UnknownReasons = reasons is not null && reasons.TryGetValue(slot, out Dictionary<FieldAvailability, long>? why)
                        ? why
                        : new Dictionary<FieldAvailability, long>(),
                });
            }

            groups[group] = (IReadOnlyList<SideMeasurement>?)measured ?? [];
        }

        return new(spec.Domain, unit, groups, otherDomain, noSlot, outsideProjection, segment.RowCount - (end - first));
    }

    /// <summary>
    /// Counts observations per group over a projection and an interval. Every counted row lands in the group it was
    /// given, so the groups' counts add up to the ungrouped count.
    /// </summary>
    public static long[] CountObservationsByGroup(
        SegmentReaderV1 segment,
        ObservationLayer? layer,
        Mechanism? mechanism,
        TimeRange? interval,
        ReadOnlySpan<int> groupOfRow,
        int groupCount)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (groupOfRow.Length != segment.RowCount)
        {
            throw new ArgumentException(
                $"A grouping names one group per row: this segment has {segment.RowCount} rows and the grouping "
                + $"{groupOfRow.Length}.",
                nameof(groupOfRow));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupCount);
        (int first, int end) = interval is { } scoped ? segment.RowsWithin(scoped) : (0, segment.RowCount);
        SegmentColumnSlice layers = segment.Slice(SegmentColumnId.Layer);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        var counts = new long[groupCount];
        for (int row = first; row < end; row++)
        {
            if ((layer is null || (ObservationLayer)layers.UnsignedAt(row)!.Value == layer)
                && (mechanism is null || (Mechanism)mechanisms.UnsignedAt(row)!.Value == mechanism))
            {
                int group = groupOfRow[row];
                if ((uint)group >= (uint)groupCount)
                {
                    throw new ArgumentException(
                        $"Row {row} is given group {group}, outside the {groupCount} groups declared.",
                        nameof(groupOfRow));
                }

                counts[group]++;
            }
        }

        return counts;
    }

    private static bool Takes(IReadOnlyList<AccountingSide> sides, AccountingSide side)
    {
        for (int index = 0; index < sides.Count; index++)
        {
            if (sides[index] == side)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One byte domain over one segment, broken down by group and then by accounting side. The exclusions are the same
/// as an ungrouped measurement's, because grouping never changes which rows are in scope.
/// </summary>
public sealed record GroupedDomainMeasurement(
    ByteDomain Domain,
    MeasurementUnit? Unit,
    IReadOnlyList<IReadOnlyList<SideMeasurement>> Groups,
    long ExcludedOtherDomain,
    long ExcludedNoDeclaredSlot,
    long ExcludedByProjection,
    long ExcludedOutsideInterval);

/// <summary>An observation count over one segment, with the rows it did not count on each ground.</summary>
public sealed record ObservationCount(
    long Counted,
    long ExcludedByProjection,
    long ExcludedOutsideInterval,
    IReadOnlyList<int> EvidenceRows);
