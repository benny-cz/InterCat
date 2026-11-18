using InterCat.Domain;

namespace InterCat.Analysis.Tests;

/// <summary>
/// FX-QUERY-001's corpus: one request of each shape the specification canonicalizes (§10.5), its two processes named by
/// the caller - the corpus's own fixed ones, or a session's - so the same shapes are asked of the specification and of
/// <c>icat metric</c> alike (R18). The analysis and CLI tests both compile it.
/// </summary>
internal static class QueryCorpus
{
    public static IEnumerable<(string Name, MetricRequest Request)> Of(ProcessInstanceId focus, ProcessInstanceId counterpart) =>
    [
        ("observations-whole-capture", Request(Metric.Observations)),
        ("bytes-sent-in-an-interval", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Interval = new TimeRange(1_200_000_000, 1_800_000_000),
        }),
        ("requested-io-with-its-domain-implied", Request(Metric.RequestedIoBytes, accountingSide: AccountingSide.SendSide)),
        ("application-payload-with-its-layer-implied", Request(Metric.ApplicationPayloadBytes, accountingSide: AccountingSide.ReceiveSide)),
        ("endpoint-activity-with-its-side-implied", Request(Metric.EndpointActivityBytes, ByteDomain.TransportObserved)),
        ("rate-of-received-bytes", Request(Metric.Rate, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with
        {
            RateNumerator = Metric.BytesReceived,
            Interval = new TimeRange(0, 10_000_000),
        }),
        ("observations-by-mechanism", Request(Metric.Observations) with { Grouping = LaneGrouping.Mechanism, Layer = ObservationLayer.Transport }),
        ("sent-bytes-by-process-top-20", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Grouping = LaneGrouping.InstanceOnly,
            RequestedRows = 20,
        }),
        ("observations-by-executable-with-candidates", Request(Metric.Observations) with
        {
            Grouping = LaneGrouping.Executable,
            EvidencePolicy = EvidencePolicy.IncludeCandidates,
        }),
        ("owner-observations", Request(Metric.Observations) with { Owner = focus }),
        ("participant-conversation-by-peer", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Participant = focus,
            Grouping = LaneGrouping.Peer,
            Mechanism = Mechanism.Tcp,
        }),
        ("sender-to-one-peer-measured-at-receivers", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide) with
        {
            Sender = focus,
            Peer = counterpart,
        }),
        ("between-two-processes-written-first-to-second", Request(Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide) with
        {
            Between = new([counterpart], [focus], BetweenDirection.SecondToFirst),
        }),
        ("channels-over-every-process-name-the-binding-rule", Request(Metric.ActiveChannels)),
        ("operations-started-name-the-operation-rule", Request(Metric.OperationsStarted) with { Basis = AnalysisBasis.LogicalOperations }),
        ("failed-calls-of-one-owner-in-an-interval", Request(Metric.Errors) with
        {
            Basis = AnalysisBasis.LogicalOperations,
            Owner = focus,
            Interval = new TimeRange(1_200_000_000, 1_800_000_000),
        }),
        ("completed-calls-by-process-top-10", Request(Metric.OperationsCompleted) with
        {
            Basis = AnalysisBasis.LogicalOperations,
            Grouping = LaneGrouping.InstanceOnly,
            RequestedRows = 10,
        }),
        ("client-call-duration-with-its-cohort-and-statistic-implied", Request(Metric.Duration) with
        {
            Basis = AnalysisBasis.LogicalOperations,
            DurationInterval = DurationInterval.ClientCall,
        }),
        ("served-p95-of-calls-started-by-process", Request(Metric.Duration) with
        {
            Basis = AnalysisBasis.LogicalOperations,
            DurationInterval = DurationInterval.ServerExecution,
            Cohort = OperationCohort.StartedInRange,
            Statistic = DurationStatistic.Percentile95,
            Grouping = LaneGrouping.InstanceOnly,
        }),
    ];

    private static MetricRequest Request(
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null) => new()
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = metric,
            ByteDomain = byteDomain,
            AccountingSide = accountingSide,
        };
}
