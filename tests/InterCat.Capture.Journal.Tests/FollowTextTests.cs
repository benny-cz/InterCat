using System.Globalization;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>A follow's counts in the words the window's card and icat share (R18): one of anything in the singular.</summary>
public sealed class FollowTextTests
{
    [Fact(DisplayName = "R18: a follow counts its published chunks and records as the card does, one in the singular")]
    public void AFollowCountsInTheSingular()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        Assert.Equal(["0 published chunks", "1 published chunk", "1,234 published chunks"],
            new long[] { 0, 1, 1_234 }.Select(count => FollowText.Chunks(count, culture)));
        Assert.Equal(["0 records", "1 record", "3,210 records"],
            new long[] { 0, 1, 3_210 }.Select(count => FollowText.Records(count, culture)));
        Assert.Equal("1 of 1 published chunk derived, 1 record so far.", FollowText.Progress(Step(1, 1, 1), culture));
        Assert.Equal("12 of 40 published chunks derived, 3,210 records so far.", FollowText.Progress(Step(12, 40, 3_210), culture));
    }

    [Fact(DisplayName = "R18: a compaction not published is said once, in words that keep the session whole, wherever it is said, and why last")]
    public void ACompactionNotPublishedIsSaidInOneWay()
    {
        Assert.Equal(
            "A compaction was not published; the session is whole, with more segments than it needs, and icat compact "
                + "coalesces them. It failed: There is not enough space on the disk.",
            FollowText.CompactionFailed(" There is not enough space on the disk. "));
        Assert.Throws<ArgumentException>(() => FollowText.CompactionFailed(" "));
    }

    private static FollowStep Step(int derived, int evidence, long records) => new()
    {
        MirroredChunks = 1,
        MirroredRecords = records,
        DerivedChunks = derived,
        EvidenceChunks = evidence,
        DerivedRecords = records,
        Finished = false,
        DerivedGeneration = derived,
        Compactions = 0,
    };
}
