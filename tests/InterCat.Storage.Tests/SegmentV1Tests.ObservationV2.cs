using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// `observation-v2` (revision 172): `observation-v1` with the two IPv6 endpoint address columns its 32-bit address columns
/// cannot hold. It is written only for a segment one of whose rows has an IPv6 address; every other segment stays
/// `observation-v1`, byte for byte (<see cref="SegmentGoldenTests"/>).
/// </summary>
public sealed partial class SegmentV1Tests
{
    // 2001:db8::5ec:1 and fe80::1:2, as numbers whose bytes in network order are the addresses.
    private static readonly UInt128 DocumentationHost = ((UInt128)0x2001_0DB8 << 96) | 0x05EC_0001;
    private static readonly UInt128 LinkLocalHost = ((UInt128)0xFE80 << 112) | 0x0001_0002;

    [Fact(DisplayName = "I15: an IPv6 endpoint round-trips in an observation-v2 segment, with its nulls")]
    public void AnIpv6EndpointRoundTrips()
    {
        ObservationRowV1 wide = Ipv6(Row(1_000, bytes: 512), DocumentationHost, 50_000, UInt128.One, 443);
        ObservationRowV1 narrow = Row(2_000, bytes: 64) with
        {
            EndpointAddressFamily = 4,
            SourceEndpointAddress = 0x7F00_0001,
            SourceEndpointPort = 52_100,
            DestinationEndpointAddress = 0x7F00_0001,
            DestinationEndpointPort = 443,
        };
        ObservationRowV1 halfKnown = Row(3_000, bytes: 16) with
        {
            EndpointAddressFamily = 6,
            SourceEndpointAddressV6 = LinkLocalHost,
            SourceEndpointPort = 0,
        };
        ObservationRowV1 sparse = Row(4_000, bytes: null, availability: FieldAvailability.NotApplicable, declareSlot: false);

        SegmentReaderV1 segment = Build([wide, narrow, halfKnown, sparse]);

        Assert.Equal(SegmentTableId.ObservationV2, segment.Table);
        Assert.True(segment.HoldsObservations);
        Assert.Equal([wide, narrow, halfKnown, sparse], Enumerable.Range(0, 4).Select(segment.Row));

        // The two columns follow observation-v1's, are 16 bytes a row, and count their nulls as every column does.
        Assert.Equal(
            [.. SegmentFormatV1.ObservationColumns.Select(column => column.Id),
                SegmentColumnId.SourceEndpointAddressV6, SegmentColumnId.DestinationEndpointAddressV6],
            SegmentFormatV1.ObservationV2Columns.Select(column => column.Id));
        SegmentColumnDescriptor source = segment.Column(SegmentColumnId.SourceEndpointAddressV6)!;
        SegmentColumnDescriptor destination = segment.Column(SegmentColumnId.DestinationEndpointAddressV6)!;
        Assert.Equal((SegmentColumnType.Address128, true, 16 * 4), (source.Type, source.Nullable, source.ValueLength));
        Assert.Equal((2, 2), (source.KnownCount, source.UnknownCount));
        Assert.Equal((1, 3), (destination.KnownCount, destination.UnknownCount));
        Assert.Equal(DocumentationHost, segment.AddressValue(SegmentColumnId.SourceEndpointAddressV6, 0));
        Assert.Equal(UInt128.One, segment.AddressValue(SegmentColumnId.DestinationEndpointAddressV6, 0));
        Assert.Null(segment.AddressValue(SegmentColumnId.SourceEndpointAddressV6, 1));
        Assert.Equal(LinkLocalHost, segment.Slice(SegmentColumnId.SourceEndpointAddressV6).AddressAt(2));
        Assert.Null(segment.Slice(SegmentColumnId.DestinationEndpointAddressV6).AddressAt(3));

        // The bytes are the address in network order, as it is written on the wire.
        byte[] stored = segment.ValueBytes(SegmentColumnId.SourceEndpointAddressV6)[..16].ToArray();
        Assert.Equal(new byte[] { 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 0xEC, 0x00, 0x01 }, stored);

        // An address column is read as an address, never as a number or an identifier of another width.
        Assert.Throws<InvalidOperationException>(() => segment.AddressValue(SegmentColumnId.SourceEndpointAddress, 0));
        Assert.Throws<InvalidOperationException>(() => segment.Slice(SegmentColumnId.SourceEndpointAddress).AddressAt(0));
    }

