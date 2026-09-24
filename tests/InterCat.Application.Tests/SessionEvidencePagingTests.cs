using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionEvidencePagingTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact]
    public void PagesFollowTheCanonicalRowOrderEvenWhenSegmentsOverlapInTime()
    {
        using var session = new TemporarySession();
        // Rows arrive out of reading order, so each two-row segment is sorted but their ranges interleave.
        Publish(session.Store,
        [
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 2).Between(ClientEnd, ServerEnd),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 3).Between(ClientEnd, ServerEnd),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 4).Between(ClientEnd, ServerEnd),
        ], rowsPerSegment: 2);
        Assert.Equal(2, Segments(session.Store).Count);

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 1, cursor: cursor);
            Assert.False(page.RestartRequired);
            seen.AddRange(page.Records.Select(record => record.Observation.NativeTicks));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([10L, 20L, 30L, 40L], seen);
        Assert.Equal([10L, 20L, 30L, 40L],
            SessionEvidenceQuery.Read(session.Store).Records.Select(record => record.Observation.NativeTicks));
    }

    [Fact]
    public void GroupScopeKeepsRowsOwnedByAnyMemberAndRefusesAnIncompleteGroup()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 3, 300, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId server = overview.Nodes.Single(node => node.ProcessId == 200).Id;

        SessionEvidencePage group = SessionEvidenceQuery.Read(session.Store, ownerProcesses: [server, client, client]);
        Assert.Equal([10L, 11L], group.Records.Select(record => record.Observation.NativeTicks));
        Assert.Equal(2, group.OwnerProcesses.Count);
        Assert.Null(group.OwnerProcessScope);
        Assert.All(group.Records, record => Assert.Contains(record.Owner!.Instance!.Value, new[] { client, server }));

        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, ownerProcesses: [client, server], pageSize: 1);
        SessionEvidencePage reordered = SessionEvidenceQuery.Read(session.Store, ownerProcesses: [server, client],
            pageSize: 1, cursor: first.NextCursor);
        Assert.False(reordered.RestartRequired);
        Assert.Equal(11L, reordered.Records.Single().Observation.NativeTicks);
        Assert.True(SessionEvidenceQuery.Read(session.Store, ownerProcesses: [client],
            cursor: first.NextCursor).RestartRequired);

        Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store,
            ownerProcesses: [client, new ProcessInstanceId(Guid.NewGuid())]));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, ownerProcesses: []));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: client, ownerProcesses: [server]));
    }

    [Fact]
    public void ResolvedOwnersNameTheirInstanceOrTheReasonThereIsNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(5, ObservationKind.Create, 100, 1),
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, null, 3).Between(ClientEnd, ServerEnd),
        ]);

        SessionEvidencePage plain = SessionEvidenceQuery.Read(session.Store);
        Assert.All(plain.Records, record => Assert.Null(record.Owner));
        SessionEvidencePage resolved = SessionEvidenceQuery.Read(session.Store, resolveOwners: true);
        SessionEvidenceOwner owned = resolved.Records.Single(record => record.Observation.NativeTicks == 10).Owner!;
        Assert.NotNull(owned.Instance);
        Assert.Equal(100, owned.ProcessId);
        Assert.True(owned.AdmittedUnderPolicy);
        SessionEvidenceOwner orphan = resolved.Records.Single(record => record.Observation.NativeTicks == 11).Owner!;
        Assert.Null(orphan.Instance);
        Assert.Equal(ProcessBindingReason.NoOwner, orphan.Reason);
        Assert.False(orphan.AdmittedUnderPolicy);
        Assert.Equal(plain.QueryIdentity, resolved.QueryIdentity);
    }

    [Fact]
    public void ACursorFromAnEarlierVersionOrAnotherSessionRestartsRatherThanShifting()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
        ]);
        SessionEvidencePage legacy = SessionEvidenceQuery.Read(session.Store,
            cursor: "v1." + new string('a', 64) + ".0.1");
        Assert.True(legacy.RestartRequired);
        Assert.Contains("earlier InterCat version", legacy.RestartReason, StringComparison.Ordinal);

        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, pageSize: 1);
        using var other = new TemporarySession();
        Publish(other.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
        ], capture: new CaptureId(Guid.NewGuid()));
        Assert.True(SessionEvidenceQuery.Read(other.Store, cursor: first.NextCursor).RestartRequired);

        string tampered = first.NextCursor!.Replace(".10.", ".x.", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, cursor: tampered));
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store,
            cursor: first.NextCursor + new string('0', 300)));
    }

    [Fact]
    public void AnOriginalRecordStaysReachableByIdentityAfterALiveGenerationSupersedesItsPage()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 9).Select(index =>
                Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, index, 100, (ulong)(index + 1))
                    .Between(ClientEnd, ServerEnd)),
        ];
        Publish(session.Store, rows, journalBatchRecords: 2,
            bodyForRow: row => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 1,
                Bytes = EnvelopeBuffer.CopyOf([(byte)row.RawRecordOrdinal]),
            });
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord middle = page.Records.Single(record => record.Observation.NativeTicks == 14);
        SessionEvidenceRecord last = page.Records.Single(record => record.Observation.NativeTicks == 18);

        // The next live publication carries every earlier chunk and adds one; the page's generation is gone.
        Publish(session.Store,
            [Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 10).Between(ClientEnd, ServerEnd)],
            journalBatchRecords: 2,
            bodyForRow: row => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 1,
                Bytes = EnvelopeBuffer.CopyOf([(byte)row.RawRecordOrdinal]),
            });
        Assert.Throws<InvalidOperationException>(() => SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, middle));

        SessionRawRecordDetail found = SessionRawRecordQuery.ReadRetained(session.Store, page.SessionId, middle,
            revealBodyBytes: true);
        Assert.True(found.Available);
        Assert.True(found.Generation > page.Generation);
        Assert.Equal(middle.ObservationId, found.ObservationId);
        Assert.Equal([(byte)5], found.BodyPreview!);
        Assert.Equal([(byte)9], SessionRawRecordQuery.ReadRetained(session.Store, page.SessionId, last,
            revealBodyBytes: true).BodyPreview!);

        SessionEvidenceRecord forged = middle with
        {
            Observation = middle.Observation with { NativeTicks = middle.Observation.NativeTicks + 1 },
        };
        Assert.Throws<InvalidDataException>(() => SessionRawRecordQuery.ReadRetained(session.Store,
            page.SessionId, forged));
        Assert.Throws<InvalidOperationException>(() => SessionRawRecordQuery.ReadRetained(session.Store,
            Guid.NewGuid(), middle));

        SessionEvidenceRecord absent = middle with
        {
            ObservationId = middle.ObservationId with
            {
                RawRecordId = middle.ObservationId.RawRecordId with { RecordOrdinal = 999 },
            },
        };
        SessionRawRecordDetail missing = SessionRawRecordQuery.ReadRetained(session.Store, page.SessionId, absent);
        Assert.False(missing.Available);
        Assert.Contains("outside the retained journal boundary", missing.UnavailableReason, StringComparison.Ordinal);
    }
}
