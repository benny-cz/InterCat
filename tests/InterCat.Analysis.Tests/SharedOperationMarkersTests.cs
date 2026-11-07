using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The markers two captures share through the cooperative SDK's operations (§8.2, ADR-108): an operation one capture recorded
/// starting under one the other recorded starting is a record known to follow the other's, read from each capture's records
/// as a capture admits them, its instant in its own session time.
/// </summary>
public sealed class SharedOperationMarkersTests
{
    private static readonly Guid Request = Guid.Parse("3a7c1e52-8b1d-4f0e-9a64-0c2b5d7e9f11");
    private static readonly Guid Served = Guid.Parse("4b8d2f63-9c2e-4a1f-8b75-1d3c6e8fa022");
    private static readonly Guid Reply = Guid.Parse("5c9e3a74-ad3f-4b2a-9c86-2e4d7f9ab133");
    private static readonly Guid Answered = Guid.Parse("6daf4b85-be4a-4c3b-8d97-3f5e8a1bc244");
    private static readonly Guid Local = Guid.Parse("7eb05c96-cf5b-4d4c-9ea8-4a6f9b2cd355");
    private static readonly Guid Elsewhere = Guid.Parse("8fc16da7-d06c-4e5d-8fb9-5b7a1c3de466");
    private static readonly Guid Unrecorded = Guid.Parse("90d27eb8-e17d-4f6e-90ca-6c8b2d4ef577");
    private static readonly Guid Inner = Guid.Parse("a1e38fc9-f28e-4a7f-a1db-7d9c3e5fa688");

    [Fact(DisplayName = "§8.2: an operation one capture recorded starting under one another capture recorded starting orders the two clocks, its start after its parent's - both ways, each start its own ordering, held to its parent's earliest start - and a start under its own capture's operation, under none, or under one neither recorded orders nothing between two")]
    public void StartsUnderTheOthersOperationsOrderTheClocks()
    {
        // The reference started a request, which the member served under it; the member started a reply, which the reference
        // answered under it. Each capture also started one under its own, and one under an operation neither recorded.
        using var reference = new TemporarySession();
        using var member = new TemporarySession();
        CooperativeOperationStart[] referenceStarts = StartsOf(reference.Store,
        [
            (1_000, Request, null), (1_200, Request, null), (3_000, Answered, Reply), (3_500, Local, Request), (3_600, Elsewhere, Unrecorded),
        ]);
        CooperativeOperationStart[] memberStarts = StartsOf(member.Store,
        [
            (2_000, Served, Request), (2_100, Reply, null), (2_200, Inner, Reply),
        ]);

        // Each start read with its parent, its instant its capture's own: a tick of the test clock is 100 ns.
        Assert.Equal(
            [
                new CooperativeOperationStart(Request, null, 100_000), new CooperativeOperationStart(Request, null, 120_000),
                new CooperativeOperationStart(Answered, Reply, 300_000), new CooperativeOperationStart(Local, Request, 350_000),
                new CooperativeOperationStart(Elsewhere, Unrecorded, 360_000),
            ],
            referenceStarts);

        // The member's serving followed the reference's request - its earliest start - and the reference's answer followed the
        // member's reply; nothing else orders the two.
        Assert.Equal(
            [new MarkerOrdering(200_000, 100_000, MarkerOrder.MemberAfter), new MarkerOrdering(210_000, 300_000, MarkerOrder.ReferenceAfter)],
            SharedOperationMarkers.Between(memberStarts, referenceStarts));
    }

    [Fact(DisplayName = "§8.2: a start is the SDK's by its provider and names its operation - another provider's record of the same number, or a start naming no operation, is no marker")]
    public void OnlyTheSdksStartsAreRead()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            StartRecord(1_000, 1, Request, null),
            StartRecord(1_100, 2, Served, Request) with { ProviderId = Guid.Parse("2d8f2e6c-6e0a-5a0a-9c48-1f1d0e5d7c11") },
            StartRecord(1_200, 3, Guid.Empty, Request),
            StartRecord(1_300, 4, Reply, Guid.Empty),
        ];
        _ = Publish(session.Store, rows);
        (SegmentReaderV1[] observations, _) = IncrementalDerivationTests.SegmentsOf(session.Store);
        Assert.Equal([new CooperativeOperationStart(Request, null, 100_000), new CooperativeOperationStart(Reply, null, 130_000)],
            SharedOperationMarkers.Starts(observations, TestClock));
    }

    private static CooperativeOperationStart[] StartsOf(SessionStore store, (long Ticks, Guid Operation, Guid? Parent)[] starts)
    {
        ulong ordinal = 0;
        _ = Publish(store, [.. starts.Select(start => StartRecord(start.Ticks, ++ordinal, start.Operation, start.Parent))]);
        (SegmentReaderV1[] observations, _) = IncrementalDerivationTests.SegmentsOf(store);
        return [.. SharedOperationMarkers.Starts(observations, TestClock)];
    }

    /// <summary>An operation's start as a capture admits the SDK's: its identity the record's identifier, its parent its related activity.</summary>
    private static ObservationRowV1 StartRecord(long ticks, ulong ordinal, Guid operation, Guid? parent) =>
        SdkRecord(ticks, CooperativeSdk.OperationStarted, 100, ordinal, operation) with
        {
            Kind = ObservationKind.RequestStart,
            RelatedActivityId = parent,
        };
}
