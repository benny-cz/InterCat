using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A process instance's package (ADR-094's addendum): the package its creation or rundown named, kept apart from a record
/// that named none and one that said nothing (R3), and kept by a checkpoint from format 1.6, which a checkpoint of an
/// earlier format, read back, says it keeps none of.
/// </summary>
public sealed class ProcessPackageInstanceTests
{
    private const string Installer = "Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe";
    private const string Earlier = "Contoso.Viewer_3.1.0.0_x64__abcdefghjkmnp";
    private const string Tool = "Fabrikam.Tool_2.0.0.0_neutral__qrstvwxyz2345";

    [Fact(DisplayName = "R3: an instance's package is the text its creation or rundown named, empty where the record named none, and unknown where no record said or its package was not read whole; the first record to name one names it")]
    public void AnInstancesPackageIsWhatItsLifecycleRecordsNamed()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Packaged();
        Publish(session.Store, rows, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);

        Assert.True(processes.PackagesKept);
        Assert.Equal(
            [(100, Installer), (200, string.Empty), (300, null), (400, null), (500, Tool), (600, Earlier)],
            processes.Instances.Select(static instance => (instance.ProcessId, instance.PackageFullName)));

        // The package rows change no instance's identity, lifetime or other attribute: the instances are those of the same
        // records without them, but for their packages.
        using var without = new TemporarySession();
        Publish(without.Store, rows, fields: [.. fields.Where(static field => field.Field != SourceField.ProcessPackageFullName)]);
        (SegmentReaderV1[] bare, SegmentReaderV1[] bareFields) = SegmentsOf(without.Store);
        Assert.Equal(
            ProcessInstanceIndex.Derive(bare, TestClock, bareFields).Instances,
            processes.Instances.Select(static instance => instance with { PackageFullName = null }));
    }

    [Fact(DisplayName = "R3: a package named by a value other than 0, which no package row carries, refuses the derivation rather than be read as none or a package")]
    public void APackageNamedByAnotherValueIsRefused()
    {
        using var session = new TemporarySession();
        ObservationRowV1 created = Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\app.exe" };
        Publish(session.Store, [created], fields: [Field(created, SourceField.ProcessPackageFullName, 5)]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        Assert.Contains("by the value 5", Assert.Throws<InvalidDataException>(
            () => ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I14: a checkpoint of format 1.6 keeps each instance's package as derived, a record that named none apart from one that said nothing")]
    public void ACheckpointKeepsEachInstancesPackage()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Packaged();
        Publish(session.Store, rows, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        byte[] bytes = Checkpoint(processes, observations);

        Assert.Equal(6, BitConverter.ToUInt16(bytes, 10));
        DerivationCheckpoint read = DerivationCheckpoint.Read(bytes, Session, TestClock);
        Assert.True(read.Processes.PackagesKept);
        Assert.Equal(processes.Instances, read.Processes.Instances);
    }

    [Fact(DisplayName = "I14: a checkpoint before format 1.6 keeps no package: read back, its instances name none and say so, nor once extended over records that name theirs, and written again it is the format 1.5 a derivation of every record gives")]
    public void AnEarlierCheckpointKeepsNoPackage()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Packaged();
        Publish(session.Store, rows, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);

        // As checkpoints were written before revision 990: format 1.5, which keeps no record's package.
        byte[] earlier = EarlierCheckpoints.WithoutPackages(Checkpoint(processes, observations));
        Assert.Equal(5, BitConverter.ToUInt16(earlier, 10));
        DerivationCheckpoint read = DerivationCheckpoint.Read(earlier, Session, TestClock);
        Assert.False(read.Processes.PackagesKept);
        Assert.Equal(processes.Instances.Select(static instance => instance with { PackageFullName = null }), read.Processes.Instances);
        Assert.True(read.Covers(observations, fieldSegments));

        // A later generation adds a process created in a package. Extended, the instances read its package, and still none of
        // the records the checkpoint covered.
        ObservationRowV1 later = Lifecycle(90, ObservationKind.Create, 700, 20) with { ResourceName = @"C:\Tools\later.exe" };
        Publish(session.Store, [later], fields:
        [
            Field(later, SourceField.ProcessStartSequence, 70),
            Field(later, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = Installer },
        ]);
        (observations, fieldSegments) = SegmentsOf(session.Store);
        ProcessInstanceIndex next = read.Processes.Extend(observations, TestClock, fieldSegments)
            ?? throw new Xunit.Sdk.XunitException("Every segment the checkpoint covers is still offered, so the instances must extend.");
        Assert.False(next.PackagesKept);
        Assert.Equal(Installer, next.Instances.Single(static instance => instance.ProcessId == 700).PackageFullName);
        Assert.Null(next.Instances.Single(static instance => instance.ProcessId == 100).PackageFullName);

        // Written, they keep none of either, as format 1.5 - the bytes a derivation of every record writes without its packages.
        TransportRelationIndex relations = read.Relations.Extend(observations, next)
            ?? throw new Xunit.Sdk.XunitException("No record added changes a decision made, so the relations must extend.");
        ProcessActivityIndex activity = read.Activity!.Extend(observations, next)
            ?? throw new Xunit.Sdk.XunitException("No record added binds otherwise, so the counts must extend.");
        using var written = new MemoryStream();
        _ = DerivationCheckpoint.Write(written, Session, 2, next, relations, activity);
        ProcessInstanceIndex full = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        Assert.Equal(EarlierCheckpoints.WithoutPackages(Checkpoint(full, observations)), written.ToArray());
        DerivationCheckpoint again = DerivationCheckpoint.Read(written.ToArray(), Session, TestClock);
        Assert.False(again.Processes.PackagesKept);
        Assert.All(again.Processes.Instances, static instance => Assert.Null(instance.PackageFullName));
    }

    [Fact(DisplayName = "I14: a checkpoint before format 1.6, read with the source-field segments it covers, takes its records' packages from their package rows - its instances, and the checkpoint written of them, those a derivation of every record gives - and is refused given other segments; one that keeps packages asks for none")]
    public void AnEarlierCheckpointReadWithItsSegmentsNamesThePackages()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Packaged();
        Publish(session.Store, rows, fields: fields, rowsPerSegment: 3);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        Assert.True(fieldSegments.Length > 1);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
        byte[] current = Checkpoint(processes, observations);
        byte[] earlier = EarlierCheckpoints.WithoutPackages(current);

        // Given the segments it covers, as the generation records them, the instances read their packages from them.
        IReadOnlyList<StoreDependency>? asked = null;
        DerivationCheckpoint read = DerivationCheckpoint.Read(earlier, Session, TestClock, covered =>
        {
            asked = covered;
            return fieldSegments;
        });
        Assert.Equal(
            fieldSegments.Select(static segment => segment.Published!).OrderBy(static file => file.Name, StringComparer.Ordinal),
            asked!.OrderBy(static file => file.Name, StringComparer.Ordinal));
        Assert.True(read.Processes.PackagesKept);
        Assert.Equal(processes.Instances, read.Processes.Instances);
        Assert.Equal(current, Checkpoint(read.Processes, observations));

        // Without them, or where the generation does not name them, it keeps none; given others, it is refused.
        Assert.False(DerivationCheckpoint.Read(earlier, Session, TestClock, static _ => null).Processes.PackagesKept);
        Assert.Contains("not the ones it covers", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(earlier, Session, TestClock, _ => fieldSegments[..1])).Message, StringComparison.Ordinal);

        // A checkpoint that keeps packages asks for no segment.
        Assert.True(DerivationCheckpoint.Read(current, Session, TestClock,
            static _ => throw new Xunit.Sdk.XunitException("A checkpoint of format 1.6 keeps every package.")).Processes.PackagesKept);
    }

    [Fact(DisplayName = "I14: a checkpoint before format 1.6 that claims a record's package, which it cannot hold, is refused rather than read")]
    public void AnEarlierCheckpointClaimingAPackageIsRefused()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = Packaged();
        Publish(session.Store, rows, fields: fields);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(session.Store);
        byte[] claimed = Checkpoint(ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments), observations);
        claimed[10] = 5;
        Assert.Contains("process fields are marked", Assert.Throws<InvalidDataException>(
            () => DerivationCheckpoint.Read(claimed, Session, TestClock)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// winget's process created in its package; a child created in none, its package the value 0; a process whose package
    /// its record could not hold whole; one whose start version named no package; one running at the capture's start whose
    /// first rundown could not hold its package and whose last one named it; and one created in a package whose stop's
    /// rundown names another - each with its start sequence.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) Packaged()
    {
        ObservationRowV1 winget = Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Program Files\WindowsApps\winget.exe" };
        ObservationRowV1 child = Lifecycle(20, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\child.exe" };
        ObservationRowV1 cut = Lifecycle(30, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\cut.exe" };
        ObservationRowV1 older = Lifecycle(40, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\older.exe" };
        ObservationRowV1 atStart = Lifecycle(1, ObservationKind.Inventory, 500, 5) with { ResourceName = @"C:\Tools\tool.exe" };
        ObservationRowV1 atStop = Lifecycle(80, ObservationKind.Inventory, 500, 6) with { ResourceName = @"C:\Tools\tool.exe" };
        ObservationRowV1 viewer = Lifecycle(50, ObservationKind.Create, 600, 7) with { ResourceName = @"C:\Tools\viewer.exe" };
        ObservationRowV1 viewerAtStop = Lifecycle(81, ObservationKind.Inventory, 600, 8) with { ResourceName = @"C:\Tools\viewer.exe" };
        return (
            [winget, child, cut, older, atStart, atStop, viewer, viewerAtStop],
            [
                Field(winget, SourceField.ProcessStartSequence, 10),
                Text(winget, Installer),
                Field(child, SourceField.ProcessStartSequence, 20),
                Field(child, SourceField.ProcessPackageFullName, 0),
                Field(cut, SourceField.ProcessStartSequence, 30),
                Field(cut, SourceField.ProcessPackageFullName, 0) with { Value = null, Availability = FieldAvailability.EventLost },
                Field(older, SourceField.ProcessStartSequence, 40),
                Field(atStart, SourceField.ProcessStartSequence, 50),
                Field(atStart, SourceField.ProcessPackageFullName, 0) with { Value = null, Availability = FieldAvailability.EventLost },
                Field(atStop, SourceField.ProcessStartSequence, 50),
                Text(atStop, Tool),
                Field(viewer, SourceField.ProcessStartSequence, 60),
                Text(viewer, Earlier),
                Field(viewerAtStop, SourceField.ProcessStartSequence, 60),
                Text(viewerAtStop, Installer),
            ]);

        static SourceFieldRowV1 Text(ObservationRowV1 row, string package) =>
            Field(row, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = package };
    }

    /// <summary>The checkpoint of <paramref name="processes"/> with the relations and counts derived with them.</summary>
    private static byte[] Checkpoint(ProcessInstanceIndex processes, SegmentReaderV1[] observations)
    {
        using var destination = new MemoryStream();
        _ = DerivationCheckpoint.Write(destination, Session, 2, processes,
            TransportRelationIndex.Derive(observations, processes), ProcessActivityIndex.Derive(observations, processes));
        return destination.ToArray();
    }
}
