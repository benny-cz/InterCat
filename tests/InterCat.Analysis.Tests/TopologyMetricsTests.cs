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

        // A membership has no direction, and counting records, a grouping by anything but a resource's mechanism, a focus's
        // peers or a fact of the processes holding it, and a mapping lifetime grouped by anything but a fact of its holder are
        // not defined yet.
        foreach (MetricRequest unanswered in new[]
        {
            Request(Metric.ActiveChannels) with { Sender = writer },
            Request(Metric.ActiveChannels) with { Between = new ProcessBetween([writer], [reader], BetweenDirection.FirstToSecond) },
            Request(Metric.MappingCapacity) with { Grouping = LaneGrouping.Endpoint },
            Request(Metric.Observations),
            Request(Metric.Duration) with { DurationInterval = DurationInterval.MappingLifetime, Grouping = LaneGrouping.Mechanism },
            Request(Metric.Duration) with { DurationInterval = DurationInterval.MappingLifetime, Owner = writer, Grouping = LaneGrouping.Peer },
        })
        {
            Assert.Equal(MetricUnavailableReason.NoResourceTopology, SessionMetrics.Evaluate(session.Store, unanswered).Unavailable);
        }

        // An answer names its resource rule as its correlation revision, and the binding rule only where holders decide it.
        Assert.Contains("\"correlationRevision\":\"resource-instances-v3\"", all.Identity!.CanonicalSpecification, StringComparison.Ordinal);
        Assert.DoesNotContain("entityRevision", all.Identity.CanonicalSpecification, StringComparison.Ordinal);
        Assert.Contains("\"entityRevision\"", writers.Identity!.CanonicalSpecification, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a grouped topology answer counts each group's resources, peers and capacity, a shared resource in each holder's group, and never adds the groups up")]
    public void GroupsOverlapByWhatTheirProcessesHeld()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        ProcessInstanceId writer = Instance(session.Store, 100);
        ProcessInstanceId reader = Instance(session.Store, 200);
        Dictionary<ProcessInstanceId, long?> ByProcess(MetricResult result) =>
            result.Groups.ToDictionary(group => group.Process!.Id, group => group.Value);

        // Each held three resources, section 4's views among them both: five in all, the groups overlapping.
        MetricResult channels = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal((5L, false), (channels.Value, channels.GroupsPartitionTotal));
        Assert.Equal(new Dictionary<ProcessInstanceId, long?> { [writer] = 3, [reader] = 3 }, ByProcess(channels));
        Assert.Equal(ProcessInstanceIndex.BindingRule, channels.BindingRule);

        // Each shares a resource with the other alone: two processes share one in all.
        MetricResult peers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal(2L, peers.Value);
        Assert.Equal(new Dictionary<ProcessInstanceId, long?> { [writer] = 1, [reader] = 1 }, ByProcess(peers));

        // Each one's sections' capacity, section 4's in both, once in the total; the writer's section by its handles recorded
        // no size.
        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal((94208L, MeasurementUnit.Bytes), (capacity.Value, capacity.Unit));
        Assert.Equal(new Dictionary<ProcessInstanceId, long?> { [writer] = 65536 + 4096, [reader] = 65536 + 8192 + 16384 }, ByProcess(capacity));
        Assert.Equal(1L, capacity.Groups.Single(group => group.Process!.Id == writer).UnknownContributions);

        // The rest past one row is one exact answer over the union of what it holds, never a sum of overlapping ones.
        MetricResult top = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with
        {
            Grouping = LaneGrouping.Executable,
            RequestedRows = 1,
        });
        Assert.Equal((1, 3L, 1), (top.Groups.Count, top.Remainder!.Value, top.Remainder.GroupsMerged));
    }

    [Fact(DisplayName = "R3: a topology answer grouped by mechanism puts each resource in the one group of what it is, the groups adding up to the total, and a capacity in its sections' alone")]
    public void GroupsByMechanismPartitionTheTotal()
    {
        // The socket fixture's object and four sockets: two groups, the sockets first, adding up to five.
        using var sockets = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(sockets.Store, rows, fields: fields);
        MetricResult channels = SessionMetrics.Evaluate(sockets.Store, Request(Metric.ActiveChannels) with { Grouping = LaneGrouping.Mechanism });
        Assert.Equal((5L, true), (channels.Value, channels.GroupsPartitionTotal));
        Assert.Equal([(MetricGroupKind.Mechanism, Mechanism.UnixDomainSocket, 4L), (MetricGroupKind.Mechanism, Mechanism.Synchronization, 1L)],
            channels.Groups.Select(group => (group.Kind, group.Mechanism!.Value, group.Value!.Value)));
        Assert.Contains(channels.Caveats, caveat => caveat.Contains("so the groups partition the total", StringComparison.Ordinal));

        // The rest past one row is its resources' count.
        MetricResult top = SessionMetrics.Evaluate(sockets.Store, Request(Metric.ActiveChannels) with
        {
            Grouping = LaneGrouping.Mechanism,
            RequestedRows = 1,
        });
        Assert.Equal((1, 1L, 1), (top.Groups.Count, top.Remainder!.Value, top.Remainder.GroupsMerged));

        // No section, no capacity group: an object or a socket maps nothing. Grouped by mechanism, a count of channels is the
        // resource-topology basis's alone; a source-observations count is refused as before.
        Assert.Empty(SessionMetrics.Evaluate(sockets.Store, Request(Metric.MappingCapacity) with { Grouping = LaneGrouping.Mechanism }).Groups);
        Assert.NotNull((Request(Metric.ActiveChannels) with { Basis = AnalysisBasis.SourceObservations, Grouping = LaneGrouping.Mechanism }).Check());

        // The mapped sections' capacity is sections' alone, one group, its handles' section an unknown contribution.
        using var mapped = new TemporarySession();
        (rows, fields) = MappedSections();
        Publish(mapped.Store, rows, fields: fields);
        MetricResult capacity = SessionMetrics.Evaluate(mapped.Store, Request(Metric.MappingCapacity) with { Grouping = LaneGrouping.Mechanism });
        MetricGroup sections = Assert.Single(capacity.Groups);
        Assert.Equal((94208L, Mechanism.SharedSection, 94208L, 1L, true),
            (capacity.Value, sections.Mechanism!.Value, sections.Value, sections.UnknownContributions, capacity.GroupsPartitionTotal));
        Assert.DoesNotContain("entityRevision", capacity.Identity!.CanonicalSpecification, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R3: a topology answer grouped by peer counts what the focus shared with each other process - the resources both held, or the sections both mapped - the groups overlapping, and one no other process held in none")]
    public void GroupsByPeerAreWhatTheFocusShared()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        ProcessInstanceId writer = Instance(session.Store, 100);
        ProcessInstanceId reader = Instance(session.Store, 200);

        // The writer held three resources - section 4 by its handles and by its views, and the image's - and shared section
        // 4's views with the reader alone.
        MetricResult channels = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Owner = writer, Grouping = LaneGrouping.Peer });
        Assert.Equal((3L, false), (channels.Value, channels.GroupsPartitionTotal));
        MetricGroup peer = Assert.Single(channels.Groups);
        Assert.Equal((MetricGroupKind.ProcessInstance, reader, 1L, 1), (peer.Kind, peer.Process!.Id, peer.Value!.Value, peer.Rank!.Value));
        Assert.Contains(channels.Caveats, caveat => caveat.StartsWith("2 resources the focus held were held by no other process", StringComparison.Ordinal));
        Assert.Contains(channels.Caveats, caveat => caveat.Contains("so the groups overlap and do not add up to the total", StringComparison.Ordinal));

        // Their shared capacity is section 4's views', 64 KiB of the writer's 68 KiB.
        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity) with { Owner = writer, Grouping = LaneGrouping.Peer });
        Assert.Equal(((long?)69_632, (long?)65_536), (capacity.Value, Assert.Single(capacity.Groups).Value));

        // 100 shared the event with 200; object 4's other holder, whom no record binds, is no group's, counted by why; and a
        // capacity by peer is sections' alone, which 100 shared with no one.
        using var objects = new TemporarySession();
        (rows, fields) = NamedObjects();
        Publish(objects.Store, rows, fields: fields);
        ProcessInstanceId maker = Instance(objects.Store, 100);
        MetricResult shared = SessionMetrics.Evaluate(objects.Store, Request(Metric.ActiveChannels) with { Owner = maker, Grouping = LaneGrouping.Peer });
        Assert.Equal([(200, 1L)], shared.Groups.Select(group => (group.Process!.ProcessId, group.Value!.Value)));
        MetricGroup unbound = Assert.Single(shared.Unattributed);
        Assert.Equal((ProcessBindingReason.BeforeFirstEvidence, 1L), (unbound.Reason!.Value, unbound.UnknownContributions));
        Assert.Empty(SessionMetrics.Evaluate(objects.Store, Request(Metric.MappingCapacity) with { Owner = maker, Grouping = LaneGrouping.Peer }).Groups);
    }

    [Fact(DisplayName = "R3: a mapping lifetime runs from a view's mapping to its unmapping, measured only where both were seen, a view mapped before the capture or still mapped at its stop stated and never given a duration")]
    public void AMappingLifetimeIsMeasuredWhereBothEndsWereSeen()
    {
        // Section 4's two views, mapped at 10 and 11 and unmapped at 31 and 30: 2,100 and 1,900 ns. Section 6's view, mapped
        // before the capture and unmapped at 50, is censored; 7's, never unmapped, and the image's, held at the stop, completed
        // in no range.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        MetricRequest lifetime = Request(Metric.Duration) with { DurationInterval = DurationInterval.MappingLifetime };
        MetricResult completed = SessionMetrics.Evaluate(session.Store, lifetime);
        Assert.Equal(((long?)1_900, MeasurementUnit.Nanoseconds, 2L, 1L), (completed.Value, completed.Unit, completed.KnownContributions, completed.UnknownContributions));
        Assert.Equal((1_900L, 2_100L), (completed.Distribution!.Minimum, completed.Distribution.Maximum));
        Assert.Contains(completed.Caveats, caveat => caveat.StartsWith("1 mapping in scope had an end the capture did not see", StringComparison.Ordinal));
        Assert.Equal((long?)2_100, SessionMetrics.Evaluate(session.Store, lifetime with { Statistic = DurationStatistic.Maximum }).Value);

        // Started in range: the image's view, mapped at 12 and held at the stop, is censored with them.
        MetricResult started = SessionMetrics.Evaluate(session.Store, lifetime with { Cohort = OperationCohort.StartedInRange });
        Assert.Equal((2L, 1L), (started.KnownContributions, started.UnknownContributions));

        // The writer's alone, and the unmappings before 31 alone.
        ProcessInstanceId writer = Instance(session.Store, 100);
        MetricResult writers = SessionMetrics.Evaluate(session.Store, lifetime with { Owner = writer });
        Assert.Equal(((long?)2_100, 2L), (writers.Value, writers.ExcludedByProcessFilter));
        MetricResult early = SessionMetrics.Evaluate(session.Store, lifetime with { Interval = new TimeRange(0, 31) });
        Assert.Equal(((long?)1_900, 2L), (early.Value, early.ExcludedOutsideInterval));

        // Nothing in scope measured is unmeasured, never a lifetime of zero.
        MetricResult none = SessionMetrics.Evaluate(session.Store, lifetime with { Interval = new TimeRange(40, 60) });
        Assert.Equal((MetricUnavailableReason.NothingMeasured, (long?)null, 1L), (none.Unavailable, none.Value, none.UnknownContributions));

        // A projection to another mechanism leaves every mapping out by it.
        MetricResult objects = SessionMetrics.Evaluate(session.Store, lifetime with { Mechanism = Mechanism.Synchronization });
        Assert.Equal((MetricUnavailableReason.NothingMeasured, 3L), (objects.Unavailable, objects.ExcludedByProjection));
    }

    [Fact(DisplayName = "R3: a mapping lifetime grouped by a fact of the process holding each mapping ranks each group's statistic, the longest first, each mapping in exactly one group, one no process holds in its reason's, and the groups' values never added up")]
    public void AMappingLifetimeGroupsByItsHolder()
    {
        // Beside the mapped sections, 300, created at 15, maps section 8's view 91 from 20 to 25, 500 ns; and its PID maps view
        // 92 from 5 to 8, before 300's creation, bound to no process: 300 ns.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        (ObservationRowV1 Row, long View)[] added =
        [
            (SectionRecord(5, 37, 300, 300, 100, view: true), 92),
            (SectionRecord(8, 38, 300, 300, 101, view: true), 92),
            (SectionRecord(20, 37, 300, 300, 102, view: true), 91),
            (SectionRecord(25, 38, 300, 300, 103, view: true), 91),
        ];
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                .. rows.Append(Lifecycle(15, ObservationKind.Create, 300, 104) with { ResourceName = @"C:\Tools\late.exe" })
                    .Concat(added.Select(entry => entry.Row))
                    .OrderBy(row => row.NativeTicks)
                    .ThenBy(row => row.RawRecordOrdinal),
            ],
            fields:
            [
                .. fields,
                .. added.SelectMany(entry => new[]
                {
                    Field(entry.Row, SourceField.SectionNumber, 8),
                    Field(entry.Row, SourceField.SectionViewNumber, entry.View),
                    Field(entry.Row, SourceField.ViewSize, 4096),
                    Field(entry.Row, SourceField.ViewInformation, 0),
                }),
            ]);
        ProcessInstanceId writer = Instance(session.Store, 100);
        ProcessInstanceId reader = Instance(session.Store, 200);
        ProcessInstanceId late = Instance(session.Store, 300);
        MetricRequest lifetime = Request(Metric.Duration) with { DurationInterval = DurationInterval.MappingLifetime };

        // Each process's mappings: the writer's 2,100 ns, the reader's 1,900 ns beside section 6's view, mapped before the
        // capture and censored, and 300's 500 ns; the view no process holds is its reason's, measured. The total is the median
        // of all four, never a statistic of the groups'.
        MetricResult grouped = SessionMetrics.Evaluate(session.Store, lifetime with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal(((long?)500, 4L, 1L, false, ProcessInstanceIndex.BindingRule),
            (grouped.Value, grouped.KnownContributions, grouped.UnknownContributions, grouped.GroupsPartitionTotal, grouped.BindingRule));
        Assert.Equal(
            [(writer, 2_100L, 1, 1L, 0L), (reader, 1_900L, 2, 1L, 1L), (late, 500L, 3, 1L, 0L)],
            grouped.Groups.Select(group => (group.Process!.Id, group.Value!.Value, group.Rank!.Value, group.KnownContributions, group.UnknownContributions)));
        Assert.Equal((1L, 1_900L), (grouped.Groups[1].Distribution!.Count, grouped.Groups[1].Distribution!.Maximum));
        MetricGroup unbound = Assert.Single(grouped.Unattributed);
        Assert.Equal((ProcessBindingReason.BeforeFirstEvidence, (long?)300, 1L, 0L),
            (unbound.Reason!.Value, unbound.Value, unbound.KnownContributions, unbound.UnknownContributions));
        Assert.Contains(grouped.Caveats, caveat => caveat.StartsWith("Groups are ranked by their median, the longest first.", StringComparison.Ordinal));
        Assert.Contains(grouped.Caveats, caveat => caveat.StartsWith("1 mapping is no group's, by reason (BeforeFirstEvidence 1)", StringComparison.Ordinal));

        // Started in range, by executable: the image's view, mapped at 12 and held at the stop, is censored in the writer's.
        MetricResult started = SessionMetrics.Evaluate(session.Store, lifetime with
        {
            Cohort = OperationCohort.StartedInRange,
            Grouping = LaneGrouping.Executable,
        });
        Assert.Equal(
            [(@"C:\Tools\writer.exe", 2_100L, 1L), (@"C:\Tools\reader.exe", 1_900L, 0L), (@"C:\Tools\late.exe", 500L, 0L)],
            started.Groups.Select(group => (group.Executable!, group.Value!.Value, group.UnknownContributions)));

        // The rest past one row is one answer over the union of its mappings, the reader's 1,900 and 300's 500 ns: their
        // median is 500, never a sum or the largest of the groups' statistics.
        MetricResult top = SessionMetrics.Evaluate(session.Store, lifetime with { Grouping = LaneGrouping.InstanceOnly, RequestedRows = 1 });
        MetricGroup rest = top.Remainder!;
        Assert.Equal((1, (long?)500, 2L, 1L, 2), (top.Groups.Count, rest.Value, rest.KnownContributions, rest.UnknownContributions, rest.GroupsMerged));
        Assert.Equal((2L, 500L, 1_900L), (rest.Distribution!.Count, rest.Distribution.Minimum, rest.Distribution.Maximum));

        // A group whose every mapping had an end unseen is unmeasured and never ranked, as is the answer.
        MetricResult censored = SessionMetrics.Evaluate(session.Store, lifetime with { Grouping = LaneGrouping.InstanceOnly, Interval = new TimeRange(40, 60) });
        MetricGroup only = Assert.Single(censored.Groups);
        Assert.Equal((MetricUnavailableReason.NothingMeasured, reader, (long?)null, (int?)null, 1L),
            (censored.Unavailable, only.Process!.Id, only.Value, only.Rank, only.UnknownContributions));
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

        // Grouped, the membership no record binds is no group's, counted by why; a process sharing nothing, with no record
        // bound to none, has no peer, never an unmeasured count.
        MetricResult grouped = SessionMetrics.Evaluate(session.Store, Request(Metric.ActiveChannels) with { Grouping = LaneGrouping.InstanceOnly });
        MetricGroup unattributed = Assert.Single(grouped.Unattributed);
        Assert.Equal((ProcessBindingReason.BeforeFirstEvidence, 1L), (unattributed.Reason!.Value, unattributed.UnknownContributions));
        Assert.Equal([(100, 3L), (300, 2L), (200, 1L)], grouped.Groups.Select(group => (group.Process!.ProcessId, group.Value!.Value)));
        MetricResult groupedPeers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal(0L, groupedPeers.Groups.Single(group => group.Process!.ProcessId == 300).Value);
        Assert.Equal(1L, groupedPeers.Groups.Single(group => group.Process!.ProcessId == 100).UnknownContributions);

        // Grouped, a capacity is still a section's alone: 100's section by its handles, whose size no record states.
        MetricResult groupedCapacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity) with { Grouping = LaneGrouping.InstanceOnly });
        Assert.Equal(MetricUnavailableReason.NothingMeasured, groupedCapacity.Unavailable);
        MetricGroup mapper = Assert.Single(groupedCapacity.Groups);
        Assert.Equal((100, (long?)null, 1L), (mapper.Process!.ProcessId, mapper.Value, mapper.UnknownContributions));

        // No section here was found by its views, so no capacity is known: unmeasured, never zero.
        MetricResult capacity = SessionMetrics.Evaluate(session.Store, Request(Metric.MappingCapacity));
        Assert.Equal((MetricUnavailableReason.NothingMeasured, (long?)null, 1L), (capacity.Unavailable, capacity.Value, capacity.UnknownContributions));

        // Grouped by executable where only 100's lifecycle record names its image, 200's and 300's memberships are no
        // group's, counted by why beside the one no record binds.
        using var named = new TemporarySession();
        Publish(named.Store,
            [.. rows.Select(row => row is { Mechanism: Mechanism.ProcessLifecycle, OwnerProcessId: 100 } ? row with { ResourceName = @"C:\Tools\maker.exe" } : row)],
            fields: fields);
        MetricResult byExecutable = SessionMetrics.Evaluate(named.Store, Request(Metric.ActiveChannels) with { Grouping = LaneGrouping.Executable });
        Assert.Equal(3L, Assert.Single(byExecutable.Groups).Value);
        Assert.Equal(
            [(ProcessBindingReason.BeforeFirstEvidence, 1L), (ProcessBindingReason.ExecutableUnknown, 3L)],
            byExecutable.Unattributed.Select(group => (group.Reason!.Value, group.UnknownContributions)).Order());
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
