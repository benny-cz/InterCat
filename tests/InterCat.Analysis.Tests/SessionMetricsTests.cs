using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// §5.3's metric compatibility matrix and the §21.1 accounting scenarios it governs. The matrix is a compiler
/// here rather than a document: a metric outside its basis is rejected with the compatible alternatives named,
/// and a metric the matrix permits but this session cannot derive is reported as unavailable with what it
/// needs — never approximated from what happens to be there.
/// </summary>
public sealed class SessionMetricsTests
{
    [Fact(DisplayName = "R2: a metric outside its basis is rejected with the compatible alternatives named")]
    public void AMetricOutsideItsBasisIsRejected()
    {
        MetricRejection observations = Reject(AnalysisBasis.LogicalOperations, Metric.Observations);
        Assert.Contains("count one exchange twice", observations.Reason, StringComparison.Ordinal);
        Assert.Contains(Metric.OperationsStarted, observations.CompatibleMetrics);
        Assert.DoesNotContain(Metric.Observations, observations.CompatibleMetrics);

        MetricRejection started = Reject(AnalysisBasis.SourceObservations, Metric.OperationsStarted);
        Assert.Contains("renamed as a call", started.Reason, StringComparison.Ordinal);
        Assert.Contains(Metric.Observations, started.CompatibleMetrics);

        MetricRejection duration = Reject(AnalysisBasis.SourceObservations, Metric.Duration);
        Assert.Contains("point fact with no interval", duration.Reason, StringComparison.Ordinal);

        MetricRejection capacity = Reject(AnalysisBasis.SourceObservations, Metric.MappingCapacity);
        Assert.Contains("never traffic", capacity.Reason, StringComparison.Ordinal);

        MetricRejection topologyBytes = Reject(
            AnalysisBasis.ResourceTopology,
            Metric.BytesSent,
            ByteDomain.TransportObserved,
            AccountingSide.SendSide);
        Assert.Contains("cannot invent a traffic value", topologyBytes.Reason, StringComparison.Ordinal);
        Assert.Contains(Metric.MappingCapacity, topologyBytes.CompatibleMetrics);
    }

