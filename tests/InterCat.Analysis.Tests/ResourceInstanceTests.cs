using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// `resource-instances-v1` (ADR-097): a generation's named synchronization objects and sections by their handles, each the
/// number a capture's gate gave one object, from its first record to its last, named as its closes and the stop's rundown
/// name it, typed as the stop's rundown names its type, and held by the process instances its records bind to.
/// </summary>
public sealed class ResourceInstanceTests
{
    private const string Ready = ReadyObject;

    [Fact(DisplayName = "R3: a resource is its gate's number from its first record to its last, named only as a close or the stop names it, its ends witnessed or said not to be, and held by the processes its records bind to")]
    public void AResourceIsItsNumbersLifetime()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = NamedObjects();
        Publish(session.Store, rows, fields: fields);
        (IReadOnlyList<ResourceInstance> resources, ProcessInstanceIndex processes) = Derive(session.Store);
        int Pid(ProcessInstanceId id) => processes.Find(id)!.ProcessId;

        // By mechanism, then by number: the section, then the four synchronization objects; a zero number is none. Each is
        // of the layer its handle records are, a resource's.
        Assert.Equal(
            [(Mechanism.SharedSection, 9L), (Mechanism.Synchronization, 1L), (Mechanism.Synchronization, 2L),
                (Mechanism.Synchronization, 3L), (Mechanism.Synchronization, 4L)],
            resources.Select(resource => (resource.Mechanism, resource.Number)));
        Assert.All(resources, resource => Assert.Equal(ObservationLayer.Resource, resource.Layer));

        // Made by 100, duplicated into 200, closed in each: its whole lifetime seen, named only by its closes.
        ResourceInstance ready = resources.Single(resource => resource.Number == 1);
        Assert.Equal((10L, 40L, true, true, false), (ready.FirstNativeTicks, ready.LastNativeTicks, ready.FirstHandleMade,
            ready.LastHandleClosed, ready.HeldAtStop));
        Assert.Equal([Ready], ready.Names);
        Assert.Equal(("Event", 4L), (ready.TypeName, ready.Records));
        Assert.Equal(@"\Sessions\1\BaseNamedObjects", ResourceNames.NamespaceOf(ready.Name!));
        Assert.Equal("Ready", ResourceNames.LeafOf(ready.Name!));
        Assert.Equal(
            [(100, 1, 0, 1, 0), (200, 0, 1, 1, 0)],
            ready.Holders.Select(holder => (Pid(holder.Process), holder.HandlesMade, holder.HandlesDuplicated,
                holder.HandlesClosed, holder.HeldAtStop)).OrderBy(holder => holder.Item1));
        Assert.Empty(ready.Unbound);

        // A handle made before the capture is seen only at its close: its start is not witnessed.
        ResourceInstance before = resources.Single(resource => resource.Number == 2);
        Assert.Equal((false, true, "Mutant", @"\BaseNamedObjects\Global\Lock"),
            (before.FirstHandleMade, before.LastHandleClosed, before.TypeName, before.Name));

        // One held at the stop is named by the stop's rundown and its end is not witnessed.
        ResourceInstance held = resources.Single(resource => resource.Number == 3);
        Assert.Equal((true, false, true, @"\Sessions\1\BaseNamedObjects\Held"),
            (held.FirstHandleMade, held.LastHandleClosed, held.HeldAtStop, held.Name));
        Assert.Equal((300, 1, 0, 0, 1), held.Holders.Select(holder => (Pid(holder.Process), holder.HandlesMade,
            holder.HandlesDuplicated, holder.HandlesClosed, holder.HeldAtStop)).Single());

        // One made and never closed nor held at the stop is unnamed - never named the empty text - its end unseen; and the
        // duplication into a process before its first lifecycle record names it no holder, counted by why.
        ResourceInstance cut = resources.Single(resource => resource.Number == 4);
        Assert.Equal((null, 0, false, false), (cut.Name, cut.Names.Count, cut.LastHandleClosed, cut.HeldAtStop));
        Assert.Equal([100], cut.Holders.Select(holder => Pid(holder.Process)));
        Assert.Equal(1L, cut.Unbound[ProcessBindingReason.BeforeFirstEvidence]);

