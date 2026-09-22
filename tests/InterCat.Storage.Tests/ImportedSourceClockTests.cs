using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class ImportedSourceClockTests
{
    private static readonly ImportSourceIdentity Etl =
        ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, "one recorded file"u8);

    [Fact(DisplayName = "I8: an imported clock identity comes from the evidence, so two imports agree")]
    public void ADerivedClockIsTheSameForTheSameEvidence()
    {
        SourceClockDescriptor first = ImportedSourceClock.Derive(Etl, 10_000_000, 1_234);
        SourceClockDescriptor second = ImportedSourceClock.Derive(Etl, 10_000_000, 9_999);

        // The anchor reading is where the file starts, not what the clock is: two imports of one file
        // must agree on its clock even if they sample a different first record.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.HostId, second.HostId);
        Assert.Equal(TimestampEncoding.Qpc, first.Encoding);
        Assert.Equal(10_000_000, first.TicksPerSecond);
        Assert.Equal(1_234, first.CaptureEpochNativeTicks);
    }

    [Fact(DisplayName = "I8: a different rate or a different file is a different clock")]
    public void ADerivedClockFollowsTheEvidenceItCameFrom()
    {
        ImportSourceIdentity other = ImportSourceIdentity.Of(ImportSourceKind.StandaloneEtl, "another file"u8);

        SourceClockDescriptor baseline = ImportedSourceClock.Derive(Etl, 10_000_000, 0);
        SourceClockDescriptor otherRate = ImportedSourceClock.Derive(Etl, 3_579_545, 0);
        SourceClockDescriptor otherFile = ImportedSourceClock.Derive(other, 10_000_000, 0);

        Assert.NotEqual(baseline.Id, otherRate.Id);
        Assert.NotEqual(baseline.Id, otherFile.Id);
        Assert.NotEqual(baseline.HostId, otherFile.HostId);

        // The rate is a property of the clock, not of the machine that recorded it, so a file's host
        // identity does not change when only its rate does.
        Assert.Equal(baseline.HostId, otherRate.HostId);
    }

    [Fact(DisplayName = "I8: imported evidence never claims the machine that is reading it")]
    public void ADerivedClockNeverClaimsTheLocalHost()
    {
        SourceClockDescriptor derived = ImportedSourceClock.Derive(Etl, 10_000_000, 0);

        Assert.NotEqual(HostId.ForLocalMachine(), derived.HostId);
        Assert.NotEqual(Guid.Empty, derived.HostId.Value);
        Assert.NotEqual(Guid.Empty, derived.Id.Value);
    }

    [Theory(DisplayName = "I8: a clock with no source or no rate is refused rather than defaulted")]
    [InlineData(0)]
    [InlineData(-1)]
    public void ADerivedClockNeedsARate(long ticksPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImportedSourceClock.Derive(Etl, ticksPerSecond, 0));
        Assert.Throws<ArgumentException>(() => ImportedSourceClock.Derive(default, 10_000_000, 0));
    }

    [Fact(DisplayName = "I2: records keyed against a derived clock keep one identity across imports")]
    public void RecordsKeyedAgainstADerivedClockKeepOneIdentity()
    {
        SourceClockDescriptor clock = ImportedSourceClock.Derive(Etl, 10_000_000, 0);
        using var record = new RecordEnvelopeV1
        {
            CaptureId = CaptureId.New(),
            StreamId = 1,
            SourceEpoch = 1,
            RecordOrdinal = 5,
            Header = new(Guid.Empty, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, Guid.Empty, Guid.Empty),
            BufferContext = new(0, 0),
            ClockId = clock.Id,
            TimestampEncoding = clock.Encoding,
            NativeTicks = 777,
            PointerSize = 8,
            SchemaReference = null,
            AdmissionPolicyReference = 1,
            ExtendedItems = [],
            OmittedExtendedItemCount = 0,
            Body = BodyV1.None,
        };

        CanonicalRecordKey first = CanonicalRecordKeyBuilder.Create(Etl, record);
        using RecordEnvelopeV1 reread = record with { RecordOrdinal = 99 };
        CanonicalRecordKey second = CanonicalRecordKeyBuilder.Create(Etl, reread);

        // A second import of the same file reads the same record at a different delivery position. The
        // clock is derived, not minted, so the record keeps one identity across both.
        Assert.Equal(first, second);
    }
}