    [Fact(DisplayName = "I15: a segment none of whose rows has an IPv6 address is observation-v1 and stages what it always did")]
    public void ASegmentWithoutIpv6StaysObservationV1()
    {
        ObservationRowV1[] rows = Spread(20);
        SegmentReaderV1 segment = Build(rows);

        Assert.Equal(SegmentTableId.ObservationV1, segment.Table);
        Assert.True(segment.HoldsObservations);
        Assert.False(segment.HasColumn(SegmentColumnId.SourceEndpointAddressV6));
        Assert.Null(segment.Column(SegmentColumnId.DestinationEndpointAddressV6));
        Assert.All(Enumerable.Range(0, rows.Length).Select(segment.Row), row => Assert.True(
            row.SourceEndpointAddressV6 is null && row.DestinationEndpointAddressV6 is null));
        Assert.Throws<InvalidOperationException>(() => segment.AddressValue(SegmentColumnId.SourceEndpointAddressV6, 0));

        // A writer stages observation-v1's layout until a row has an IPv6 address, so where a derivation flushes a
        // segment of IPv4 records - and so which segments it publishes - is what it was before observation-v2 existed.
        var writer = new SegmentWriterV1(Identity, 0);
        foreach (ObservationRowV1 row in rows)
        {
            writer.Add(row);
        }

        Assert.Equal(Staged(SegmentFormatV1.ObservationColumns, rows.Length), writer.StagedBytes);
        writer.Add(Ipv6(Row(5_000, bytes: 1, ordinal: 999), DocumentationHost, 50_000, LinkLocalHost, 443));
        Assert.Equal(Staged(SegmentFormatV1.ObservationV2Columns, rows.Length + 1), writer.StagedBytes);
        SegmentBuildResult widened = writer.Build();
        Assert.Equal(SegmentTableId.ObservationV2, SegmentReaderV1.Open(widened.Segment, widened.Dictionaries).Table);
    }

    [Fact(DisplayName = "I15: an observation-v2 segment has its own identity, and a reader refuses to take it for another table")]
    public void AnObservationV2SegmentHasItsOwnIdentity()
    {
        ObservationRowV1 row = Row(1_000, bytes: 8);
        SegmentBuildResult narrow = new SegmentWriterV1(Identity, 0).Tap(writer => writer.Add(row)).Build();
        SegmentBuildResult wide = new SegmentWriterV1(Identity, 0)
            .Tap(writer => writer.Add(Ipv6(row, DocumentationHost, 50_000, UInt128.One, 443)))
            .Build();

        // Same ordinal, rows and extent, different tables: the identities differ, so neither can stand in for the other.
        Assert.NotEqual(narrow.SegmentId, wide.SegmentId);
        Assert.Equal(Identity.SegmentIdFor(0, 1, 1_000, 1_000), narrow.SegmentId);
        Assert.Equal(Identity.SegmentIdFor(SegmentTableId.ObservationV2, 0, 1, 1_000, 1_000), wide.SegmentId);
        SegmentReaderV1 segment = SegmentReaderV1.Open(wide.Segment, wide.Dictionaries);
        Assert.Throws<InvalidOperationException>(() => segment.FieldRow(0));
    }

