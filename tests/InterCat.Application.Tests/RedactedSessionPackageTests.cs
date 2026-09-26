using System.Text;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §11.3's redacted normalized session (I22): it reopens as a session, reproduces what every analysis concludes from its
/// source, and holds none of the source's names, identities, locators, absolute readings or payload bytes.
/// </summary>
public sealed class RedactedSessionPackageTests
{
    // The source clock's epoch is a boot-relative reading two and a half hours in: the package must not reveal it.
    private const long SourceEpoch = 90_000_000_000;
    private const long PreEpochOffset = -5_000_000;

    private static readonly Guid PrivateProvider = Guid.Parse("c0de5ec2-0000-4000-8000-00000000c0de");
    private static readonly Guid UnrequestedProvider = Guid.Parse("c0de5ec2-1111-4000-8000-00000000c0de");
    private static readonly Guid SecretInterface = Guid.Parse("5ec2e7a1-2222-4333-8444-555555555555");
    private static readonly Guid SecretActivity = Guid.Parse("5ec2e7a1-3333-4333-8444-666666666666");
    private static readonly Guid SecretRelated = Guid.Parse("5ec2e7a1-4444-4333-8444-777777777777");
    private static readonly ClockId SourceClockId = new(Guid.Parse("5ec2e7a1-5555-4333-8444-888888888888"));
    private const string SecretPath = @"C:\Program Files\Contoso Secret\Agent Service.exe";
    private const string SecretPathOtherCase = @"c:\program files\contoso secret\agent service.exe";
    private const string SecretPipe = @"\Device\NamedPipe\Contoso-Secret-Pipe";
    private const string SecretBody = "SECRET BODY BYTES FROM THE SOURCE";
    private const long SecretFileTime = 133_444_555_666_777_888;
    private static readonly int[] SourceProcessIds = [1200, 1300, 1400, 1500];

    private static readonly SourceClockDescriptor SourceClock = new(
        SourceClockId,
        HostId.Derive("redacted-package-source-host"),
        SourceClockKind.Monotonic,
        TimestampEncoding.Qpc,
        10_000_000,
        SourceEpoch,
        TimestampRounding.NearestEven,
        SourceClockMath.SessionTicksPerSecond * 60);

    [Fact(DisplayName = "I22: a redacted package reproduces its source's graph, processes, coverage and totals")]
    public void ReproducesWhatAnalysesConclude()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();

