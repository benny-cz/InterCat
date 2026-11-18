using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §13.2's layer accounting (I11, §5.3): evidence of a higher layer laid over the transport that carried it - RPC calls
/// over the ALPC messages of their exchanges - adds nothing to any transport metric, and counts at its own layer.
/// </summary>
public sealed class LayerAccountingTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "I11: RPC calls laid over the ALPC messages that carried them add no transport byte, record, ranking or lane, and count at their own layer")]
    public void RpcCallsOverAlpcAddNothingToTransport()
    {
        // The transport: a TCP exchange whose sizes are known, and the ALPC messages of two calls, which carry none.
        ObservationRowV1[] transport =
        [
            Timed(Lifecycle(1, ObservationKind.Create, 400, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 1_960, 2)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 300, 400, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(60, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 1_960, 4).Between("127.0.0.1:8080", "127.0.0.1:50000")),
            Timed(Alpc(102, ObservationKind.Send, 400, 401, 60)),
            Timed(Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61)),
            Timed(Alpc(302, ObservationKind.Send, 400, 401, 62)),
            Timed(Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63)),
        ];
        using var session = new TemporarySession();
        Publish(session.Store, transport,
            fields:
            [
                Field(transport[4], SourceField.AlpcMessageId, 21),
                Field(transport[5], SourceField.AlpcMessageId, 21),
                Field(transport[6], SourceField.AlpcMessageId, 22),
                Field(transport[7], SourceField.AlpcMessageId, 22),
            ]);
        string before = Transport(session.Store);
        Assert.Contains("(300, 1, 0)", before, StringComparison.Ordinal);

        // A later generation lays the two calls over those messages, at the caller and at the server, each request
        // carrying its own payload's size at the application layer.
        Publish(session.Store,
        [
            Payload(Timed(RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), ServiceControl))),
            Timed(RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0)),
            Payload(Timed(RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(3), ServiceControl))),
            Timed(RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(3), status: 0)),
            Timed(RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, Activity(11), ServiceControl)),
            Timed(RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, Activity(11), status: 0)),
            Timed(RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, Activity(13), ServiceControl)),
            Timed(RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, Activity(13), status: 0)),
        ]);
        Assert.Equal(before, Transport(session.Store));

        // The calls are there, at their own layer: four starts, and eight records the transport layer does not count.
        Assert.Equal(4L, SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.LogicalOperations,
            Metric = Metric.OperationsStarted,
        }).Value);
        Assert.Equal(8L, SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Observations,
            Layer = ObservationLayer.Application,
        }).Value);
    }

    /// <summary>
    /// Every transport answer of the session's current generation, in words, but which generation it is: the bytes sent,
    /// received and of endpoint activity at the transport layer, its records, each process's transport bytes over the
    /// session - read from its persisted index - and over an interval - read from its segments - and each transport's byte
    /// lane.
    /// </summary>
    private static string Transport(SessionStore store) => AnswerText.Of(
        new object?[]
        {
            Measured(store, Metric.BytesSent, AccountingSide.SendSide),
            Measured(store, Metric.BytesReceived, AccountingSide.ReceiveSide),
            Measured(store, Metric.EndpointActivityBytes, AccountingSide.EndpointActivity),
            SessionMetrics.Evaluate(store, new MetricRequest
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = Metric.Observations,
                Layer = ObservationLayer.Transport,
            }).Value,
            SessionByteRanking.Measure(store, null),
            SessionByteRanking.Measure(store, new TimeRange(0, 400)),
            SessionIntervalByteQuery.MeasureByMechanism(store, new TimeRange(0, 400), 8, [Mechanism.Tcp, Mechanism.Alpc]),
        },
        "SessionId",
        "Generation",
        "ManifestDigest");

    /// <summary>A byte metric at the transport layer: its sum and how many contributions it knew and did not.</summary>
    private static string Measured(SessionStore store, Metric metric, AccountingSide side)
    {
        MetricResult result = SessionMetrics.Evaluate(store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = metric,
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = side,
            Layer = ObservationLayer.Transport,
        });
        return $"{metric} ({result.Value}, {result.KnownContributions}, {result.UnknownContributions})";
    }

    /// <summary>A call's request with its own payload's size, measured at the application layer.</summary>
    private static ObservationRowV1 Payload(ObservationRowV1 row) => row with
    {
        ByteValue = 4_096,
        ByteDomain = ByteDomain.ApplicationPayload,
        AccountingSide = AccountingSide.SendSide,
        MeasurementUnit = MeasurementUnit.Bytes,
        ByteAvailability = FieldAvailability.Present,
    };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 7);
}
