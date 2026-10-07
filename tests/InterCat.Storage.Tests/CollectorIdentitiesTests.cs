using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// The processes that collected a live capture (`contracts/collector-identities-v1.md`): its broker and the client that
/// asked for it, or the recorder, by PID and creation time, kept with the capture like its clock calibration (§19.5).
/// </summary>
public sealed class CollectorIdentitiesTests
{
    private static readonly Guid Capture = Guid.Parse("7c444444-2222-4333-8444-555555555555");
    private static readonly DateTimeOffset BrokerCreated = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(3);

    [Fact(DisplayName = "§19.5: collector identities round-trip each process's role, PID and creation time, and refuse what they cannot hold")]
    public void RoundTripAndRefusals()
    {
        CollectorIdentitiesV1 original = Example();
        CollectorIdentitiesV1 decoded = CollectorIdentitiesV1.Decode(original.Encode());
        Assert.Equal(original.CaptureId, decoded.CaptureId);
        Assert.Equal(original.Processes, decoded.Processes);

        // A creation time that could not be read is written as nothing, never as a guessed one; roles are words.
        string json = Encoding.UTF8.GetString(original.Encode());
        Assert.Contains("\"role\":\"Broker\"", json, StringComparison.Ordinal);
        Assert.Null(decoded.Processes[1].CreatedUtc);
        Assert.Equal(1, json.Split("createdUtc").Length - 1);

        foreach (string refused in new[]
        {
            json.Replace("\"contract\":", "\"surprise\":1,\"contract\":", StringComparison.Ordinal),
            json.Replace("collector-identities-v1", "collector-identities-v2", StringComparison.Ordinal),
            json.Replace(Capture.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            json.Replace("\"Broker\"", "\"Watcher\"", StringComparison.Ordinal),
            json.Replace("\"Broker\"", "1", StringComparison.Ordinal),
            "{\"contract\":",
        })
        {
            Assert.Throws<InvalidDataException>(() => CollectorIdentitiesV1.Decode(Encoding.UTF8.GetBytes(refused)));
        }

        Assert.Throws<InvalidDataException>(() => CollectorIdentitiesV1.Decode([]));
        Assert.Throws<InvalidDataException>(() => CollectorIdentitiesV1.Decode(new byte[CollectorIdentitiesV1.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => (original with { Processes = [] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with
        {
            Processes = [.. Enumerable.Range(1, 9).Select(pid => new CollectorProcessV1 { Role = CollectorRole.Client, ProcessId = pid })],
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Processes = [original.Processes[0] with { ProcessId = 0 }] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Processes = [original.Processes[0] with { Role = (CollectorRole)9 }] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Processes = [original.Processes[0], original.Processes[0]] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { CaptureId = Guid.Empty }).Encode());

        // One PID in two roles is two collectors, as a process that asked itself to record would be.
        _ = (original with { Processes = [original.Processes[0], original.Processes[0] with { Role = CollectorRole.Client }] }).Encode();
    }

    [Fact(DisplayName = "§19.5: published collector identities are read back, carried by re-derivation and never released, and a generation without them states none")]
    public void PublishedCollectorsAreCarriedAndKept()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "collector-test");
        string name = DerivedGenerationBuilder.CollectorIdentitiesFileName(1);
        Assert.Equal("collector-identities-0000000001.json", name);
        CommittedBoundary boundary = CommitWith(store, [(name, Example().Encode())]);

        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(Example().Processes, CollectorIdentitiesV1.Read(reopened.Root, reopened.Current!)!.Processes);
        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            reopened.ReleaseDependencies([name], "try to release the collectors", DateTimeOffset.UtcNow));
        Assert.Contains("retention cannot release them", refusal.Message, StringComparison.Ordinal);

        _ = reopened.CommitReplacingDerived([], 1, boundary, DateTimeOffset.UtcNow);
        SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(2, replaced.Current!.Generation);
        Assert.Equal(Example().Processes, CollectorIdentitiesV1.Read(replaced.Root, replaced.Current)!.Processes);

        // A generation that names none says so, and one that names two is refused.
        using var none = new TemporaryStoreDirectory();
        SessionStore plain = SessionStore.Open(LocalOwnedDirectory.Open(none.Path), Guid.NewGuid(), "collector-test");
        _ = CommitWith(plain, []);
        Assert.Null(CollectorIdentitiesV1.Read(plain.Root, plain.Current!));
        using var two = new TemporaryStoreDirectory();
        SessionStore doubled = SessionStore.Open(LocalOwnedDirectory.Open(two.Path), Guid.NewGuid(), "collector-test");
        _ = CommitWith(doubled, [(name, Example().Encode()), ("collector-identities-extra.json", Example().Encode())]);
        Assert.Contains("one capture has one", Assert.Throws<InvalidDataException>(() =>
            CollectorIdentitiesV1.Read(doubled.Root, doubled.Current!)).Message, StringComparison.Ordinal);
    }

    private static CollectorIdentitiesV1 Example() => new()
    {
        Contract = CollectorIdentitiesV1.ContractName,
        CaptureId = Capture,
        Processes =
        [
            new() { Role = CollectorRole.Broker, ProcessId = 4_120, CreatedUtc = BrokerCreated },
            new() { Role = CollectorRole.Client, ProcessId = 7_008 },
        ],
    };

    private static CommittedBoundary CommitWith(SessionStore store, (string Name, byte[] Bytes)[] files)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string fileName, byte[] bytes) in files)
            {
                StoreStagingFile file = store.Stage(fileName, StoreDependencyKind.CollectorIdentities);
                staged.Add(file);
                file.Content.Write(bytes);
                _ = file.Complete();
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
                System.IO.Path.GetTempPath(), "InterCat.Storage.Tests.Collectors", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
