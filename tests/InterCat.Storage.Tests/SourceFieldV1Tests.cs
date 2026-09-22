using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>The second segment-v1 table carries provider facts without rewriting the observation table.</summary>
public sealed class SourceFieldV1Tests
{
    private static readonly SegmentIdentityV1 Identity = new()
    {
        CaptureId = new CaptureId(Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")),
        ClockId = new ClockId(Guid.Parse("11112222-3333-4444-8555-666677778888")),
        TimestampEncoding = TimestampEncoding.Qpc,
        Derivation = NormalizerContractVersion.V1,
    };

    [Fact(DisplayName = "R1: source fields round-trip with their observation identity and deterministic order")]
    public void FieldsRoundTripInCanonicalOrder()
    {
        SourceFieldRowV1 sequence = Row(SourceField.ProcessStartSequence, 91, 7);
        SourceFieldRowV1 parent = Row(SourceField.ParentProcessId, 42, 7);
        SourceFieldRowV1 unknown = Row(SourceField.ConnectionId, null, 8) with
        {
            Availability = FieldAvailability.EventLost,
        };

        SegmentBuildResult forward = Build([sequence, parent, unknown]);
        SegmentBuildResult reversed = Build([unknown, parent, sequence]);
        Assert.Equal(forward.Segment, reversed.Segment);
        Assert.Equal(SegmentTableId.SourceFieldsV1, SegmentReaderV1.Open(forward.Segment, forward.Dictionaries).Table);
        SegmentReaderV1 reader = SegmentReaderV1.Open(forward.Segment, forward.Dictionaries);
        Assert.Equal([sequence, parent, unknown], Enumerable.Range(0, reader.RowCount).Select(reader.FieldRow));
        Assert.Throws<InvalidOperationException>(() => reader.Row(0));
    }

    [Fact(DisplayName = "I2: one observation cannot publish the same source field twice")]
    public void DuplicateFieldIsRefused()
    {
        var writer = new SourceFieldWriterV1(Identity, 0);
        writer.Add(Row(SourceField.ProcessStartSequence, 1, 7));
        writer.Add(Row(SourceField.ProcessStartSequence, 2, 7));
        Assert.Contains("same field", Assert.Throws<InvalidOperationException>(() => writer.Build()).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a source field value and its availability cannot disagree")]
    public void AvailabilityCannotContradictValue()
    {
        Assert.Contains("`Present` is not a reason", Row(SourceField.ConnectionId, null, 7).Validate(), StringComparison.Ordinal);
        Assert.Contains("also reports it as", (Row(SourceField.ConnectionId, 2, 7) with
        {
            Availability = FieldAvailability.EventLost,
        }).Validate(), StringComparison.Ordinal);
    }

    private static SegmentBuildResult Build(IReadOnlyList<SourceFieldRowV1> rows)
    {
        var writer = new SourceFieldWriterV1(Identity, 0);
        foreach (SourceFieldRowV1 row in rows)
        {
            writer.Add(row);
        }

        return writer.Build();
    }

    private static SourceFieldRowV1 Row(SourceField field, long? value, ulong ordinal) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        FactKey = FactKey.Create("source-field-test"),
        NativeTicks = (long)ordinal * 10,
        Field = field,
        Value = value,
        Availability = FieldAvailability.Present,
    };
}
