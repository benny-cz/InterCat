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

    [Theory(DisplayName = "R21: icat record says what it did not admit by policy and by a source's gate, neither of them loss, and a gate that could follow no more")]
    [InlineData(0L, 0L, 0L, "6 by policy, which is not loss")]
    [InlineData(40L, 2L, 0L, "6 by policy and 42 by a source's gate, neither of which is loss")]
    [InlineData(40L, 2L, 1L, "6 by policy and 43 by a source's gate, neither of which is loss; the gate followed as many resources as it may, and not 1 more")]
    [InlineData(0L, 0L, 3L, "6 by policy and 3 by a source's gate, neither of which is loss; the gate followed as many resources as it may, and not 3 more")]
    public void ARecordingSaysWhatItDidNotAdmit(long notCounted, long noFollowedResource, long beyondBound, string said) =>
        Assert.Equal(said, RecordCommand.NotAdmitted(new Capture.Windows.CaptureHealthSnapshot
        {
            AdmittedRecords = 10,
            ObservedRecords = 16 + notCounted + noFollowedResource + beyondBound,
            PolicyOmissionsUnrequestedProvider = 1,
            PolicyOmissionsDescriptorNotAdmitted = 2,
            PolicyOmissionsDescriptorDenied = 3,
            UndecodableBodyShorterThanSchema = 0,
            UndecodableUnknownDescriptorVersion = 0,
            UndecodableAdmissionReadFailed = 0,
            UndecodablePointerWidthMismatch = 0,
            GateOmissionsNotCounted = notCounted,
            GateOmissionsNoFollowedResource = noFollowedResource,
            GateOmissionsBeyondBound = beyondBound,
            ProviderReportedEventLoss = 0,
            ConsumerReportedBufferLoss = 0,
            ApplicationDrops = 0,
            QuarantinedTimestamps = 0,
            QueueDepth = 0,
            QueueCapacity = 1,
        }));

    [Theory(DisplayName = "§18.4: icat import says it spilled nothing, or how many runs and bytes, each in words that agree with its count")]
    [InlineData(0, 0L, "nothing; the index fitted in memory")]
    [InlineData(1, 1L, "1 run, 1 byte")]
    [InlineData(2, 4_096L, "2 runs, 4,096 bytes")]
    public void AnImportSaysWhatItSpilled(int runs, long bytes, string said) =>
        Assert.Equal(said, ImportCommand.Spilled(new ImportSpillDocument { RunsWritten = runs, BytesSpilled = bytes }));

    [Theory(DisplayName = "§18.3: icat measure says how many admitted records its run left undecoded past its budget, in words that agree with them")]
    [InlineData(1, "1 admitted record was not decoded because the run budget of 500,000 was reached.")]
    [InlineData(2, "2 admitted records were not decoded because the run budget of 500,000 was reached.")]
    public void AMeasurementSaysWhatItLeftUndecoded(long overBudget, string said) =>
        Assert.Equal(said, MeasureCommand.NotDecoded(overBudget));
}