        RedactedSessionPackageResult result = RedactedSessionPackage.Create(source.Store, package.Path, Committed);
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));

        SessionOverviewBundle before = SessionOverviewProjector.Project(source.Store);
        SessionOverviewBundle after = SessionOverviewProjector.Project(shared);
        Assert.Equal(before.Nodes.Count, after.Nodes.Count);
        Assert.Equal(before.Groups.Count, after.Groups.Count);
        Assert.Equal(before.Edges.Count, after.Edges.Count);
        Assert.Equal(before.Channels.Count, after.Channels.Count);
        Assert.Equal(before.ObservationRows, after.ObservationRows);
        Assert.Equal(before.RowsWithoutSessionTime, after.RowsWithoutSessionTime);
        Assert.Equal(before.GraphEligibleRows, after.GraphEligibleRows);
        Assert.Equal(before.UnresolvedTcpRows, after.UnresolvedTcpRows);
        Assert.Equal(before.RelationshipsNotAdmitted, after.RelationshipsNotAdmitted);
        Assert.Equal(before.Extent, after.Extent);
        Assert.Equal(before.Timeline.Select(bucket => (bucket.Interval, bucket.ObservationCount, bucket.Coverage)),
            after.Timeline.Select(bucket => (bucket.Interval, bucket.ObservationCount, bucket.Coverage)));
        Assert.Equal(before.MechanismCoverage.Select(entry => (entry.Mechanism, entry.State)),
            after.MechanismCoverage.Select(entry => (entry.Mechanism, entry.State)));
        Assert.Equal(before.Minimap!.Capture, after.Minimap!.Capture);
        Assert.True(after.CoverageLedgerPublished);

        // Nodes labelled alike before are labelled alike after: a path and its case variant are one executable, and an
        // exit-only instance's bare name is the same executable as the path's file name.
        Assert.Equal(LabelPartition(before), LabelPartition(after));
        Assert.Contains(after.Nodes, node => node.ProcessId == 4);
        Assert.All(after.Nodes.Where(node => node.ProcessId != 4),
            node => Assert.DoesNotContain(node.ProcessId, SourceProcessIds));
        Assert.All(after.Nodes, node => Assert.StartsWith(RedactedSessionPseudonyms.ExecutablePrefix, node.Name));

        ProcessInstanceIndex sourceProcesses = ProcessInstanceIndex.Derive(Segments(source.Store), SourceClock,
            FieldSegments(source.Store));
        SourceClockDescriptor packageClock = SessionSegments.SourceClock(shared.Root, shared.Current!)!.Value;
        ProcessInstanceIndex packageProcesses = ProcessInstanceIndex.Derive(Segments(shared), packageClock,
            FieldSegments(shared));
        Assert.True(sourceProcesses.StartKeysAvailable);
        Assert.True(packageProcesses.StartKeysAvailable);
        Assert.Equal(Shape(sourceProcesses), Shape(packageProcesses));

        // Row by row, every record binds its owner, finds its peer and joins a channel - or fails to, for the same
        // reason - exactly as its source row did.
        Assert.Equal(Outcomes(Segments(source.Store), sourceProcesses), Outcomes(Segments(shared), packageProcesses));

        foreach (LaneGrouping grouping in new[] { LaneGrouping.Executable, LaneGrouping.InstanceOnly, LaneGrouping.Mechanism })
        {
            MetricRequest request = new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = Metric.BytesSent,
                ByteDomain = ByteDomain.TransportObserved,
                AccountingSide = AccountingSide.SendSide,
                Grouping = grouping,
            };
            MetricResult sent = SessionMetrics.Evaluate(source.Store, request);
            MetricResult shared_ = SessionMetrics.Evaluate(shared, request);
            Assert.Equal(sent.Value, shared_.Value);
            Assert.Equal(sent.Groups.Select(group => group.Value).Order(), shared_.Groups.Select(group => group.Value).Order());
        }

        Assert.Equal(SessionEvidenceQuery.ReadScope(source.Store, 1_000).Records.Count,
            SessionEvidenceQuery.ReadScope(shared, 1_000).Records.Count);
        Assert.Equal(result.Source.Rows, result.Counts.Rows);
        Assert.True(result.FilesVerified > 5);
        Assert.True(result.IdentityNeedles > 0);
        Assert.True(result.NameNeedles > 0);
    }

    [Fact(DisplayName = "P17: a package labelled redacted carries none of its source's evidence: no journal, segment or plan of it")]
    public void APackageCarriesNoSourceEvidence()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();
        _ = RedactedSessionPackage.Create(source.Store, package.Path, Committed);

        SessionManifestV1 original = source.Store.Current!;
        SessionManifestV1 manifest = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path)).Current!;
        StoreDependency[] evidence = [.. original.Dependencies.Where(dependency => dependency.Kind is StoreDependencyKind.Journal
            or StoreDependencyKind.Segment or StoreDependencyKind.Dictionary or StoreDependencyKind.Index
            or StoreDependencyKind.DerivationPlan)];
        Assert.NotEmpty(evidence);

        // Not one of them is a dependency of the package, and no package file holds one's bytes - a package file may share
        // a conventional name, such as its own synthetic journal's, but never the content: the package is built from
        // synthetic records, never from a copy of what was captured.
        foreach (StoreDependency dependency in evidence)
        {
            Assert.DoesNotContain(manifest.Dependencies, candidate => candidate.Digest == dependency.Digest);
            byte[] bytes = File.ReadAllBytes(Path.Combine(source.Path, dependency.Name));
            foreach (string file in Directory.EnumerateFiles(package.Path, "*", SearchOption.AllDirectories))
            {
                Assert.False(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(file)), $"{Path.GetFileName(file)} is {dependency.Name}");
            }
        }
    }

    [Fact(DisplayName = "I22: no source name, identity, reading, locator or body byte reaches a package file")]
    public void HoldsNoSourceValue()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();
        RedactedSessionPackageResult result = RedactedSessionPackage.Create(source.Store, package.Path, Committed);

        SessionManifestV1 original = source.Store.Current!;
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));
        SessionManifestV1 manifest = shared.Current!;
        Assert.NotEqual(original.SessionId, manifest.SessionId);
        Assert.Equal(result.SessionId, manifest.SessionId);
        Assert.Equal(RedactedSessionPackage.Contract, manifest.SourceIdentity);
        Assert.Single(manifest.Dependencies, dependency => dependency.Kind == StoreDependencyKind.RedactionPolicy);
        Assert.Single(manifest.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Journal);
        Assert.DoesNotContain(manifest.Dependencies, dependency => dependency.Kind is StoreDependencyKind.DerivationPlan
            or StoreDependencyKind.CaptureFinalization or StoreDependencyKind.Index);

        string[] texts = [SecretPath, SecretPathOtherCase, SecretPipe, "Contoso", "Secret", "Agent Service", SecretBody,
            "metric-tests", "Contoso-Secret-Provider"];
        Guid[] identities = [original.SessionId, Capture.Value, SourceClockId.Value, SourceClock.HostId.Value,
            PrivateProvider, UnrequestedProvider, SecretInterface, SecretActivity, SecretRelated];
        foreach (string path in Directory.EnumerateFiles(package.Path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            foreach (string text in texts)
            {
                Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0, $"{Path.GetFileName(path)}: {text}");
                Assert.False(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0, $"{Path.GetFileName(path)}: {text}");
            }

            foreach (Guid identity in identities)
            {
                Assert.False(bytes.AsSpan().IndexOf(identity.ToByteArray(bigEndian: true)) >= 0, $"{Path.GetFileName(path)}: {identity}");
                Assert.False(bytes.AsSpan().IndexOf(identity.ToByteArray()) >= 0, $"{Path.GetFileName(path)}: {identity}");
                Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(identity.ToString("D"))) >= 0,
                    $"{Path.GetFileName(path)}: {identity}");
            }

            Assert.False(bytes.AsSpan().IndexOf(BitConverter.GetBytes(SecretFileTime)) >= 0, Path.GetFileName(path));
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(original.Digest[7..])) >= 0, Path.GetFileName(path));
        }

        // Every reading moved: the package's epoch is one second, because a timed reading preceded the source epoch.
        SourceClockDescriptor clock = SessionSegments.SourceClock(shared.Root, manifest)!.Value;
        Assert.NotEqual(SourceClockId, clock.Id);
        Assert.NotEqual(SourceClock.HostId, clock.HostId);
        Assert.Equal(10_000_000, clock.CaptureEpochNativeTicks);
        ObservationRowV1[] rows = [.. Segments(shared).SelectMany(Rows)];
        Assert.All(rows, row => Assert.True(row.NativeTicks < SourceEpoch));
        Assert.All(rows.Where(row => row.SessionRelativeTicks is not null), row => Assert.True(row.NativeTicks >= 0));
        Assert.Contains(rows, row => row.SessionRelativeTicks == PreEpochOffset * 100);
        Assert.All(rows, row => Assert.Equal(row.RawRecordOrdinal - 1, row.JournalRecordIndex));
        Assert.All(rows, row => Assert.Equal(0, row.ProcessorNumber));
        Assert.Contains(rows, row => (row.Markers & SegmentRowMarkers.ResourceNameTruncated) != 0);
        Assert.All(rows, row => Assert.Equal(SegmentRowMarkers.None, row.Markers & ~SegmentRowMarkers.ResourceNameTruncated));

        // Loopback, the unspecified port, the System PID and the public providers keep their meaning; nothing else does.
        Assert.Contains(rows, row => row.SourceEndpointAddress == 0x7F00_0001);
        Assert.Contains(rows, row => row.SourceEndpointPort == 0);
        Assert.DoesNotContain(rows, row => row.SourceEndpointAddress is 0x0A01_0203 or 0xC0A8_010A or 0xC0A8_0114
            || row.DestinationEndpointAddress is 0xCB00_7109 or 0xC0A8_010A or 0xC0A8_0114);
        Assert.DoesNotContain(rows, row => row.SourceEndpointPort is 50000 or 51000 or 52000 or 8443 or 9000
            || row.DestinationEndpointPort is 443 or 8443 or 50000 or 9000 or 52000);
        Assert.All(rows.Where(row => row.SourceEndpointPort is > 0), row => Assert.True(row.SourceEndpointPort >= 1024));
        Assert.Contains(rows, row => row.ProviderId == NetworkProvider);
        Assert.DoesNotContain(rows, row => row.ProviderId == PrivateProvider);
        Assert.All(rows, row => Assert.StartsWith("redacted-schema-", row.SchemaFingerprint));

        // The original-record drill-down resolves only into the package's synthetic journal.
        SessionEvidenceRecord record = SessionEvidenceQuery.ReadScope(shared, 1_000).Records[0];
        SessionRawRecordDetail raw = SessionRawRecordQuery.ReadRetained(shared, manifest.SessionId, record, revealBodyBytes: true);
        Assert.True(raw.Available);
        Assert.Equal(RedactedSessionPackage.Policy, raw.AdmissionPolicyId);
        Assert.Equal(BodyDispositionV1.NoBody, raw.BodyDisposition);
        Assert.Equal(0, raw.RetainedBodyLength);
        Assert.Empty(raw.ExtendedItems);
        Assert.Equal(record.Observation.ProviderId, raw.Header!.Value.ProviderId);

        SessionRedaction redaction = Assert.IsType<SessionRedaction>(SessionRedaction.Read(shared.Root, manifest));
        Assert.Equal(RedactedSessionPackage.Policy, redaction.Policy);
        Assert.Equal(result.Counts, redaction.Counts);
        Assert.Null(SessionRedaction.Read(source.Store.Root, original));
        Assert.Equal(SessionOverviewProjector.Project(shared).Redaction, redaction);
    }

    [Fact(DisplayName = "I22: source fields are kept, pseudonymized or redacted by their allowlist")]
    public void SourceFieldsFollowTheirPolicy()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();
        RedactedSessionPackageResult result = RedactedSessionPackage.Create(source.Store, package.Path, Committed);
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));

        SourceFieldRowV1[] fields = [.. FieldSegments(shared).SelectMany(FieldRows)];
        Assert.Equal(result.Source.SourceFieldRows, fields.Length);
        Assert.All(fields, field => Assert.Null(field.Text));
        Assert.All(fields.Where(field => field.Field is SourceField.ProcessCreateTime), field =>
        {
            Assert.Null(field.Value);
            Assert.Equal(FieldAvailability.Redacted, field.Availability);
        });
        Assert.Single(fields, field => field.Field == SourceField.FileKey && field.Availability == FieldAvailability.Redacted);
        Assert.Equal([0L, 1L], fields.Where(field => field.Field == SourceField.ProcessSessionId)
            .Select(field => field.Value!.Value).Order());
        Assert.Contains(fields, field => field.Field == SourceField.RpcProcedureNumber && field.Value == 7);
        Assert.DoesNotContain(fields, field => field.Field is SourceField.ProcessStartSequence && field.Value is 5001 or 5002);
        Assert.DoesNotContain(fields, field => field.Field is SourceField.FileObject && field.Value == unchecked((long)0xFFFF_8A0C_1234_5670));
        Assert.Equal(3, result.Counts.SourceFieldRowsRedacted);

        // A parent named by PID is the same pseudonym as that process's own records, so the tree survives; System's
        // PID 4 is a fixed point.
        ObservationRowV1[] rows = [.. Segments(shared).SelectMany(Rows)];
        long[] parents = [.. fields.Where(field => field.Field == SourceField.ParentProcessId).Select(field => field.Value!.Value)];
        Assert.Contains(4L, parents);
        long agent = Assert.Single(parents, parent => parent != 4);
        Assert.Contains(rows, row => row.OwnerProcessId == agent && row.Kind == ObservationKind.Create);
        Assert.NotEqual(1200L, agent);
    }

    [Fact(DisplayName = "I22: the coverage ledger keeps its states under pseudonymous providers")]
    public void CoverageLedgerKeepsStates()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();
        RedactedSessionPackage.Create(source.Store, package.Path, Committed);
        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));

        CoverageLedgerV1 before = SessionSegments.CoverageLedger(source.Store.Root, source.Store.Current!)!;
        CoverageLedgerV1 after = SessionSegments.CoverageLedger(shared.Root, shared.Current!)!;
        Assert.Equal(SessionCoverage.ByMechanism(before).Select(entry => (entry.Mechanism, entry.State, entry.Reason)),
            SessionCoverage.ByMechanism(after).Select(entry => (entry.Mechanism, entry.State, entry.Reason)));
        CoverageEpochV1 epoch = Assert.Single(after.Epochs);
        Assert.Contains(epoch.Collected, collected => collected.ProviderId == NetworkProvider
            && collected.ProviderName == "Microsoft-Windows-Kernel-Network");
        CoverageCollectedV1 pseudonymous = Assert.Single(epoch.Collected, collected => collected.Mechanism == Mechanism.NamedPipe);
        Assert.NotEqual(PrivateProvider, pseudonymous.ProviderId);
        Assert.Equal(RedactedSessionPseudonyms.PseudonymousProviderName(pseudonymous.ProviderId), pseudonymous.ProviderName);
        Assert.DoesNotContain(epoch.Deliveries, delivery => delivery.ProviderId == UnrequestedProvider);
        Assert.Equal(before.Epochs[0].Losses.Select(loss => (loss.Layer, loss.Lost)),
            epoch.Losses.Select(loss => (loss.Layer, loss.Lost)));
        long shift = 10_000_000 - SourceEpoch;
        Assert.Equal(before.Epochs[0].FirstDeliveredNativeTicks + shift, epoch.FirstDeliveredNativeTicks);
        Assert.Equal(before.Epochs[0].LastDeliveredNativeTicks + shift, epoch.LastDeliveredNativeTicks);
    }

    [Fact(DisplayName = "I22: a companion the package reproduces unchanged is not mistaken for a leak")]
    public void AnUnchangedLedgerIsNotALeak()
    {
        // A zero-epoch clock and only public providers leave the rewritten ledger byte-identical to the source's.
        using var source = new TemporarySession();
        Publish(source.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 2_000 },
        ], coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs =
            [
                new CoverageEpochV1
                {
                    Epoch = 1,
                    Acquisition = CoverageAcquisition.EtlImport,
                    FirstDeliveredNativeTicks = 10,
                    LastDeliveredNativeTicks = 20,
                    Collected =
                    [
                        new CoverageCollectedV1
                        {
                            ProviderId = NetworkProvider, ProviderName = "Microsoft-Windows-Kernel-Network",
                            EventId = 10, Version = 0, Mechanism = Mechanism.Tcp,
                        },
                    ],
                    Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 }],
                    Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 }],
                },
            ],
        });
        using var package = new PackageDirectory();
        RedactedSessionPackage.Create(source.Store, package.Path, Committed);

        SessionStore shared = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path));
        Assert.Equal(
            source.Store.Current!.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger).Digest,
            shared.Current!.Dependencies.Single(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger).Digest);
    }

    [Fact(DisplayName = "I22: two packages of one session share no pseudonym")]
    public void PseudonymsAreFreshPerPackage()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var first = new PackageDirectory();
        using var second = new PackageDirectory();
        RedactedSessionPackage.Create(source.Store, first.Path, Committed);
        RedactedSessionPackage.Create(source.Store, second.Path, Committed);

        ObservationRowV1[] left = [.. Segments(SessionStore.OpenExisting(LocalOwnedDirectory.Open(first.Path))).SelectMany(Rows)];
        ObservationRowV1[] right = [.. Segments(SessionStore.OpenExisting(LocalOwnedDirectory.Open(second.Path))).SelectMany(Rows)];
        Assert.Empty(left.Select(row => row.ResourceName).OfType<string>()
            .Intersect(right.Select(row => row.ResourceName).OfType<string>()));
        Assert.Empty(left.Select(row => row.OwnerProcessId).OfType<int>().Where(pid => pid != 4)
            .Intersect(right.Select(row => row.OwnerProcessId).OfType<int>()));
        Assert.Empty(left.Select(row => row.ActivityId).OfType<Guid>().Intersect(right.Select(row => row.ActivityId).OfType<Guid>()));
    }

    [Fact(DisplayName = "I22: a package refuses an overlapping or existing destination, and a package of a package")]
    public void RefusesUnsafeRequests()
    {
        using var source = new TemporarySession();
        Publish(source.Store, [Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 1, 10, 1)]);
        Assert.Throws<ArgumentException>(() => RedactedSessionPackage.Create(source.Store, source.Path, Committed));
        Assert.Throws<ArgumentException>(() => RedactedSessionPackage.Create(source.Store,
            Path.Combine(source.Path, "shared"), Committed));
        Assert.Throws<ArgumentException>(() => RedactedSessionPackage.Create(source.Store,
            Path.GetDirectoryName(source.Path)!, Committed));
        using var existing = new PackageDirectory();
        Directory.CreateDirectory(existing.Path);
        Assert.Throws<IOException>(() => RedactedSessionPackage.Create(source.Store, existing.Path, Committed));

        using var package = new PackageDirectory();
        RedactedSessionPackage.Create(source.Store, package.Path, Committed);
        using var again = new PackageDirectory();
        InvalidOperationException twice = Assert.Throws<InvalidOperationException>(() => RedactedSessionPackage.Create(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(package.Path)), again.Path, Committed));
        Assert.Contains("already a redacted package", twice.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(again.Path));
    }

    [Fact(DisplayName = "I22: a session with no rows has nothing to package")]
    public void RefusesAnEvidenceOnlySession()
    {
        using var source = new TemporarySession();
        using (DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(source.Store, new SegmentIdentityV1
        {
            CaptureId = Capture,
            ClockId = TestClock.Id,
            TimestampEncoding = TimestampEncoding.Qpc,
            Derivation = NormalizerContractVersion.V1,
        }, TestClock, Committed))
        {
            builder.Complete(Committed);
        }

        using var package = new PackageDirectory();
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            RedactedSessionPackage.Create(source.Store, package.Path, Committed));
        Assert.Contains("no normalized rows", refused.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I22: a cancelled package leaves neither the package nor its stage behind")]
    public void CancellationLeavesNothing()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        using var package = new PackageDirectory();
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(update =>
        {
            if (update.Stage == RedactedPackageStage.Writing) cancellation.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => RedactedSessionPackage.Create(source.Store, package.Path,
            Committed, progress, cancellation.Token));
        Assert.False(Directory.Exists(package.Path));

        // Only this package's own stage counts: the folder is shared, and an interrupted run of any test can leave one.
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(package.Path)!,
            Path.GetFileName(package.Path) + ".partial-*"));
    }

    [Fact(DisplayName = "I22: a preview measures the package without writing it")]
    public void PreviewWritesNothing()
    {
        using var source = new TemporarySession();
        PublishRichSource(source.Store);
        string[] before = [.. Directory.EnumerateFiles(source.Path).Order()];
        RedactedSessionPackagePreview preview = RedactedSessionPackage.Preview(source.Store);
        Assert.Equal(before, Directory.EnumerateFiles(source.Path).Order());
        Assert.Equal(source.Store.Current!.SessionId, preview.SourceSessionId);
        Assert.Equal(Segments(source.Store).Sum(segment => segment.RowCount), preview.Rows);
        Assert.Equal(1, preview.SourceJournals);
        Assert.True(preview.SourceJournalBytes > 0);
        Assert.True(preview.CoverageLedger);
        Assert.Equal(3, preview.SourceFieldRowsRedacted);

        // The bound is checked from segment headers alone, which must declare what the segments hold.
        SessionManifestV1 manifest = source.Store.Current!;
        Assert.All(SessionSegments.Names(manifest), name => Assert.Equal(
            SessionSegments.Open(source.Store.Root, manifest, name).RowCount,
            SessionSegments.DeclaredRowCount(source.Store.Root, manifest, name)));
    }

    [Fact(DisplayName = "I22: a redaction policy that claims original sources, or names an unknown member, is refused")]
    public void PolicyReaderIsStrict()
    {
        RedactedSessionCounts counts = new()
        {
            Rows = 1, SourceFieldRows = 0, SourceFieldRowsRedacted = 0, CoverageLedger = false, Names = 0,
            ProcessesAndThreads = 0, Addresses = 0, Ports = 0, Identifiers = 0, Providers = 0, PublicProviders = 0,
            Schemas = 1, StartSequences = 0, KernelObjects = 0,
        };
        byte[] valid = RedactedSessionPackage.PolicyFor(Committed, counts).Encode();
        Assert.Equal(counts, RedactedSessionPolicyV1.Decode(valid).Counts);

        string text = Encoding.UTF8.GetString(valid);
        Assert.Throws<InvalidDataException>(() => RedactedSessionPolicyV1.Decode(Encoding.UTF8.GetBytes(
            text.Replace("\"originalSourcesIncluded\": false", "\"originalSourcesIncluded\": true", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => RedactedSessionPolicyV1.Decode(Encoding.UTF8.GetBytes(
            text.Replace("\"rederivable\": false", "\"rederivable\": false, \"sourceSession\": \"x\"", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => RedactedSessionPolicyV1.Decode(Encoding.UTF8.GetBytes(
            text.Replace(RedactedSessionPackage.Policy, "some-other-policy", StringComparison.Ordinal))));
    }

    [Fact(DisplayName = "I22: the leak scanner finds a needle across a chunk boundary and ignores generated-looking text")]
    public void LeakScannerFindsOnlyLeaks()
    {
        var scanner = new RedactedPackageLeakScanner();
        scanner.AddText("Contoso Secret", "a name");
        scanner.AddText("resource-a", "a pseudonym-shaped name");
        scanner.AddIdentifier(SecretActivity, "an identifier");
        Assert.False(RedactedPackageLeakScanner.IsDistinctive("resource-a"u8));
        Assert.True(RedactedPackageLeakScanner.IsDistinctive("Resource-a"u8));

        byte[] data = new byte[(1 << 20) + 64];
        Encoding.UTF8.GetBytes("resource-a1b2c3d4").CopyTo(data, 100);
        Assert.Null(scanner.Find(new MemoryStream(data), CancellationToken.None));

        // One needle crosses the first chunk's end; the other starts in the bytes carried into the second read.
        foreach (int at in new[] { (1 << 20) - 7, (1 << 20) + 20 })
        {
            byte[] straddling = (byte[])data.Clone();
            Encoding.Unicode.GetBytes("Contoso Secret").CopyTo(straddling, at);
            Assert.Equal("a name", scanner.Find(new MemoryStream(straddling), CancellationToken.None));
        }

        byte[] identifier = (byte[])data.Clone();
        SecretActivity.ToByteArray(bigEndian: true).CopyTo(identifier, 5000);
        Assert.Equal("an identifier", scanner.Find(new MemoryStream(identifier), CancellationToken.None));
    }

    // -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A session with every kind of value the package replaces: an uptime-revealing epoch and a reading before it, an
    /// image path and its case variant, an exit-only instance, a reused PID, loopback and LAN pairs, an unspecified
    /// endpoint, a third-party provider, source fields including wall-clock times and text, retained bodies, row
    /// markers, and a coverage ledger with loss.
    /// </summary>
    private static void PublishRichSource(SessionStore store)
    {
        ObservationRowV1 system = At(Lifecycle(0, ObservationKind.Inventory, 4, 1) with { ResourceName = "System" }, 0);
        ObservationRowV1 agent = At(Lifecycle(0, ObservationKind.Create, 1200, 2) with { ResourceName = SecretPath }, 10);
        ObservationRowV1 child = At(Lifecycle(0, ObservationKind.Create, 1300, 3) with { ResourceName = SecretPathOtherCase }, 20);
        ObservationRowV1 childExit = At(Lifecycle(0, ObservationKind.Exit, 1300, 4, exitCode: 0) with
        {
            ResourceName = "agent service.exe",
        }, 30);
        ObservationRowV1 exitOnly = At(Lifecycle(0, ObservationKind.Exit, 1400, 5, exitCode: 3) with
        {
            ResourceName = "Agent Service.exe",
        }, 35);
        ObservationRowV1 worker = At(Lifecycle(0, ObservationKind.Create, 1500, 6) with { ResourceName = @"C:\Tools\worker.exe" }, 40);
        ObservationRowV1 workerExit = At(Lifecycle(0, ObservationKind.Exit, 1500, 7, exitCode: 0) with { ResourceName = "worker.exe" }, 50);
        ObservationRowV1 reused = At(Lifecycle(0, ObservationKind.Create, 1500, 8) with { ResourceName = @"C:\Tools\other.exe" }, 60);

        ObservationRowV1 send = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 4096, 1200, 20)
            .Between("127.0.0.1:50000", "127.0.0.1:8443"), 25);
        ObservationRowV1 receive = At(Transfer(0, ObservationKind.Receive, AccountingSide.ReceiveSide, 4096, 1300, 21)
            .Between("127.0.0.1:8443", "127.0.0.1:50000"), 26);
        ObservationRowV1 remote = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 512, 1200, 22)
            .Between("10.1.2.3:51000", "203.0.113.9:443"), 27);
        ObservationRowV1 incomplete = At(Transfer(0, ObservationKind.Connect, AccountingSide.SendSide, null, 1200, 23)
            .Between("10.1.2.3:0", "203.0.113.9:443") with { ByteAvailability = FieldAvailability.NotExposed }, 28);
        ObservationRowV1 lanSend = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 700, 1500, 24)
            .Between("192.168.1.10:52000", "192.168.1.20:9000"), 45);
        ObservationRowV1 lanReceive = At(Transfer(0, ObservationKind.Receive, AccountingSide.ReceiveSide, 700, 1200, 25)
            .Between("192.168.1.20:9000", "192.168.1.10:52000"), 46);
        ObservationRowV1 early = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 1, 1200, 26)
            .Between("127.0.0.1:50001", "127.0.0.1:8443"), PreEpochOffset);
        ObservationRowV1 quarantined = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 9, 1200, 27)
            .Between("127.0.0.1:50002", "127.0.0.1:8443"), 55) with { SessionRelativeTicks = null };

        ObservationRowV1 pipe = At(Transfer(0, ObservationKind.Send, AccountingSide.SendSide, 64, 1200, 30) with
        {
            ProviderId = PrivateProvider,
            EventId = 5,
            SchemaFingerprint = "sha256:" + new string('c', 64),
            Mechanism = Mechanism.NamedPipe,
            ByteDomain = ByteDomain.RequestedIo,
            ResourceName = SecretPipe,
            Markers = SegmentRowMarkers.ResourceNameTruncated | SegmentRowMarkers.ExtendedItemsOmitted,
        }, 33);
        ObservationRowV1 rpc = At(Transfer(0, ObservationKind.RequestStart, AccountingSide.SendSide, null, 1300, 31) with
        {
            ProviderId = PrivateProvider,
            EventId = 6,
            SchemaFingerprint = "sha256:" + new string('c', 64),
            Mechanism = Mechanism.Rpc,
            Layer = ObservationLayer.Application,
            ByteAvailability = FieldAvailability.NotApplicable,
            ByteDomain = null,
            AccountingSide = null,
            MeasurementUnit = null,
            MeasurementQuality = QualityLevel.UnknownQuality,
            SourceIdentifier = SecretInterface,
            ActivityId = SecretActivity,
            RelatedActivityId = SecretRelated,
            StatusCode = 5,
            StatusAvailability = FieldAvailability.Present,
        }, 24);

        ObservationRowV1[] rows = [system, agent, child, childExit, exitOnly, worker, workerExit, reused, send, receive,
            remote, incomplete, lanSend, lanReceive, early, quarantined, pipe, rpc];
        SourceFieldRowV1[] fields =
        [
            Field(system, SourceField.ProcessStartSequence, 1),
            Field(agent, SourceField.ProcessStartSequence, 5001),
            Field(agent, SourceField.ProcessCreateTime, SecretFileTime),
            Field(agent, SourceField.ParentProcessId, 4),
            Field(agent, SourceField.ParentStartSequence, 1),
            Field(agent, SourceField.ProcessSessionId, 0),
            Field(child, SourceField.ProcessStartSequence, 5002),
            Field(child, SourceField.ProcessCreateTime, SecretFileTime + 1),
            Field(child, SourceField.ParentProcessId, 1200),
            Field(child, SourceField.ParentStartSequence, 5001),
            Field(child, SourceField.ProcessSessionId, 1),
            Field(pipe, SourceField.FileObject, unchecked((long)0xFFFF_8A0C_1234_5670)),
            Field(pipe, SourceField.IssuingThreadId, 1201),
            new SourceFieldRowV1
            {
                RawStreamId = pipe.RawStreamId,
                RawSourceEpoch = pipe.RawSourceEpoch,
                RawRecordOrdinal = pipe.RawRecordOrdinal,
                FactKey = pipe.FactKey,
                NativeTicks = pipe.NativeTicks,
                Field = SourceField.FileKey,
                Text = @"C:\Users\Contoso Secret\notes.txt",
                Availability = FieldAvailability.Present,
            },
            Field(rpc, SourceField.RpcProcedureNumber, 7),
            Field(rpc, SourceField.RpcProtocolSequence, 1),
        ];
        Publish(store, rows, rowsPerSegment: 5, clock: SourceClock, fields: fields, coverage: Ledger(),
            bodyForRow: _ => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = SecretBody.Length,
                Bytes = EnvelopeBuffer.CopyOf(Encoding.UTF8.GetBytes(SecretBody)),
            });
    }

    /// <summary>A row at a distance from the source epoch, in 100-ns ticks, with the session time that distance is.</summary>
    private static ObservationRowV1 At(ObservationRowV1 row, long offset) => row with
    {
        NativeTicks = SourceEpoch + offset,
        SessionRelativeTicks = offset * 100,
    };

    private static CoverageLedgerV1 Ledger() => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.LiveCapture,
                FirstDeliveredNativeTicks = SourceEpoch + PreEpochOffset,
                LastDeliveredNativeTicks = SourceEpoch + 60,
                Collected =
                [
                    new CoverageCollectedV1
                    {
                        ProviderId = NetworkProvider, ProviderName = "Microsoft-Windows-Kernel-Network",
                        EventId = 10, Version = 0, Mechanism = Mechanism.Tcp,
                    },
                    new CoverageCollectedV1
                    {
                        ProviderId = ProcessProvider, ProviderName = "Microsoft-Windows-Kernel-Process",
                        EventId = 1, Version = 4, Mechanism = Mechanism.ProcessLifecycle,
                    },
                    new CoverageCollectedV1
                    {
                        ProviderId = PrivateProvider, ProviderName = "Contoso-Secret-Provider",
                        EventId = 5, Version = 0, Mechanism = Mechanism.NamedPipe,
                    },
                ],
                Deliveries =
                [
                    new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 9, Admitted = 9, Omitted = 0 },
                    new CoverageDeliveryV1 { ProviderId = ProcessProvider, EventId = 1, Version = 4, Delivered = 4, Admitted = 4, Omitted = 0 },
                    new CoverageDeliveryV1 { ProviderId = PrivateProvider, EventId = 5, Version = 0, Delivered = 1, Admitted = 1, Omitted = 0 },
                    new CoverageDeliveryV1
                    {
                        ProviderId = UnrequestedProvider, Delivered = 12, Admitted = 0, Omitted = 12,
                        Omission = OmissionReason.UnrequestedProvider,
                    },
                ],
                Losses =
                [
                    new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 3 },
                    new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
                ],
            },
        ],
    };

    private static IReadOnlyList<SegmentReaderV1> FieldSegments(SessionStore store) =>
        [.. SessionSegments.FieldNames(store.Current!).Select(name => SessionSegments.Open(store.Root, store.Current!, name))];

    private static IEnumerable<ObservationRowV1> Rows(SegmentReaderV1 segment) =>
        Enumerable.Range(0, segment.RowCount).Select(segment.Row);

    private static IEnumerable<SourceFieldRowV1> FieldRows(SegmentReaderV1 segment) =>
        Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow);

    /// <summary>
    /// How the nodes partition by label, as sorted group sizes. Labels are compared as executables are, ignoring case:
    /// a path's case variants are one executable, and a package gives them one pseudonym.
    /// </summary>
    private static int[] LabelPartition(SessionOverviewBundle overview) =>
        [.. overview.Nodes.GroupBy(node => node.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.Count()).Order()];

    /// <summary>What an instance index concludes, without the identities a package replaces.</summary>
    private static string[] Shape(ProcessInstanceIndex index) =>
    [
        .. index.Instances.Select(instance => string.Join('|',
            instance.Witness, instance.Gaps, instance.LifecycleEpoch, instance.StartKey is not null,
            instance.CreatedNativeTicks is not null, instance.ExitedNativeTicks is not null, instance.ExitCode,
            instance.Parent is not null, instance.ParentBinding, instance.SessionId,
            instance.ImagePath is not null, instance.ImageName is not null))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Each row's owner, peer and channel outcome, keyed by what identifies it in both sessions.</summary>
    private static string[] Outcomes(IReadOnlyList<SegmentReaderV1> segments, ProcessInstanceIndex processes)
    {
        TransportRelationIndex relations = TransportRelationIndex.Derive(segments, processes);
        return
        [
            .. segments.SelectMany(segment =>
            {
                ProcessBinding[] owners = processes.OwnersOf(segment);
                ProcessBinding[] peers = relations.PeersOf(segment);
                ChannelBinding[] channels = relations.ChannelsOf(segment);
                return Enumerable.Range(0, segment.RowCount).Select(index =>
                {
                    ObservationRowV1 row = segment.Row(index);
                    return $"{row.SessionRelativeTicks}|{row.Mechanism}|{row.Kind}|{row.ByteValue}"
                        + $"|owner {owners[index].Strength}/{owners[index].Reason}"
                        + $"|peer {peers[index].Strength}/{peers[index].Reason}"
                        + $"|channel {channels[index].IsKnown}/{channels[index].Reason}";
                });
            }).Order(StringComparer.Ordinal),
        ];
    }

    private sealed class SynchronousProgress(Action<RedactedPackageProgress> report) : IProgress<RedactedPackageProgress>
    {
        public void Report(RedactedPackageProgress value) => report(value);
    }

    /// <summary>A package destination that does not exist yet, removed after the test.</summary>
    private sealed class PackageDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InterCat.RedactedPackage.Tests",
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
