using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The words a retained journal envelope is read in, by the window and by icat raw alike (R5, R18): every code in words,
/// never an enumeration's name, and every length counted in the singular where it is one.
/// </summary>
public sealed class RawRecordTextTests
{
    [Fact(DisplayName = "R5: an envelope's codes read as words - its encodings, its body's classification and disposition - and none recorded says so")]
    public void CodesReadAsWords()
    {
        Assert.Equal(
            ["unknown", "QPC (performance counter)", "UTC file time", "ETW system time, converted", "CPU cycle counter", "unknown"],
            new TimestampEncoding?[]
            {
                TimestampEncoding.Unknown, TimestampEncoding.Qpc, TimestampEncoding.FileTimeUtc,
                TimestampEncoding.EtwSystemTimeConverted, TimestampEncoding.CpuCycleCounter, null,
            }.Select(RawRecordText.Encoding));
        Assert.Equal("approved metadata", RawRecordText.Words<BodyClassificationV1>(BodyClassificationV1.ApprovedMetadata));
        Assert.Equal("omitted unapproved classification",
            RawRecordText.Words<BodyDispositionV1>(BodyDispositionV1.OmittedUnapprovedClassification));
        Assert.Equal("none", RawRecordText.Words<BodyClassificationV1>(BodyClassificationV1.None));
        Assert.Equal("not recorded", RawRecordText.Words<BodyDispositionV1>(null));
    }

    [Fact(DisplayName = "R5: an extended item's lengths are counted, one byte in the singular")]
    public void AnExtendedItemCountsItsBytes()
    {
        Assert.Equal("item type 3, flags 1: original 12 bytes, retained 1 byte",
            RawRecordText.ExtendedItem(new RawExtendedItemSummary(3, 1, 12, 1)));
        Assert.Equal("item type 7, flags 0: original 1 byte, retained 0 bytes",
            RawRecordText.ExtendedItem(new RawExtendedItemSummary(7, 0, 1, 0)));
    }

    [Fact(DisplayName = "§3.7: a revealed preview reads as inert hexadecimal rows of sixteen bytes, each led by its offset")]
    public void APreviewReadsInRowsOfSixteen()
    {
        byte[] bytes = [.. Enumerable.Range(0, 18).Select(value => (byte)value)];
        Assert.Equal(["0000: 000102030405060708090A0B0C0D0E0F", "0010: 1011"], RawRecordText.PreviewRows(bytes));
        Assert.Empty(RawRecordText.PreviewRows([]));
    }
}
