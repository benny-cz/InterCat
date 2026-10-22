using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>What one row gives a byte view's measurement of one domain (<see cref="ByteDomainRows"/>).</summary>
public enum ByteDomainContribution
{
    /// <summary>The row measures no byte domain, or another one: the measurement passes it over.</summary>
    None,

    /// <summary>The row measures the domain at an accounting side, with a size or none.</summary>
    Measured,

    /// <summary>
    /// The measurement refuses the row: it names no accounting side, one §23 does not define, no unit, or a unit other than
    /// bytes, which a byte view does not sum (<see cref="SegmentMeasurement.Unsummable"/>).
    /// </summary>
    Refused,
}

/// <summary>
/// One segment's rows as a byte view measures one domain of them (<see cref="SegmentMeasurement.MeasureDomainByGroup"/>,
/// summed in bytes): whether a row is measured, at which accounting side and with what size, or refused. A derivation
/// that counts a domain's bytes as it reads its rows counts with this exactly what that measurement would, so a view can
/// take the counts in place of a second read.
/// </summary>
public readonly ref struct ByteDomainRows
{
    private readonly ByteDomain domain;
    private readonly SegmentColumnSlice domains;
    private readonly SegmentColumnSlice sides;
    private readonly SegmentColumnSlice units;
    private readonly SegmentColumnSlice values;

    public ByteDomainRows(SegmentReaderV1 segment, ByteDomain domain)
    {
        ArgumentNullException.ThrowIfNull(segment);
        this.domain = domain;
        domains = segment.Slice(SegmentColumnId.ByteDomain);
        sides = segment.Slice(SegmentColumnId.AccountingSide);
        units = segment.Slice(SegmentColumnId.MeasurementUnit);
        values = segment.Slice(SegmentColumnId.ByteValue);
    }

    /// <summary>
    /// What <paramref name="row"/> gives the measurement: with <see cref="ByteDomainContribution.Measured"/>, its side and
    /// the bytes it measured, or null for a size it declared and did not record.
    /// </summary>
    public ByteDomainContribution At(int row, out AccountingSide side, out long? value)
    {
        side = default;
        value = null;
        if (domains.UnsignedAt(row) is not { } code || (ByteDomain)code != domain)
        {
            return ByteDomainContribution.None;
        }

        if (sides.UnsignedAt(row) is not { } sideCode
            || sideCode is 0 or > (ulong)AccountingSide.CanonicalOwner
            || units.UnsignedAt(row) is not { } unit
            || (MeasurementUnit)unit != MeasurementUnit.Bytes)
        {
            return ByteDomainContribution.Refused;
        }

        side = (AccountingSide)sideCode;
        value = values.SignedAt(row);
        return ByteDomainContribution.Measured;
    }
}