        // A section by its handles, typed as the stop names its type.
        ResourceInstance section = resources.Single(resource => resource.Mechanism == Mechanism.SharedSection);
        Assert.Equal(("Section", @"\Sessions\1\BaseNamedObjects\SharedMap", true, true),
            (section.TypeName, section.Name, section.FirstHandleMade, section.LastHandleClosed));
    }

    [Fact(DisplayName = "R3: a name two records give differently is each kept, and a resource whose type no rundown named is typed by none")]
    public void NamesAreKeptAsRecorded()
    {
        ObservationRowV1 made = Handle(10, HandleCreated, 100, null, 2);
        ObservationRowV1 first = Handle(20, HandleClosed, 100, null, 3) with { ResourceName = Ready };
        ObservationRowV1 second = Handle(30, HandleAtStop, 100, 100, 4) with { ResourceName = Ready.ToUpperInvariant() };
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 100, 1), made, first, second],
            fields: [.. new[] { made, first, second }.SelectMany(row => new[]
            {
                Field(row, SourceField.SynchronizationObjectNumber, 7), Field(row, SourceField.ObjectTypeNumber, 16),
            })]);
        ResourceInstance resource = Assert.Single(Derive(session.Store).Resources);
        Assert.Equal([Ready, Ready.ToUpperInvariant()], resource.Names);
        Assert.Null(resource.TypeName);
        Assert.Null(ResourceNames.NamespaceOf("NoDirectory"));
        Assert.Equal(@"\", ResourceNames.NamespaceOf(@"\Root"));
    }

    [Fact(DisplayName = "R3: an AF_UNIX socket is a resource with the traffic its records carried, one that carried none is idle, and a synchronization object's traffic is none recorded, never idle")]
    public void ASocketCarriesItsTraffic()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(session.Store, rows, fields: fields);
        (IReadOnlyList<ResourceInstance> resources, ProcessInstanceIndex processes) = Derive(session.Store);
        ResourceInstance[] sockets = [.. resources.Where(resource => resource.Mechanism == Mechanism.UnixDomainSocket)];

        // Each socket's records are its own: the TCP send whose connection id is 5 is none of them. A socket is of the layer
        // its records are, the transport's.
        Assert.Equal([(5L, 3L), (6L, 5L), (7L, 5L), (8L, 1L)], sockets.Select(socket => (socket.Number, socket.Records)));
        Assert.All(sockets, socket => Assert.Equal(ObservationLayer.Transport, socket.Layer));
        Assert.Equal(
            [
                (5L, new ResourceTraffic(0, 0, 0, 0, 0)), (6L, new ResourceTraffic(1, 1, 96, 120, 0)),
                (7L, new ResourceTraffic(1, 1, 120, 0, 1)), (8L, new ResourceTraffic(1, 0, 64, 0, 0)),
            ],
            sockets.Select(socket => (socket.Number, socket.Traffic!)));
        Assert.Equal([true, false, false, false], sockets.Select(socket => socket.Traffic!.Idle));

        // A socket created and closed in the capture is seen whole; one made before it, seen only sending, is neither.
        Assert.Equal([(true, true), (true, true), (true, true), (false, false)],
            sockets.Select(socket => (socket.FirstHandleMade, socket.LastHandleClosed)));

        // Its holder is the process its records name, its create a handle made and its close one closed; it has no name.
        ResourceInstance listener = sockets[0];
        Assert.Equal((100, 1, 0, 1, 0), listener.Holders.Select(holder => (processes.Find(holder.Process)!.ProcessId, holder.HandlesMade,
            holder.HandlesDuplicated, holder.HandlesClosed, holder.HeldAtStop)).Single());
        Assert.Equal((null, null), (listener.Name, listener.TypeName));

        // A synchronization object records no transfer, so its traffic is none recorded - null - never an idle zero.
        Assert.Null(Assert.Single(resources, resource => resource.Mechanism == Mechanism.Synchronization).Traffic);
        Assert.Equal("resource-instances-v3", ResourceInstanceIndex.Rule);
    }

    [Fact(DisplayName = "R3: a section by its views is a resource of its own, never joined to its handles' number, with the views its processes mapped, the largest extent one mapped, and an image's said to be one")]
    public void ASectionByItsViewsIsItsOwn()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        (IReadOnlyList<ResourceInstance> resources, ProcessInstanceIndex processes) = Derive(session.Store);
        (int, int, int, int) Views(ResourceHolder holder) =>
            (processes.Find(holder.Process)!.ProcessId, holder.ViewsMapped, holder.ViewsUnmapped, holder.ViewsListed);

        // Section 4 by its handles and by its views are two instances, the handles' first; the views' have no name.
        Assert.Equal([(false, 4L), (true, 4L), (true, 5L), (true, 6L), (true, 7L)], resources.Select(resource => (resource.ByViews, resource.Number)));
        ResourceInstance handles = resources[0];
        Assert.Equal((@"\BaseNamedObjects\Shared4", 0, (long?)null), (handles.Name, handles.Views, handles.LargestView));

        // Each process mapped and unmapped a view of section 4, 64 KiB the largest: its whole lifetime seen, held by both.
        // A view whose record names no section is no section's.
        ResourceInstance shared = resources[1];
        Assert.Equal((false, 2, (long?)65536, true, true, false, (string?)null), (shared.Image, shared.Views, shared.LargestView,
            shared.FirstHandleMade, shared.LastHandleClosed, shared.HeldAtStop, shared.Name));
        Assert.Equal([(100, 1, 1, 0), (200, 1, 1, 0)], shared.Holders.Select(Views));
        Assert.Null(shared.Traffic);

        // An image's view, held at the stop, is said to be an image's.
        ResourceInstance image = resources[2];
        Assert.Equal((true, true, false), (image.Image, image.HeldAtStop, image.LastHandleClosed));
        Assert.Equal([(100, 1, 0, 1)], image.Holders.Select(Views));

        // A view the start's rundown lists was mapped before the capture: its start is not witnessed, and it is listed.
        ResourceInstance before = resources[3];
        Assert.Equal((false, true, false), (before.FirstHandleMade, before.LastHandleClosed, before.HeldAtStop));
        Assert.Equal([(200, 0, 1, 1)], before.Holders.Select(Views));

        // A rundown is an inventory at the start as at the stop: a view only the start's lists was not seen held at the stop.
        ResourceInstance unseen = resources[4];
        Assert.Equal((false, false, false, (long?)16384), (unseen.FirstHandleMade, unseen.LastHandleClosed, unseen.HeldAtStop, unseen.LargestView));
        Assert.Equal([(200, 0, 0, 1)], unseen.Holders.Select(Views));
        Assert.Equal("resource-instances-v3", ResourceInstanceIndex.Rule);
    }

    [Fact(DisplayName = "R3: a section's view records pair into its mappings by view and process - each mapping to its unmapping, one listed at the start mapped before, one the stop held kept open - and a reading not seen is none")]
    public void AViewsRecordsPairIntoItsMappings()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        (IReadOnlyList<ResourceInstance> resources, ProcessInstanceIndex processes) = Derive(session.Store);
        (long, int?, long?, long?, bool, bool) Said(ResourceView mapping) => (mapping.View,
            mapping.Holder is { } holder ? processes.Find(holder)!.ProcessId : null, mapping.MappedNativeTicks, mapping.UnmappedNativeTicks,
            mapping.MappedBefore, mapping.HeldAtStop);
        IReadOnlyList<ResourceView> Of(long number) => resources.Single(resource => resource.ByViews && resource.Number == number).Mappings;

        // Section 4's two views, each process's; 5's, the image's, held at the stop; 6's listed at the start and unmapped;
        // 7's listed at the start alone; and no section by its handles has a mapping.
        Assert.Equal([(41L, (int?)100, (long?)10, (long?)31, false, false), (42L, (int?)200, (long?)11, (long?)30, false, false)], Of(4).Select(Said));
        Assert.Equal([(51L, (int?)100, (long?)12, (long?)null, false, true)], Of(5).Select(Said));
        Assert.Equal([(61L, (int?)200, (long?)null, (long?)50, true, false)], Of(6).Select(Said));
        Assert.Equal([(71L, (int?)200, (long?)null, (long?)null, true, false)], Of(7).Select(Said));
        Assert.Empty(resources.Single(resource => !resource.ByViews && resource.Number == 4).Mappings);

        // A view mapped again before its unmapping leaves the first's end unseen, and an unmapping no mapping of is the capture's
        // was mapped unseen.
        using var again = new TemporarySession();
        var twice = new List<ObservationRowV1> { Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\writer.exe" } };
        var numbered = new List<SourceFieldRowV1>();
        foreach ((long ticks, byte opcode, long view) in new (long, byte, long)[] { (10, 37, 91), (20, 37, 91), (30, 38, 91), (40, 38, 92) })
        {
            ObservationRowV1 row = SectionRecord(ticks, opcode, 100, 100, (ulong)ticks, view: true);
            twice.Add(row);
            numbered.AddRange([Field(row, SourceField.SectionNumber, 9), Field(row, SourceField.SectionViewNumber, view),
                Field(row, SourceField.ViewSize, 4096), Field(row, SourceField.ViewInformation, 0)]);
        }

        Publish(again.Store, twice, fields: numbered);
        (resources, processes) = Derive(again.Store);
        Assert.Equal(
            [(91L, (int?)100, (long?)10, (long?)null, false, false), (91L, (int?)100, (long?)20, (long?)30, false, false), (92L, (int?)100, (long?)null, (long?)40, false, false)],
            Of(9).Select(Said));
    }

    [Fact(DisplayName = "R3: a resource's records are exactly those its gate numbered it in - a section's views apart from its handles, and a socket's never a TCP connection's of the same number")]
    public void AResourcesRecordsAreItsNumbers()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        SegmentReaderV1[] fieldSegments = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        ObservationKind[] KindsOf(IReadOnlyList<(int Segment, int Row)> records) =>
            [.. records.Select(record => (ObservationKind)segments[record.Segment].Slice(SegmentColumnId.ObservationKind).UnsignedAt(record.Row)!.Value)];

        // Section 4's views - two mapped, two unmapped - and apart from them its handles' creation and close.
        Assert.Equal([ObservationKind.Map, ObservationKind.Map, ObservationKind.Unmap, ObservationKind.Unmap],
            KindsOf(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.SharedSection, true, 4)));
        Assert.Equal([ObservationKind.Open, ObservationKind.Close],
            KindsOf(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.SharedSection, false, 4)));
        Assert.Empty(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.Synchronization, false, 4));
        Assert.Empty(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.SharedSection, true, 0));

        // Socket 5's three records, never the TCP send whose connection id is 5 too.
        using var sockets = new TemporarySession();
        (rows, fields) = UnixSockets();
        Publish(sockets.Store, rows, fields: fields);
        manifest = sockets.Store.Current!;
        segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(sockets.Store, manifest, name))];
        fieldSegments = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(sockets.Store, manifest, name))];
        Assert.Equal(3, ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.UnixDomainSocket, false, 5).Count);

        // Object 1's four handle records, never section 9's; and none for the 0 a record carries where its gate gave none.
        using var objects = new TemporarySession();
        (rows, fields) = NamedObjects();
        Publish(objects.Store, rows, fields: fields);
        manifest = objects.Store.Current!;
        segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(objects.Store, manifest, name))];
        fieldSegments = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(objects.Store, manifest, name))];
        Assert.Equal([ObservationKind.Open, ObservationKind.Open, ObservationKind.Close, ObservationKind.Close],
            KindsOf(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.Synchronization, false, 1)));
        Assert.Empty(ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.Synchronization, false, 0));
        Assert.Equal(2, ResourceInstanceIndex.RecordsOf(segments, fieldSegments, Mechanism.SharedSection, false, 9).Count);
    }

    [Fact(DisplayName = "R3: a later holder of a reused PID holds a resource only as a candidate, which the default policy counts by that reason and includes when asked")]
    public void ACandidateHolderIsCountedByThePolicy()
    {
        ObservationRowV1 made = Handle(10, HandleCreated, 100, null, 4);
        ObservationRowV1 closed = Handle(20, HandleClosed, 100, null, 5) with { ResourceName = Ready };
        using var session = new TemporarySession();
        Publish(session.Store,
            [Lifecycle(1, ObservationKind.Create, 100, 1), Lifecycle(5, ObservationKind.Exit, 100, 2), Lifecycle(6, ObservationKind.Create, 100, 3), made, closed],
            fields: [Field(made, SourceField.SynchronizationObjectNumber, 7), Field(closed, SourceField.SynchronizationObjectNumber, 7)]);
        (IReadOnlyList<ResourceInstance> resources, ProcessInstanceIndex processes) = Derive(session.Store);
        ResourceInstance resource = Assert.Single(resources);
        Assert.Empty(resource.Holders);
        Assert.Equal(2L, resource.Unbound[ProcessBindingReason.NotAdmittedByPolicy]);

        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(session.Store, manifest, name))];
        ResourceInstance counted = Assert.Single(ResourceInstanceIndex.Derive(segments, fields, processes, EvidencePolicy.IncludeCandidates).Instances);
        Assert.Equal((100, 2u), (processes.Find(Assert.Single(counted.Holders).Process)!.ProcessId,
            processes.Find(counted.Holders[0].Process)!.LifecycleEpoch));
        Assert.Empty(counted.Unbound);
    }

    private static (IReadOnlyList<ResourceInstance> Resources, ProcessInstanceIndex Processes) Derive(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock, fields);
        return (ResourceInstanceIndex.Derive(segments, fields, processes).Instances, processes);
    }
}
