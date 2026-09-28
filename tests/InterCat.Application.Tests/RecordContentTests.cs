using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// What a record holds of its message, and why nothing (§3.7, §11): each admitted source says what it carries instead, and
/// which source could hold the bytes when one is known.
/// </summary>
public sealed class RecordContentTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§3.7: a record says why it holds no content, and which source could, by what its source carries")]
    public void EachSourceSaysWhyItHoldsNoContent()
    {
        RecordContent tcp = RecordContent.Of(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1));
        Assert.False(tcp.Recorded);
        Assert.Equal(
            "None. The kernel's network events carry endpoints and a transfer's size, never the bytes sent. "
            + "A packet capture could hold them, and encrypted traffic stays encrypted there. A Content capture of a WinINet "
            + "client's process keeps its HTTP messages above the encryption.",
            tcp.Describe());
        Assert.Equal(tcp, RecordContent.Of(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1) with { Mechanism = Mechanism.Udp }));

        RecordContent rpc = RecordContent.Of(RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10));
        Assert.StartsWith("None. RPC's events carry a call's interface, procedure and status, never its arguments.", rpc.Describe(),
            StringComparison.Ordinal);
        Assert.NotNull(rpc.Elsewhere);

        RecordContent alpc = RecordContent.Of(Alpc(102, ObservationKind.Send, 400, 401, 60));
        Assert.Equal(("None. ALPC's kernel events carry a message id, never the message.", (string?)null), (alpc.Describe(), alpc.Elsewhere));

        Assert.Equal("None. A lifecycle record describes a process or thread, not a message.",
            RecordContent.Of(Lifecycle(1, ObservationKind.Create, 400, 1)).Describe());

        // A redacted package's record is synthetic, whatever its mechanism.
        Assert.StartsWith("None. A redacted package keeps no body or content",
            RecordContent.Of(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1), synthetic: true).Describe(),
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.7: an original record's view says what the record holds of its message, apart from the event's body")]
    public void TheOriginalRecordSaysWhatItHoldsOfItsMessage()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd)],
            bodyForRow: _ => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 5,
                Bytes = EnvelopeBuffer.CopyOf([1, 2, 3, 4, 5]),
            });
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord selected = Assert.Single(page.Records);

        SessionRawRecordDetail detail = SessionRawRecordQuery.Read(session.Store, page.SessionId, page.Generation, selected);
        Assert.True(detail.Available);
        Assert.Equal(RecordContent.Of(selected.Observation), detail.Content);
    }
}