    [Fact(DisplayName = "P3: a byte metric names one traffic domain, and another metric's domain is never relabelled")]
    public void AByteMetricNamesOneTrafficDomain()
    {
        MetricRejection none = Reject(
            AnalysisBasis.SourceObservations,
            Metric.BytesSent,
            byteDomain: null,
            AccountingSide.SendSide);
        Assert.Contains("there is no default", none.Reason, StringComparison.Ordinal);
        Assert.Contains("TransportObserved or CompletedIo", none.Reason, StringComparison.Ordinal);

        // Requested lengths are their own metric. Asking for them as sent bytes would be the relabelling P3 names.
        MetricRejection requestedAsSent = Reject(
            AnalysisBasis.SourceObservations,
            Metric.BytesSent,
            ByteDomain.RequestedIo,
            AccountingSide.SendSide);
        Assert.Contains("measured by RequestedIoBytes", requestedAsSent.Reason, StringComparison.Ordinal);

        MetricRejection capacityAsTraffic = Reject(
            AnalysisBasis.SourceObservations,
            Metric.EndpointActivityBytes,
            ByteDomain.Capacity);
        Assert.Contains("measured by MappingCapacity", capacityAsTraffic.Reason, StringComparison.Ordinal);

        MetricRejection relabelled = Reject(
            AnalysisBasis.SourceObservations,
            Metric.RequestedIoBytes,
            ByteDomain.TransportObserved,
            AccountingSide.SendSide);
        Assert.Contains("exactly what P3 forbids", relabelled.Reason, StringComparison.Ordinal);

        // A fixed-domain metric that names its own domain, or none, is fine: the metric already says which.
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, ByteDomain.RequestedIo, AccountingSide.SendSide).Check());
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, null, AccountingSide.SendSide).Check());
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.CompletedIo, AccountingSide.SendSide).Check());

        MetricRejection noBytes = Reject(
            AnalysisBasis.SourceObservations,
            Metric.Observations,
            ByteDomain.TransportObserved);
        Assert.Contains("measures no bytes", noBytes.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R2: a byte metric names its accounting, and a canonical owner is a request rather than a guess")]
    public void AByteMetricNamesItsAccounting()
    {
        MetricRejection none = Reject(
            AnalysisBasis.SourceObservations,
            Metric.BytesSent,
            ByteDomain.TransportObserved,
            accountingSide: null);
        Assert.Contains("unexplained volume number", none.Reason, StringComparison.Ordinal);

        MetricRejection side = Reject(
            AnalysisBasis.SourceObservations,
            Metric.Observations,
            accountingSide: AccountingSide.SendSide);
        Assert.Contains("no observation side", side.Reason, StringComparison.Ordinal);

        // The matrix permits a canonical-owner total. Whether a session can answer it is a different question,
        // asked of the session, and the answer there is "not until a transfer association is proven".
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.CanonicalOwner).Check());
    }

    [Fact(DisplayName = "I6: endpoint activity is a metric of its own, and a one-direction metric cannot be accounted as it")]
    public void EndpointActivityIsItsOwnMetric()
    {
        MetricRejection contradiction = Reject(
            AnalysisBasis.SourceObservations,
            Metric.BytesSent,
            ByteDomain.TransportObserved,
            AccountingSide.EndpointActivity);
        Assert.Contains("names one direction and endpoint activity counts both", contradiction.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(Metric.EndpointActivityBytes), contradiction.Reason, StringComparison.Ordinal);

        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.EndpointActivityBytes, ByteDomain.TransportObserved).Check());
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.EndpointActivityBytes, ByteDomain.TransportObserved, AccountingSide.EndpointActivity).Check());

        MetricRejection oneSided = Reject(
            AnalysisBasis.SourceObservations,
            Metric.EndpointActivityBytes,
            ByteDomain.TransportObserved,
            AccountingSide.SendSide);
        Assert.Contains("accounted as EndpointActivity by definition", oneSided.Reason, StringComparison.Ordinal);

        // A metric whose name states no direction may be accounted as endpoint activity: every requested length at
        // every endpoint is a meaningful total, and it is labelled as counting both ends.
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, null, AccountingSide.EndpointActivity).Check());
    }

    [Fact(DisplayName = "I11: an application payload metric projects onto its layer, and refuses any other")]
    public void AnApplicationPayloadMetricProjectsOntoItsLayer()
    {
        MetricRequest implied = Request(
            AnalysisBasis.SourceObservations,
            Metric.ApplicationPayloadBytes,
            byteDomain: null,
            AccountingSide.SendSide);
        Assert.Null(implied.Check());
        Assert.Equal(ObservationLayer.Application, implied.Materialized().Layer);

        MetricRejection transport = Reject(
            AnalysisBasis.SourceObservations,
            Metric.ApplicationPayloadBytes,
            ByteDomain.ApplicationPayload,
            AccountingSide.SendSide,
            layer: ObservationLayer.Transport);
        Assert.Contains("transport length be read as an application", transport.Reason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R2: a rate names an additive numerator, and nothing else carries one")]
    public void ARateNamesAnAdditiveNumerator()
    {
        MetricRejection none = Reject(AnalysisBasis.SourceObservations, Metric.Rate);
        Assert.Contains("names its numerator", none.Reason, StringComparison.Ordinal);
        Assert.Contains(Metric.Observations, none.CompatibleMetrics);
        Assert.DoesNotContain(Metric.ActivePeers, none.CompatibleMetrics);

        MetricRejection itself = Reject(AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.Rate);
        Assert.Contains("not its own numerator", itself.Reason, StringComparison.Ordinal);

        MetricRejection distinct = Reject(AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.ActivePeers);
        Assert.Contains("not additive over time", distinct.Reason, StringComparison.Ordinal);

        // A rate inherits its numerator's rules, so an incompatible numerator rejects the rate.
        MetricRejection inherited = Reject(AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.OperationsStarted);
        Assert.Contains("renamed as a call", inherited.Reason, StringComparison.Ordinal);

        MetricRejection stray = Reject(AnalysisBasis.SourceObservations, Metric.Observations, rateNumerator: Metric.Observations);
        Assert.Contains("Only a rate divides", stray.Reason, StringComparison.Ordinal);

        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.Observations).Check());
        Assert.Null(Request(AnalysisBasis.SourceObservations, Metric.Rate, ByteDomain.TransportObserved, AccountingSide.SendSide, Metric.BytesSent).Check());
    }

    [Fact(DisplayName = "I16: a result names the materialized request it answers, so two spellings are one request")]
    public void AResultNamesTheMaterializedRequest()
    {
        MetricRequest implicitDomain = Request(AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, null, AccountingSide.SendSide);
        MetricRequest explicitDomain = Request(AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, ByteDomain.RequestedIo, AccountingSide.SendSide);
        Assert.NotEqual(implicitDomain, explicitDomain);
        Assert.Equal(explicitDomain.Materialized(), implicitDomain.Materialized());

        MetricRequest endpoint = Request(AnalysisBasis.SourceObservations, Metric.EndpointActivityBytes, ByteDomain.TransportObserved);
        Assert.Equal(AccountingSide.EndpointActivity, endpoint.Materialized().AccountingSide);

        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, owner: 1_000)]);
        MetricResult answered = SessionMetrics.Evaluate(session.Store, endpoint);
        Assert.Equal(endpoint.Materialized(), answered.Request);
        Assert.Equal(1, answered.Generation);
        Assert.Equal([NormalizerContractVersion.V1], answered.Derivations);
        Assert.Equal(TimeScope.RetainedCapture, answered.Request.TimeScope);
    }

    [Fact(DisplayName = "I6: 21.1 scenario 1 - one transfer seen from both ends, accounted once per side")]
    public void OneTransferSeenFromBothEnds()
    {
        // A sends 100 known-domain bytes to B; B receives the same transfer.
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 100, owner: 1_000),
                Transfer(200, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, owner: 2_000),
            ]);

        MetricResult observations = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations);
        Assert.Equal(2, observations.Value);

        // Sender accounting counts the send record once. The receive record of the same transfer stays in the
        // breakdown as another side and is not added.
        MetricResult sent = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);
        Assert.Equal(100, sent.Value);
        Assert.Equal(1, sent.KnownContributions);
        Assert.Equal(1, sent.ExcludedOtherSide);
        Assert.Equal([AccountingSide.SendSide], sent.TakenSides);
        Assert.Equal([AccountingSide.SendSide, AccountingSide.ReceiveSide], sent.Sides.Select(side => side.Side));

        MetricResult received = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide);
        Assert.Equal(100, received.Value);

        // Endpoint activity counts both endpoints by design: A sent 100, B received 100, and the total is 200,
        // labelled as such. It is its own metric, not a correction of the two above.
        MetricResult endpoint = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.EndpointActivityBytes, ByteDomain.TransportObserved);
        Assert.Equal(200, endpoint.Value);
        Assert.Equal(2, endpoint.KnownContributions);
        Assert.Equal(0, endpoint.ExcludedOtherSide);
        Assert.Contains(endpoint.Caveats, caveat => caveat.Contains("both of its ends by design", StringComparison.Ordinal));

        // Sent bytes measured where they were received is the same transfer from its other end. Over the whole
        // session that is well defined; it says so, and says what grouping it by an entity would need.
        MetricResult receiverAccounted = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.ReceiveSide);
        Assert.Equal(100, receiverAccounted.Value);
        Assert.Contains(receiverAccounted.Caveats, caveat => caveat.Contains("other end of each transfer", StringComparison.Ordinal));

        // One owner per proven association is a meaningful request that nothing here can answer yet.
        MetricResult owner = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.CanonicalOwner);
        Assert.Equal(MetricUnavailableReason.NoTransferAssociations, owner.Unavailable);
        Assert.Null(owner.Value);

        // The logical basis would answer "one call" and there is no correlator, so it is unavailable with the
        // reason rather than answered from the two source records.
        MetricResult logical = Evaluate(session.Store, AnalysisBasis.LogicalOperations, Metric.OperationsCompleted);
        Assert.False(logical.IsAvailable);
        Assert.Equal(MetricUnavailableReason.NoLogicalOperations, logical.Unavailable);
        Assert.Contains("renamed as operations", logical.UnavailableExplanation!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P3: 21.1 scenario 2 - requested and completed bytes stay different quantities")]
    public void RequestedAndCompletedBytesStayDifferent()
    {
        // A requests a 4,096-byte pipe write and a validated completion reports 1,024 bytes.
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 4_096, owner: 1_000) with
                {
                    ByteDomain = ByteDomain.RequestedIo,
                    Mechanism = Mechanism.NamedPipe,
                    Layer = ObservationLayer.Resource,
                },
                Transfer(200, ObservationKind.RequestEnd, AccountingSide.SendSide, 1_024, owner: 1_000) with
                {
                    ByteDomain = ByteDomain.CompletedIo,
                    Mechanism = Mechanism.NamedPipe,
                    Layer = ObservationLayer.Resource,
                },
            ]);

        MetricResult requested = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.RequestedIoBytes, ByteDomain.RequestedIo, AccountingSide.SendSide);
        Assert.Equal(4_096, requested.Value);
        Assert.Equal(1, requested.ExcludedOtherDomain);

        MetricResult completed = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.CompletedIo, AccountingSide.SendSide);
        Assert.Equal(1_024, completed.Value);
        Assert.Equal(1, completed.ExcludedOtherDomain);

        // No unqualified transfer claim exists: nothing measured transport-observed bytes, so the answer is that
        // nothing was measured - not a zero, and not the requested length relabelled. It names what was measured.
        MetricResult transport = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);
        Assert.Equal(MetricUnavailableReason.NothingMeasured, transport.Unavailable);
        Assert.Null(transport.Value);
        Assert.Equal(2, transport.ExcludedOtherDomain);
        Assert.Equal(1, transport.OtherDomains[ByteDomain.RequestedIo]);
        Assert.Equal(1, transport.OtherDomains[ByteDomain.CompletedIo]);
        Assert.Contains("CompletedIo (1)", transport.UnavailableExplanation!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P1: a byte sum with nothing measured in scope is unavailable, never an observed zero")]
    public void NothingMeasuredIsNotAZero()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, null, owner: 1_000, 0) with { ByteAvailability = FieldAvailability.EventLost },
                Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 0, owner: 1_000, 1) with { Mechanism = Mechanism.Rpc },
            ]);

        // Every declared slot in scope is unknown: the result is unavailable and the unknown stays an unknown.
        MetricResult tcp = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide, mechanism: Mechanism.Tcp);
        Assert.Equal(MetricUnavailableReason.NothingMeasured, tcp.Unavailable);
        Assert.Null(tcp.Value);
        Assert.Equal(1, tcp.UnknownContributions);
        Assert.Contains("counted as unknown and never as zero", tcp.UnavailableExplanation!, StringComparison.Ordinal);

        // An observed zero is a measurement: the same request over a slot that reported 0 answers 0.
        MetricResult rpc = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide, mechanism: Mechanism.Rpc);
        Assert.True(rpc.IsAvailable);
        Assert.Equal(0, rpc.Value);
        Assert.Equal(1, rpc.KnownContributions);
    }

    [Fact(DisplayName = "I3: an interval scopes every metric half-open, and a rate divides by the whole of it")]
    public void AnIntervalScopesEveryMetric()
    {
        // One send every 1,000 ticks from 0 to 9,000: ten records, 10 bytes each, on a 10 MHz clock.
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [.. Enumerable.Range(0, 10).Select(index =>
                Transfer(index * 1_000L, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000, (ulong)index))],
            rowsPerSegment: 4);

        // [2,000, 5,000) holds the readings at 2,000, 3,000 and 4,000 - its start, and not its end.
        var interval = new TimeRange(2_000, 5_000);
        MetricResult count = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations, interval: interval);
        Assert.Equal(3, count.Value);
        Assert.Equal(7, count.ExcludedOutsideInterval);
        Assert.Equal(TimeScope.AnalysisInterval, count.Request.TimeScope);

        MetricResult bytes = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide, interval: interval);
        Assert.Equal(30, bytes.Value);
        Assert.Equal(7, bytes.ExcludedOutsideInterval);
        Assert.Equal(3, bytes.Segments);

        // A rate's numerator is what the interval holds, and its denominator is the whole interval: 3 records over
        // 3,000 ticks of a 10 MHz clock is 10,000 per second, kept as the integers it is made of.
        MetricResult rate = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.Observations, interval: interval);
        Assert.True(rate.IsAvailable);
        Assert.Null(rate.Value);
        Assert.Equal(3, rate.Rate!.Numerator);
        Assert.Equal(3_000, rate.Rate.IntervalTicks);
        Assert.Equal(10_000_000, rate.Rate.TicksPerSecond);
        Assert.Equal(10_000m, rate.Rate.PerSecond);
        Assert.Equal(MeasurementUnit.CountPerSecond, rate.Unit);
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("never divided by a shorter", StringComparison.Ordinal));
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("observed rate and not a corrected one", StringComparison.Ordinal));

        MetricResult byteRate = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate, ByteDomain.TransportObserved, AccountingSide.SendSide, Metric.BytesSent, interval);
        Assert.Equal((30, MeasurementUnit.Bytes), (byteRate.Rate!.Numerator, byteRate.Rate.NumeratorUnit));
        Assert.Equal(100_000m, byteRate.Rate.PerSecond);
        Assert.Equal(MeasurementUnit.BytesPerSecond, byteRate.Unit);

        // An interval with nothing in it is a count of zero records over a real interval, not an absent answer.
        MetricResult empty = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations, interval: new TimeRange(9_001, 20_000));
        Assert.Equal(0, empty.Value);
        Assert.Contains(empty.Caveats, caveat => caveat.Contains("not a finding that nothing happened", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R3: 21.1 scenario 8 - a window with a gap has an observed rate over the whole window")]
    public void AWindowWithAGapHasAnObservedRate()
    {
        // 100 operations observed in a 10-second window, none in its 2-second gap [4 s, 6 s).
        const long second = 10_000_000;
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [.. Enumerable.Range(0, 100).Select(index =>
            {
                long offset = index * (8 * second / 100);
                long ticks = offset < 4 * second ? offset : offset + (2 * second);
                return Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 1, 1_000, (ulong)index);
            })]);

        MetricResult rate = Evaluate(
            session.Store,
            AnalysisBasis.SourceObservations,
            Metric.Rate,
            rateNumerator: Metric.Observations,
            interval: new TimeRange(0, 10 * second));

        // 10 per second over the whole window. Dividing by the 8 seconds that look healthy would report 12.5/s and
        // call it the window's rate, which §19.2 forbids; no missing count is invented either.
        Assert.Equal(100, rate.Rate!.Numerator);
        Assert.Equal(10 * second, rate.Rate.IntervalTicks);
        Assert.Equal(10m, rate.Rate.PerSecond);
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("observed rate and not a corrected one", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "R21: a mechanism-scoped rate carries ledger coverage without changing observed arithmetic")]
    [InlineData(0L, CoverageState.Covered)]
    [InlineData(1L, CoverageState.PartialGap)]
    public void ARateReportsCaptureCoverageSeparately(long lost, CoverageState expected)
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2),
        ], coverage: MetricLedger(lost));

        MetricResult rate = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate,
            rateNumerator: Metric.Observations, interval: new TimeRange(10, 21), mechanism: Mechanism.Tcp);
        Assert.Equal(2, rate.Rate!.Numerator);
        Assert.Equal(11, rate.Rate.IntervalTicks);
        Assert.True(rate.Coverage!.LedgerPublished);
        Assert.Equal(expected, Assert.Single(rate.Coverage.Mechanisms).State);
        Assert.Contains(rate.Caveats, caveat => caveat.Contains($"is {expected}", StringComparison.Ordinal));
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("observed rate and not a corrected one", StringComparison.Ordinal));

        MetricResult quiet = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations,
            interval: new TimeRange(10, 21), mechanism: Mechanism.Rpc);
        Assert.Equal(0, quiet.Value);
        Assert.Equal(CoverageState.UnknownCoverage, Assert.Single(quiet.Coverage!.Mechanisms).State);
        MetricResult outside = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations,
            interval: new TimeRange(9, 21), mechanism: Mechanism.Tcp);
        Assert.Equal(CoverageState.UnknownCoverage, Assert.Single(outside.Coverage!.Mechanisms).State);

        MetricResult all = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate,
            rateNumerator: Metric.Observations, interval: new TimeRange(10, 21));
        Assert.Equal(Enum.GetValues<Mechanism>().Length, all.Coverage!.Mechanisms.Count);
        Assert.Contains(all.Coverage.Mechanisms, state => state.Mechanism == Mechanism.Tcp && state.State == expected);
        Assert.Contains(all.Caveats, caveat => caveat.Contains("No single covered state", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R21: a legacy metric names unknown capture coverage rather than claiming a healthy rate")]
    public void LegacyRateCoverageIsUnknown()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)]);
        MetricResult rate = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate,
            rateNumerator: Metric.Observations, interval: new TimeRange(10, 11), mechanism: Mechanism.Tcp);
        Assert.False(rate.Coverage!.LedgerPublished);
        Assert.Equal(CoverageState.UnknownCoverage, Assert.Single(rate.Coverage.Mechanisms).State);
        Assert.Contains(rate.Caveats, caveat => caveat.Contains("publishes no coverage ledger", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R3: a rate with no interval is unavailable rather than divided by the observed span")]
    public void ARateNeedsItsInterval()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000)]);

        MetricResult noInterval = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Rate, rateNumerator: Metric.Observations);
        Assert.False(noInterval.IsAvailable);
        Assert.Equal(MetricUnavailableReason.NoInterval, noInterval.Unavailable);
        Assert.Contains("there is no default", noInterval.UnavailableExplanation!, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: a record with no endpoint pair is no channel, never a zero, and a peer count needs a process")]
    public void AChannelCountNeverCountsWhatItCannotIdentify()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000)]);

        // A channel is a connection incarnation found through endpoints; a record without them identifies none, and a
        // count of nothing identified is not an observed zero.
        MetricResult channels = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.ActiveChannels);
        Assert.False(channels.IsAvailable);
        Assert.Equal(MetricUnavailableReason.NothingMeasured, channels.Unavailable);
        Assert.Equal(1, channels.UnknownCounterparts[ProcessBindingReason.PeerEndpointIncomplete]);
        Assert.Contains("Zero would be a guess", channels.UnavailableExplanation!, StringComparison.Ordinal);

        // Peers are process instances, which are identities; but the peers of nothing mean nothing.
        ArgumentException peers = Assert.Throws<ArgumentException>(() =>
            Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.ActivePeers));
        Assert.Contains("Name a process focus", peers.Message, StringComparison.Ordinal);

        MetricResult errors = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Errors);
        Assert.Equal(MetricUnavailableReason.NoStatusDomain, errors.Unavailable);
        Assert.Contains("assigns no enumeration", errors.UnavailableExplanation!, StringComparison.Ordinal);

        MetricResult topology = Evaluate(session.Store, AnalysisBasis.ResourceTopology, Metric.Observations);
        Assert.Equal(MetricUnavailableReason.NoResourceTopology, topology.Unavailable);
    }

    [Fact(DisplayName = "I11: a metric answers over every segment of the generation, and projects by layer")]
    public void AMetricAnswersOverEverySegment()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000, 0),
                Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 20, 1_000, 1),
                Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 30, 1_000, 2) with
                {
                    Layer = ObservationLayer.Application,
                    Mechanism = Mechanism.Rpc,
                },
            ],
            rowsPerSegment: 2);

        MetricResult all = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);
        Assert.Equal(60, all.Value);
        Assert.Equal(2, all.Segments);
        Assert.Equal(3, all.RowsRead);

        MetricResult transportOnly = Evaluate(
            session.Store,
            AnalysisBasis.SourceObservations,
            Metric.BytesSent,
            ByteDomain.TransportObserved,
            AccountingSide.SendSide,
            layer: ObservationLayer.Transport);
        Assert.Equal(30, transportOnly.Value);
        Assert.Equal(1, transportOnly.ExcludedByProjection);
    }

    [Fact(DisplayName = "R3: an unknown measurement is counted as unknown, never as a zero contribution")]
    public void AnUnknownMeasurementStaysUnknown()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 40, 1_000, 0),
                Transfer(200, ObservationKind.Send, AccountingSide.SendSide, null, 1_000, 1) with
                {
                    ByteAvailability = FieldAvailability.EventLost,
                    MeasurementQuality = QualityLevel.UnknownQuality,
                },
            ]);

        MetricResult sent = Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide);

        Assert.Equal(40, sent.Value);
        Assert.Equal(1, sent.KnownContributions);
        Assert.Equal(1, sent.UnknownContributions);
        Assert.Equal(0.5, sent.MeasurementAvailability);
        Assert.Equal(1, sent.UnknownReasons[FieldAvailability.EventLost]);
        Assert.Contains(sent.Caveats, caveat => caveat.Contains("never as zero", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "I5: a total resolves to exactly the records it counted, with identities that survive a re-read")]
    public void ATotalResolvesToTheRecordsItCounted()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 100, owner: 1_000, 7),
                Transfer(200, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, owner: 2_000, 8),
                Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 5, owner: 1_000, 9),
            ]);

        MetricResult sent = SessionMetrics.Evaluate(
            session.Store,
            Request(AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved, AccountingSide.SendSide),
            new() { EvidenceLimit = 1 });

        // The limit bounds the listing, not the total, and the listing holds only records the total took.
        Assert.Equal(105, sent.Value);
        MetricEvidence first = Assert.Single(sent.Evidence);
        Assert.Equal(ObservationKind.Send, first.Observation.Kind);
        Assert.Equal(new RawRecordId(Capture, 1, 1, 7), first.ObservationId.RawRecordId);
        Assert.Equal(NormalizerContractVersion.V1, first.ObservationId.NormalizerContractVersion);

        // With room for every record, the listing is exactly the set the total took: as many records as it
        // counted, each one a contribution it summed (I5).
        MetricResult endpoint = SessionMetrics.Evaluate(
            session.Store,
            Request(AnalysisBasis.SourceObservations, Metric.EndpointActivityBytes, ByteDomain.TransportObserved),
            new() { EvidenceLimit = 10 });
        Assert.Equal([7UL, 8UL, 9UL], endpoint.Evidence.Select(item => item.ObservationId.RawRecordId.RecordOrdinal));
        Assert.Equal(endpoint.KnownContributions + endpoint.UnknownContributions, endpoint.Evidence.Count);
        Assert.Equal(endpoint.Value, endpoint.Evidence.Sum(item => item.Observation.ByteValue));

        MetricResult received = SessionMetrics.Evaluate(
            session.Store,
            Request(AnalysisBasis.SourceObservations, Metric.BytesReceived, ByteDomain.TransportObserved, AccountingSide.ReceiveSide),
            new() { EvidenceLimit = 10 });
        Assert.Equal([8UL], received.Evidence.Select(item => item.ObservationId.RawRecordId.RecordOrdinal));

        MetricResult count = SessionMetrics.Evaluate(
            session.Store,
            Request(AnalysisBasis.SourceObservations, Metric.Observations),
            new() { EvidenceLimit = 10 });
        Assert.Equal(count.Value, count.Evidence.Count);

        MetricResult bounded = SessionMetrics.Evaluate(
            session.Store,
            Request(AnalysisBasis.SourceObservations, Metric.Observations),
            new() { EvidenceLimit = 2 });
        Assert.Equal(3, bounded.Value);
        Assert.Equal(2, bounded.Evidence.Count);
    }

    [Fact(DisplayName = "I2: a generation naming two derivations of one capture is refused rather than counted twice")]
    public void TwoDerivationsOfOneCaptureAreRefused()
    {
        using var session = new TemporarySession();
        ObservationRowV1 row = Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000);
        Publish(session.Store, [row]);

        // A second derivation of the same evidence, published beside the first rather than instead of it.
        Publish(session.Store, [row], derivation: new NormalizerContractVersion(2));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            Evaluate(session.Store, AnalysisBasis.SourceObservations, Metric.Observations));
        Assert.Contains("would count it twice", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R2: an invalid request is refused before it is planned, not answered")]
    public void AnInvalidRequestIsRefusedBeforePlanning()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, 1_000)]);

        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            SessionMetrics.Evaluate(
                session.Store,
                Request(AnalysisBasis.SourceObservations, Metric.BytesSent, ByteDomain.TransportObserved)));

        Assert.Contains("unexplained volume number", refusal.Message, StringComparison.Ordinal);
    }

    private static MetricRequest Request(
        AnalysisBasis basis,
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null,
        Metric? rateNumerator = null) => new()
        {
            Basis = basis,
            Metric = metric,
            ByteDomain = byteDomain,
            AccountingSide = accountingSide,
            RateNumerator = rateNumerator,
        };

    private static MetricRejection Reject(
        AnalysisBasis basis,
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null,
        Metric? rateNumerator = null,
        ObservationLayer? layer = null)
    {
        MetricRejection? rejection = (Request(basis, metric, byteDomain, accountingSide, rateNumerator) with { Layer = layer }).Check();
        Assert.NotNull(rejection);
        return rejection;
    }

    private static CoverageLedgerV1 MetricLedger(long lost) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = CoverageAcquisition.EtlImport,
            FirstDeliveredNativeTicks = 10,
            LastDeliveredNativeTicks = 20,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 99, Version = 0, Mechanism = Mechanism.Rpc },
            ],
            Deliveries = [new CoverageDeliveryV1
            {
                ProviderId = NetworkProvider,
                EventId = 10,
                Version = 0,
                Delivered = 2,
                Admitted = 2,
                Omitted = 0,
            }],
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost }],
        }],
    };

    private static MetricResult Evaluate(
        SessionStore store,
        AnalysisBasis basis,
        Metric metric,
        ByteDomain? byteDomain = null,
        AccountingSide? accountingSide = null,
        Metric? rateNumerator = null,
        TimeRange? interval = null,
        ObservationLayer? layer = null,
        Mechanism? mechanism = null) =>
        SessionMetrics.Evaluate(
            store,
            Request(basis, metric, byteDomain, accountingSide, rateNumerator) with
            {
                Interval = interval,
                Layer = layer,
                Mechanism = mechanism,
            });
}
