using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// What a report counts, in words that agree with each count: a recording's publications and an import's spill, which
/// only a Windows capture or a real trace makes, said here from the counts alone.
/// </summary>
public sealed class ReportCountsTests
{
    [Theory(DisplayName = "§20.1: icat record says it published nothing, once when the capture stopped, or a generation for each journal chunk")]
    [InlineData(0, "nothing")]
    [InlineData(1, "once, when the capture stopped")]
    [InlineData(3, "3 generations, one journal chunk each; the last holds them all")]
    public void ARecordingSaysHowItPublished(int publications, string said) =>
        Assert.Equal(said, RecordCommand.Published(publications));

    [Theory(DisplayName = "§18.4: icat import says it spilled nothing, or how many runs and bytes, each in words that agree with its count")]
    [InlineData(0, 0L, "nothing; the index fitted in memory")]
    [InlineData(1, 1L, "1 run, 1 byte")]
    [InlineData(2, 4_096L, "2 runs, 4,096 bytes")]
    public void AnImportSaysWhatItSpilled(int runs, long bytes, string said) =>
        Assert.Equal(said, ImportCommand.Spilled(new ImportSpillDocument { RunsWritten = runs, BytesSpilled = bytes }));
}
