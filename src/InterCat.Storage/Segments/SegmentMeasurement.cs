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
/// The metric contributions a published segment can answer directly. This is the accounting layer, not the
/// query engine: it fixes what a byte sum and an observation count mean over one segment, so the scheduler
/// and caches of IC-017 compose something already correct rather than defining the semantics themselves.
/// </summary>
public static class SegmentMeasurement
{
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

        long total = 0;
        long known = 0;
        long unknown = 0;
        long otherDomain = 0;
        long otherSide = 0;
        long noSlot = 0;
        long outsideProjection = 0;
        MeasurementUnit? unit = null;
        var reasons = new Dictionary<FieldAvailability, long>();

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

        for (int row = 0; row < segment.RowCount; row++)
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

            ulong sideCode = sides.UnsignedAt(row)
                ?? throw new InvalidDataException(
                    $"Row {row} declares a byte domain and no accounting side, so nothing says which side of "
                    + "the exchange its measurement belongs to (§5.3).");
            if ((AccountingSide)sideCode != spec.Side)
            {
                otherSide++;
                continue;
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
            if (values.SignedAt(row) is { } value)
            {
                // Checked, so a long high-volume capture reports an overflow rather than wrapping into a
                // smaller total that looks plausible (§10.2).
                total = checked(total + value);
                known++;
                continue;
            }

            unknown++;
            var reason = (FieldAvailability)availability.UnsignedAt(row)!.Value;
            reasons[reason] = reasons.TryGetValue(reason, out long count) ? count + 1 : 1;
        }

        return new()
        {
            Domain = spec.Domain,
            Side = spec.Side,
            Unit = unit,
            TotalBytes = total,
            KnownContributions = known,
            UnknownContributions = unknown,
            ExcludedOtherDomain = otherDomain,
            ExcludedOtherSide = otherSide,
            ExcludedNoDeclaredSlot = noSlot,
            ExcludedByProjection = outsideProjection,
            UnknownReasons = reasons,
        };
    }

    /// <summary>
    /// Counts observations over a projection. It is deliberately a separate metric from a byte sum: an
    /// observation count is sensitive to instrumentation density and says nothing about volume (§5).
    /// </summary>
    public static long CountObservations(
        SegmentReaderV1 segment,
        ObservationLayer? layer = null,
        Mechanism? mechanism = null)
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

        SegmentColumnSlice layers = segment.Slice(SegmentColumnId.Layer);
        SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
        long count = 0;
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((layer is null || (ObservationLayer)layers.UnsignedAt(row)!.Value == layer)
                && (mechanism is null || (Mechanism)mechanisms.UnsignedAt(row)!.Value == mechanism))
            {
                count++;
            }
        }

        return count;
    }
}