    [Fact(DisplayName = "R3: an address is kept whole in its family's columns, never narrowed into another's")]
    public void ARowKeepsItsAddressesInItsFamilysColumns()
    {
        ObservationRowV1 row = Row(1_000, bytes: 8);

        Assert.Null(Ipv6(row, DocumentationHost, 50_000, UInt128.One, 443).Validate());
        Assert.Null((row with { EndpointAddressFamily = 6, SourceEndpointPort = 50_000 }).Validate());
        Assert.Contains("family 6", (row with { EndpointAddressFamily = 4, SourceEndpointAddressV6 = UInt128.One }).Validate(),
            StringComparison.Ordinal);
        Assert.Contains("family 6", (row with { SourceEndpointAddressV6 = UInt128.One }).Validate(), StringComparison.Ordinal);
        Assert.Contains("128 bits", (row with { EndpointAddressFamily = 6, DestinationEndpointAddress = 1 }).Validate(),
            StringComparison.Ordinal);
        Assert.Contains("states the family", (row with { SourceEndpointAddress = 1 }).Validate(), StringComparison.Ordinal);

        // A row never holds an address in both pairs of columns, whichever family it declares.
        foreach (byte family in new byte[] { 4, 6 })
        {
            Assert.NotNull((row with
            {
                EndpointAddressFamily = family,
                SourceEndpointAddress = 0x7F00_0001,
                DestinationEndpointAddressV6 = UInt128.One,
            }).Validate());
        }
    }

    [Fact(DisplayName = "I15: a generation publishes observation-v1 and observation-v2 segments side by side, and reopens both")]
    public void AGenerationPublishesBothTables()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] narrow = Spread(3);
        ObservationRowV1[] wide =
        [
            Ipv6(Row(2_000, bytes: 5, ordinal: 10), DocumentationHost, 50_000, LinkLocalHost, 443),
            Row(2_001, bytes: 6, ordinal: 11),
        ];

        DerivedGenerationResult published = Publish(
            session.Store,
            [.. narrow, .. wide],
            new DerivedGenerationOptions { RowsPerSegment = 3 });

        SessionManifestV1 manifest = published.Manifest;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        Assert.Equal([SegmentTableId.ObservationV1, SegmentTableId.ObservationV2], segments.Select(segment => segment.Table));
        Assert.Equal(
            [.. narrow, .. wide],
            segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.Row))
                .Select(row => row with { JournalRecordIndex = null }));

        SessionStore reopened = session.Reopen();
        Assert.Equal(manifest.Generation, reopened.Current!.Generation);
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        SegmentReaderV1 last = SessionSegments.Open(reopened.Root, reopened.Current!, SessionSegments.Names(reopened.Current!)[^1]);
        Assert.Equal(DocumentationHost, last.Row(0).SourceEndpointAddressV6);
    }

    /// <summary>The same record with an IPv6 endpoint pair: its own endpoint first, then the remote one.</summary>
    private static ObservationRowV1 Ipv6(ObservationRowV1 row, UInt128 local, ushort localPort, UInt128 remote, ushort remotePort) =>
        row with
        {
            EndpointAddressFamily = 6,
            SourceEndpointAddress = null,
            DestinationEndpointAddress = null,
            SourceEndpointAddressV6 = local,
            SourceEndpointPort = localPort,
            DestinationEndpointAddressV6 = remote,
            DestinationEndpointPort = remotePort,
        };

    /// <summary>What a writer stages for rows of no resource name under a table layout (`SegmentTableBuilder.StagedBytes`).</summary>
    private static long Staged(IReadOnlyList<SegmentColumnSpec> columns, int rows) =>
        SegmentFormatV1.HeaderLength + (columns.Count * (long)SegmentFormatV1.ColumnEntryLength) + SegmentFormatV1.TrailerLength
        + ((long)rows * columns.Sum(column => SegmentFormatV1.WidthOf(column.Type)))
        + ((long)columns.Count(column => column.Nullable) * ((rows + 7) / 8));
}

internal static class SegmentWriterTestExtensions
{
    /// <summary>Applies <paramref name="action"/> to a writer and returns it, so a one-row segment reads as one expression.</summary>
    public static SegmentWriterV1 Tap(this SegmentWriterV1 writer, Action<SegmentWriterV1> action)
    {
        action(writer);
        return writer;
    }
}
