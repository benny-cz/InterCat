using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The resource-topology basis (`contracts/metrics-v1.md` §8b, ADR-098): each resource `resource-instances-v3` derives is
/// counted once over its known lifetime, with the processes sharing it and the capacity its views mapped - never traffic,
/// and never a count of its records.
/// </summary>
public sealed class TopologyMetricsTests
{
    private const long EightMebibytes = 8L << 20;

    [Fact(DisplayName = "R3: two processes mapping one 8 MiB section are one resource, two memberships and 8 MiB of capacity, and no traffic")]
    public void TwoProcessesMappingOneSection()
    {
        // writer.exe and reader.exe each map a view of one section and unmap it (§21.1).
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\writer.exe" },
            Lifecycle(1, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\reader.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        foreach ((long ticks, byte opcode, int process, long view) in new (long, byte, int, long)[]
        {
            (10, 37, 100, 1), (11, 37, 200, 2), (20, 38, 200, 2), (21, 38, 100, 1),
        })
        {
            ObservationRowV1 row = SectionRecord(ticks, opcode, process, process, (ulong)(10 + rows.Count), view: true);
            rows.Add(row);
            fields.AddRange([Field(row, SourceField.SectionNumber, 1), Field(row, SourceField.SectionViewNumber, view),
                Field(row, SourceField.ViewSize, EightMebibytes), Field(row, SourceField.ViewInformation, 0)]);
        }

        using var session = new TemporarySession();
        Publish(session.Store, rows, fields: fields);

        MetricResult channels = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels));
        Assert.Equal((1L, MeasurementUnit.Count), (channels.Value, channels.Unit));
        Assert.Equal((1L, 2L), (channels.Resources!.ByViews, channels.Resources.Memberships));
        Assert.Equal(ResourceInstanceIndex.Rule, channels.ResourceRule);

        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity));
        Assert.Equal((EightMebibytes, MeasurementUnit.Bytes), (capacity.Value, capacity.Unit));

        MetricResult peers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Owner = Instance(session.Store, 100) });
        Assert.Equal(1L, peers.Value);

        // Traffic is no topology metric, and counting a resource's records is not defined at this version.
        Assert.NotNull((Request(Metric.BytesSent) with
        {
            ByteDomain = ByteDomain.TransportObserved,
            AccountingSide = AccountingSide.SendSide,
        }).Check());
        Assert.Equal(MetricUnavailableReason.NoResourceTopology, SessionMetrics.Evaluate(session.Store, Request(Metric.Observations)).Unavailable);
    }

    [Fact(DisplayName = "R3: a topology answer takes each resource whole, by its mechanism and layer, its known lifetime and its holders, and says what its records establish")]
    public void ResourcesAreTakenWhole()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        ProcessInstanceId writer = Instance(session.Store, 100);
        ProcessInstanceId reader = Instance(session.Store, 200);

        // Section 4 by its handles and sections 4 to 7 by their views: five resources, the two 4s never joined.
        MetricResult all = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels));
        Assert.Equal(5L, all.Value);
        MetricResources resources = all.Resources!;
        Assert.Equal(new Dictionary<Mechanism, long> { [Mechanism.SharedSection] = 5 }, resources.Taken);
        Assert.Equal((4L, 1L, 2L, 1L, 1L, 6L, 5L), (resources.ByViews, resources.Images, resources.MadeBefore, resources.HeldAtStop,
            resources.EndUnseen, resources.Memberships, resources.Resources));

        // A resource meets an interval by its known lifetime: section 6, mapped before the capture, reaches [32, 40), and
        // section 7, which only the start's rundown lists, does not.
        MetricResult during = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Interval = new TimeRange(32, 40) });
        Assert.Equal((3L, 2L), (during.Value, during.ExcludedOutsideInterval));
        Assert.Contains(during.Caveats, caveat => caveat.StartsWith("A resource is in scope where its known lifetime", StringComparison.Ordinal));

        // Sections 6 and 7, mapped before the capture, reach back before their first records; section 5, held at the stop,
        // reaches past its last.
        Assert.Equal(2L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Interval = new TimeRange(0, 2) }).Value);
        Assert.Equal(1L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Interval = new TimeRange(91, 100) }).Value);

        // A projection keeps a resource by its mechanism and by the layer its records are of.
        MetricResult synchronization = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Mechanism = Mechanism.Synchronization });
        Assert.Equal((0L, 5L), (synchronization.Value, synchronization.ExcludedByProjection));
        MetricResult transport = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Layer = ObservationLayer.Transport });
        Assert.Equal((0L, 5L), (transport.Value, transport.ExcludedByProjection));
        Assert.Equal(5L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Layer = ObservationLayer.Resource }).Value);

        // A process filter keeps the resources its process holds: the writer's are section 4 both ways and section 5; the one
        // it shares with the reader is section 4's views, which is also what lies between them.
        MetricResult writers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Owner = writer });
        Assert.Equal((3L, 2L), (writers.Value, writers.ExcludedByProcessFilter));
        Assert.Equal(ProcessInstanceIndex.BindingRule, writers.BindingRule);
        Assert.Equal(1L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Owner = writer, Peer = reader }).Value);
        Assert.Equal(1L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with
        {
            Between = new ProcessBetween([writer], [reader]),
        }).Value);

        // Each shares a resource with the other alone.
        Assert.Equal(1L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Owner = writer }).Value);
        Assert.Equal(1L, SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Participant = reader }).Value);

        // Capacity is each section's largest view, once, an image's said apart; section 4 by its handles records no size.
        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity));
        Assert.Equal((65536L + 4096 + 8192 + 16384, 4L, 1L), (capacity.Value, capacity.KnownContributions, capacity.UnknownContributions));
        Assert.Equal(1L, capacity.UnknownReasons[FieldAvailability.NotExposed]);
        Assert.Contains(capacity.Caveats, caveat => caveat.StartsWith("1 section of it is an image's, 4,096 bytes", StringComparison.Ordinal));

        // A membership has no direction, and counting records, a mapping lifetime and a grouping are not defined yet.
        foreach (MetricRequest unanswered in new[]
        {
            Request(Metric.ActiveChannels) with { Sender = writer },
            Request(Metric.ActiveChannels) with { Between = new ProcessBetween([writer], [reader], BetweenDirection.FirstToSecond) },
            Request(Metric.ActiveChannels) with { Grouping = LaneGrouping.InstanceOnly },
            Request(Metric.Observations),
            Request(Metric.Duration) with { DurationInterval = DurationInterval.MappingLifetime },
        })
        {
            Assert.Equal(MetricUnavailableReason.NoResourceTopology, SessionMetrics.Evaluate(session.Store, unanswered).Unavailable);
        }

        // An answer names its resource rule as its correlation revision, and the binding rule only where holders decide it.
        Assert.Contains("\"correlationRevision\":\"resource-instances-v3\"", all.Identity!.CanonicalSpecification, StringComparison.Ordinal);
        Assert.DoesNotContain("entityRevision", all.Identity.CanonicalSpecification, StringComparison.Ordinal);
        Assert.Contains("\"entityRevision\"", writers.Identity!.CanonicalSpecification, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: a topology count beside records bound to no process is a lower bound, and a capacity no section's views measured is unmeasured, never zero")]
    public void WhatNoRecordBindsIsDisclosed()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = NamedObjects();
        Publish(session.Store, rows, fields: fields);

        // 100 shares the event with 200, and object 4's duplication into 400, before 400's creation, binds to no process.
        MetricResult peers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Owner = Instance(session.Store, 100) });
        Assert.Equal((1L, 1L), (peers.Value, peers.UnknownCounterparts[ProcessBindingReason.BeforeFirstEvidence]));
        Assert.Contains(peers.Caveats, caveat => caveat.StartsWith("At least 1:", StringComparison.Ordinal));

        // In [70, 80) 100 held object 4 alone, whose other holder no record binds: unmeasured, never no peer.
        MetricResult unbound = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with
        {
            Owner = Instance(session.Store, 100),
            Interval = new TimeRange(70, 80),
        });
        Assert.Equal((MetricUnavailableReason.NothingMeasured, (long?)null), (unbound.Unavailable, unbound.Value));

        // 400 holds nothing its records bind it to: the object it may hold is disclosed, never counted.
        MetricResult held = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Owner = Instance(session.Store, 400) });
        Assert.Equal((0L, 1L), (held.Value, held.Resources!.Disclosed));
        Assert.Equal(1L, held.UnresolvedCounterparts[ProcessBindingReason.BeforeFirstEvidence]);

        // No section here was found by its views, so no capacity is known: unmeasured, never zero.
        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity));
        Assert.Equal((MetricUnavailableReason.NothingMeasured, (long?)null, 1L), (capacity.Unavailable, capacity.Value, capacity.UnknownContributions));
    }

    private static MetricRequest Request(Metric metric) => new() { Basis = AnalysisBasis.ResourceTopology, Metric = metric };

    // A process's instance as the answer derives it: from the side segments' start keys as well as its records.
    private static ProcessInstanceId Instance(SessionStore store, int processId)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] fieldSegments = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        return ProcessInstanceIndex.Derive(Segments(store), TestClock, fieldSegments)
            .Instances.Single(instance => instance.ProcessId == processId).Id;
    }
}
