using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §11.3's third preset, the original evidence package: an exact, verified copy of a session's current generation that
/// reopens as the same session, and holds nothing else.
/// </summary>
public sealed class OriginalEvidencePackageTests
{
    [Fact(DisplayName = "I15: an original package reopens as the same session and generation, byte for byte, holding nothing else")]
    public void APackageIsTheGenerationByteForByte()
    {
        using var source = new TemporarySession();
        _ = Publish(source.Store, [Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 10, 1)]);
        DerivedGenerationResult second = Publish(source.Store, [Transfer(5, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 20, 2)]);
        File.WriteAllText(Path.Combine(source.Path, "left-behind.txt"), "not part of any generation");
        using var package = new PackageDirectory();

        OriginalEvidencePackageResult result = OriginalEvidencePackage.Create(source.Store, package.Path);

        // The very generation, at the very digest, with every file it names and nothing it does not name: no earlier
        // generation to fall back to, no superseded manifest, no stray file.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(result.Directory));
        Assert.Equal(package.Path, result.Directory);
        Assert.Equal(second.Manifest.Digest, reopened.Current!.Digest);
        Assert.Equal((second.Manifest.Generation, second.Manifest.SessionId), (reopened.Current.Generation, reopened.SessionId));
        Assert.False(reopened.Recovery.RolledBackToLastKnownGood);
        Assert.Empty(reopened.Recovery.OrphanFiles);
        foreach (StoreDependency dependency in second.Manifest.Dependencies)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(source.Path, dependency.Name)),
                File.ReadAllBytes(Path.Combine(package.Path, dependency.Name)));
        }

        string[] expected =
        [
            .. second.Manifest.Dependencies.Select(dependency => dependency.Name)
                .Append(SessionManifestV1.FileNameFor(second.Manifest.Generation))
                .Append(SessionPointerV1.FileName)
                .Append(SessionStore.EvidenceLeaseLockFileName)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
        string[] held =
            [.. Directory.EnumerateFiles(package.Path).Select(file => Path.GetFileName(file)).Order(StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(expected, held);
        Assert.Empty(Directory.EnumerateDirectories(package.Path));

        // A recipient reads it as the session it is, including a lease, which takes the guard the package carries.
        using (EvidenceLease lease = reopened.AcquireLease())
        {
            Assert.Equal(second.Manifest.Generation, lease.Manifest.Generation);
        }

        Assert.Equal(2, SessionOverviewProjector.Project(reopened).ObservationRows);
        Assert.Equal((second.Manifest.Dependencies.Count + 1, result.Source.Bytes), (result.FilesVerified, result.BytesVerified));
    }

    [Fact(DisplayName = "I15: an original package refuses a file that changed after its generation recorded it, and leaves nothing")]
    public void APackageRefusesAChangedFile()
    {
        using var source = new TemporarySession();
        DerivedGenerationResult published = Publish(source.Store, [Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 10, 1)]);
        string segment = Path.Combine(source.Path, published.Segments[0].Name);
        DateTime measured = File.GetLastWriteTimeUtc(segment);
        byte[] bytes = File.ReadAllBytes(segment);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(segment, bytes);
        File.SetLastWriteTimeUtc(segment, measured);
        using var package = new PackageDirectory();

        // The source's store hashed the file before the change, which kept its length and time, so the lease trusts its
        // measurement and only the copy's own hashing can find it. It is found as the file is copied, before the rest
        // is; reopening the package would have found it too, later.
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() =>
            OriginalEvidencePackage.Create(source.Store, package.Path));

        Assert.Contains($"'{published.Segments[0].Name}' computes", refused.Message, StringComparison.Ordinal);
        Assert.Contains("so it is not copied into a package", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(package.Path));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(package.Path)!, Path.GetFileName(package.Path) + ".partial-*"));
    }

    [Fact(DisplayName = "§11.3: an original package is written only to a new directory outside its source, and a cancelled one leaves nothing")]
    public void APackageRefusesUnsafeDestinationsAndCancelsCleanly()
    {
        using var source = new TemporarySession();
        _ = Publish(source.Store, [Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 10, 1)]);
        Assert.Throws<ArgumentException>(() => OriginalEvidencePackage.Create(source.Store, source.Path));
        Assert.Throws<ArgumentException>(() => OriginalEvidencePackage.Create(source.Store, Path.Combine(source.Path, "inside")));
        Assert.Throws<ArgumentException>(() => OriginalEvidencePackage.Create(source.Store, Path.GetDirectoryName(source.Path)!));
        using var existing = new PackageDirectory();
        Directory.CreateDirectory(existing.Path);
        Assert.Throws<IOException>(() => OriginalEvidencePackage.Create(source.Store, existing.Path));

        using var package = new PackageDirectory();
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(update =>
        {
            if (update.Stage == OriginalPackageStage.Copying) cancellation.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            OriginalEvidencePackage.Create(source.Store, package.Path, progress, cancellation.Token));
        Assert.False(Directory.Exists(package.Path));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(package.Path)!, Path.GetFileName(package.Path) + ".partial-*"));
    }

    [Fact(DisplayName = "§11.3: an original package's preview names every file, the rows and the host, and writes nothing")]
    public void APreviewNamesWhatThePackageHolds()
    {
        using var source = new TemporarySession();
        DerivedGenerationResult published = Publish(
            source.Store,
            [
                Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 10, 1),
                Transfer(2, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 20, 2),
            ],
            fields: [new() { RawStreamId = 1, RawSourceEpoch = 1, RawRecordOrdinal = 1, FactKey = FactKey.Create("network-transfer"),
                NativeTicks = 1, Field = SourceField.ConnectionId, Value = 7, Availability = FieldAvailability.Present }]);
        string[] before = [.. Directory.EnumerateFiles(source.Path).Order()];

        OriginalEvidencePackagePreview preview = OriginalEvidencePackage.Preview(source.Store);

        Assert.Equal(before, Directory.EnumerateFiles(source.Path).Order());
        Assert.Equal((published.Manifest.SessionId, published.Manifest.Generation), (preview.SessionId, preview.Generation));
        Assert.Equal(
            published.Manifest.Dependencies.Select(dependency => (dependency.Name, dependency.Kind, dependency.LengthBytes)),
            preview.Files.Select(file => (file.Name, file.Kind, file.LengthBytes)));
        Assert.Equal((2L, 1L), (preview.Rows, preview.SourceFieldRows));
        Assert.Single(preview.Journals);
        Assert.Equal(TestClock.HostId.Value, preview.HostId);
        Assert.False(preview.Redacted);
        Assert.Equal(OriginalEvidencePackage.Contents, OriginalEvidencePackage.ContentsFor(preview));
        Assert.Equal(OriginalEvidencePackage.Warning, OriginalEvidencePackage.WarningFor(preview));
    }

    [Fact(DisplayName = "§11.3: a redacted package's copy is that package as it is, stated as pseudonymized and never unredacted")]
    public void ARedactedPackagesCopyIsNeverCalledUnredacted()
    {
        using var source = new TemporarySession();
        Publish(source.Store,
        [
            Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 64, 10, 1) with { SessionRelativeTicks = 100 },
            Transfer(2, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 20, 2) with { SessionRelativeTicks = 200 },
        ]);
        using var redacted = new PackageDirectory();
        _ = RedactedSessionPackage.Create(source.Store, redacted.Path, DateTimeOffset.UnixEpoch);
        SessionStore package = SessionStore.OpenExisting(LocalOwnedDirectory.Open(redacted.Path));

        // Its copy holds the package's pseudonyms and synthetic records, under the redacted package's own warning.
        OriginalEvidencePackagePreview preview = OriginalEvidencePackage.Preview(package);
        Assert.True(preview.Redacted);
        Assert.Equal(OriginalEvidencePackage.RedactedContents, OriginalEvidencePackage.ContentsFor(preview));
        Assert.Equal(RedactedSessionPackage.Warning, OriginalEvidencePackage.WarningFor(preview));
        Assert.DoesNotContain("Unredacted", OriginalEvidencePackage.WarningFor(preview), StringComparison.OrdinalIgnoreCase);

        // The copy is the same package, which reopens as one.
        using var copy = new PackageDirectory();
        OriginalEvidencePackageResult result = OriginalEvidencePackage.Create(package, copy.Path);
        Assert.True(result.Source.Redacted);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(copy.Path));
        Assert.True(OriginalEvidencePackage.Preview(reopened).Redacted);
        Assert.Equal(preview.SessionId, OriginalEvidencePackage.Preview(reopened).SessionId);
    }

    private sealed class SynchronousProgress(Action<OriginalPackageProgress> report) : IProgress<OriginalPackageProgress>
    {
        public void Report(OriginalPackageProgress value) => report(value);
    }

    /// <summary>A package destination that does not exist yet, removed after the test.</summary>
    private sealed class PackageDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InterCat.OriginalPackage.Tests",
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
