using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The SDK's announcements (ADR-111): each process's schema, library and clock, read from the SDK's own provider's records,
/// a process's equal announcements once - as each session enabling the provider is told them again.
/// </summary>
public sealed class CooperativeAnnouncementTests
{
    private static readonly Guid FixtureProvider = Guid.Parse("2d8f2e6c-6e0a-5a0a-9c48-1f1d0e5d7c11");

    [Fact(DisplayName = "§15: each process's announcement of the SDK it speaks is read once - its schema's major and minor, its library and its clock - one whose minor the capture did not keep says none, and another provider's record of the same number is none")]
    public void EachProcessAnnouncesItsSchemaOnce()
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1),
            Lifecycle(2, ObservationKind.Create, 200, 2),
            Lifecycle(3, ObservationKind.Create, 300, 3),
        };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 10;
        void Announce(long ticks, int process, string library, long? minor, long frequency, Guid? provider = null)
        {
            ObservationRowV1 row = SdkRecord(ticks, CooperativeSdk.SdkStarted, process, ++ordinal, null) with
            {
                Kind = ObservationKind.Inventory,
                ResourceName = library,
                ProviderId = provider ?? CooperativeSdk.ProviderId,
            };
            rows.Add(row);
            if (minor is { } kept) fields.Add(Field(row, SourceField.SdkSchemaMinor, kept));
            fields.Add(Field(row, SourceField.SourceClockFrequency, frequency));
        }

        // The .NET process tells two sessions its schema; the native one a later minor; a third's minor was not kept.
        Announce(10, 100, "dotnet-1.2", 2, 10_000_000);
        Announce(20, 100, "dotnet-1.2", 2, 10_000_000);
        Announce(30, 200, "native-1.3", 3, 3_000_000);
        Announce(40, 300, "native-1.0", null, 3_000_000);
        Announce(50, 100, "fixture", 9, 1, FixtureProvider);
        using var session = new TemporarySession();
        _ = Publish(session.Store, rows, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = IncrementalDerivationTests.SegmentsOf(session.Store);
        ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        IReadOnlyList<CooperativeAnnouncement> announced = CooperativeAnnouncements.Read(observations, fieldSegments, instances);

        Assert.Equal(
            [(100, 1, (int?)2, "dotnet-1.2", (long?)10_000_000), (200, 1, (int?)3, "native-1.3", (long?)3_000_000),
                (300, 1, (int?)null, "native-1.0", (long?)3_000_000)],
            announced.Select(announcement => (announcement.ProcessId, announcement.Major, announcement.Minor, announcement.Library,
                announcement.TicksPerSecond)));
        Assert.All(announced, announcement => Assert.True(announcement.Process.IsBound));
    }
}
