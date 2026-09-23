using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

public sealed class CaptureFinalizationTests
{
    private static readonly Guid Capture = Guid.Parse("7c111111-2222-4333-8444-555555555555");

    [Fact(DisplayName = "R16: a capture finalization marker round-trips its stop milestones")]
    public void RoundTrip()
    {
        CaptureFinalizationV1 original = Example() with { ProvidersStopped = true, CallbacksDrained = false };

        CaptureFinalizationV1 decoded = CaptureFinalizationV1.Decode(original.Encode());

        Assert.Equal(original, decoded);
        Assert.Contains("\"contract\":\"capture-finalization-v1\"", Encoding.UTF8.GetString(original.Encode()), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: a finalization marker refuses unknown members, other contracts and empty identity")]
    public void MalformedMarkersAreRefused()
    {
        string json = Encoding.UTF8.GetString(Example().Encode());

        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode(
            Encoding.UTF8.GetBytes(json.Replace("\"contract\":", "\"surprise\":1,\"contract\":", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode(
            Encoding.UTF8.GetBytes(json.Replace("capture-finalization-v1", "capture-finalization-v2", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode(
            Encoding.UTF8.GetBytes(json.Replace(Capture.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode(Encoding.UTF8.GetBytes("{\"contract\":")));
        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode([]));
        Assert.Throws<InvalidDataException>(() => CaptureFinalizationV1.Decode(new byte[CaptureFinalizationV1.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => (Example() with { FinalizedUtc = default }).Encode());
        Assert.Throws<InvalidDataException>(() => (Example() with { Contract = "coverage-v1" }).Encode());
    }

    [Fact(DisplayName = "R16: a published marker is read back, protected from retention and carried by re-derivation")]
    public void PublishedMarkerIsReadBackAndProtected()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "finalization-test");
        string name = DerivedGenerationBuilder.CaptureFinalizationFileName(1);
        CommittedBoundary boundary = CommitWithMarkers(store, [(name, Example().Encode())]);

        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(Example(), CaptureFinalizationV1.Read(reopened.Root, reopened.Current!));
        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            reopened.ReleaseDependencies([name], "try to release finalization", DateTimeOffset.UtcNow));
        Assert.Contains("retention cannot release it", refusal.Message, StringComparison.Ordinal);

        _ = reopened.CommitReplacingDerived([], 1, boundary, DateTimeOffset.UtcNow);
        SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(2, replaced.Current!.Generation);
        Assert.Equal(Example(), CaptureFinalizationV1.Read(replaced.Root, replaced.Current));
    }

    [Fact(DisplayName = "R16: a generation without a marker is not final, and two markers are ambiguous")]
    public void AbsentAndDuplicateMarkers()
    {
        using var none = new TemporaryStoreDirectory();
        SessionStore intermediate = SessionStore.Open(LocalOwnedDirectory.Open(none.Path), Guid.NewGuid(), "finalization-test");
        _ = CommitWithMarkers(intermediate, []);
        Assert.Null(CaptureFinalizationV1.Read(intermediate.Root, intermediate.Current!));

        using var two = new TemporaryStoreDirectory();
        SessionStore ambiguous = SessionStore.Open(LocalOwnedDirectory.Open(two.Path), Guid.NewGuid(), "finalization-test");
        _ = CommitWithMarkers(ambiguous,
        [
            (DerivedGenerationBuilder.CaptureFinalizationFileName(1), Example().Encode()),
            ("capture-finalization-extra.json", Example().Encode()),
        ]);
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            CaptureFinalizationV1.Read(ambiguous.Root, ambiguous.Current!));
        Assert.Contains("exactly one", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: a marker whose bytes changed after publication is refused")]
    public void TamperedMarkerIsRefused()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "finalization-test");
        string name = DerivedGenerationBuilder.CaptureFinalizationFileName(1);
        _ = CommitWithMarkers(store, [(name, Example().Encode())]);

        // Same length, different bytes: only the digest can notice.
        byte[] altered = (Example() with { CallbacksDrained = true, ProvidersStopped = false }).Encode();
        Assert.Equal(Example().Encode().Length, altered.Length);
        File.WriteAllBytes(Path.Combine(directory.Path, name), altered);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() =>
            CaptureFinalizationV1.Read(store.Root, store.Current!));
        Assert.Contains("changed after generation", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: the publication metadata bound covers a real manifest and pointers with long names")]
    public void PublicationMetadataBoundCoversRealMetadata()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "finalization-test");
        (string, byte[])[] markers =
        [
            .. Enumerable.Range(0, 40).Select(index => (
                $"capture-finalization-{index:D4}-".PadRight(OwnedFileName.MaximumLength - 5, 'x') + ".json",
                Example().Encode())),
        ];
        _ = CommitWithMarkers(store, markers);
        SessionManifestV1 manifest = store.Current!;
        HashSet<string> dependencies = [.. manifest.Dependencies.Select(dependency => dependency.Name)];

        long metadata = Directory.EnumerateFiles(directory.Path)
            .Where(file => !dependencies.Contains(Path.GetFileName(file)))
            .Sum(file => new FileInfo(file).Length);

        Assert.InRange(metadata, 1, SessionStore.PublicationMetadataBound(manifest.Dependencies.Count));
    }

    private static CaptureFinalizationV1 Example() => new()
    {
        Contract = CaptureFinalizationV1.ContractName,
        CaptureId = Capture,
        FinalizedUtc = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
        ProvidersStopped = true,
        CallbacksDrained = false,
    };

    private static CommittedBoundary CommitWithMarkers(SessionStore store, (string Name, byte[] Bytes)[] markers)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string markerName, byte[] bytes) in markers)
            {
                StoreStagingFile marker = store.Stage(markerName, StoreDependencyKind.CaptureFinalization);
                staged.Add(marker);
                marker.Content.Write(bytes);
                _ = marker.Complete();
            }

            byte[] journalBytes = Encoding.UTF8.GetBytes("journal");
            const string journalName = "journal-0000000001.icatj";
            StoreStagingFile journal = store.Stage(journalName, StoreDependencyKind.Journal);
            staged.Add(journal);
            journal.Content.Write(journalBytes);
            _ = journal.Complete();
            StoreStagingFile plan = store.Stage("normalizer-plan-0000000001.json", StoreDependencyKind.DerivationPlan);
            staged.Add(plan);
            plan.Content.Write(Encoding.UTF8.GetBytes("retained plan"));
            _ = plan.Complete();
            var boundary = new CommittedBoundary(
                journalName, journalBytes.Length, 1,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(journalBytes)));
            _ = store.Commit([.. staged], boundary, DateTimeOffset.UtcNow);
            return boundary;
        }
        finally
        {
            foreach (StoreStagingFile file in staged)
            {
                file.Dispose();
            }
        }
    }

    private sealed class TemporaryStoreDirectory : IDisposable
    {
        public TemporaryStoreDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "InterCat.Storage.Tests.Finalization", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
