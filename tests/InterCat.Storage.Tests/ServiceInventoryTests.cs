using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// The services running as a capture started or stopped, as the service control manager lists them, each with its host's
/// PID and creation time (`contracts/service-inventory-v1.md`, ADR-095): evidence kept with the capture, which no journal
/// holds, as its collector identities are.
/// </summary>
public sealed class ServiceInventoryTests
{
    private static readonly Guid Capture = Guid.Parse("7c444444-2222-4333-8444-666666666666");
    private static readonly DateTimeOffset HostCreated = new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero).AddTicks(7);
    private static readonly DateTimeOffset Read = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§18.5: a service inventory round-trips each service's name and its host's PID and creation time, its moment and its read's start and end, and refuses what it cannot hold")]
    public void RoundTripAndRefusals()
    {
        ServiceInventoryV1 original = Example(ServiceInventoryMoment.Start);
        ServiceInventoryV1 decoded = ServiceInventoryV1.Decode(original.Encode());
        Assert.Equal((original.CaptureId, original.Moment, original.ReadStartedUtc, original.ReadEndedUtc),
            (decoded.CaptureId, decoded.Moment, decoded.ReadStartedUtc, decoded.ReadEndedUtc));
        Assert.Equal(original.Services, decoded.Services);

        // A host's creation time that could not be read is written as nothing, never a guessed one; the moment is a word.
        string json = Encoding.UTF8.GetString(original.Encode());
        Assert.Contains("\"moment\":\"Start\"", json, StringComparison.Ordinal);
        Assert.Null(decoded.Services[2].HostCreatedUtc);
        Assert.Equal(2, json.Split("hostCreatedUtc").Length - 1);

        foreach (string refused in new[]
        {
            json.Replace("\"contract\":", "\"surprise\":1,\"contract\":", StringComparison.Ordinal),
            json.Replace("service-inventory-v1", "service-inventory-v2", StringComparison.Ordinal),
            json.Replace(Capture.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
            json.Replace("\"Start\"", "\"Middle\"", StringComparison.Ordinal),
            json.Replace("\"Start\"", "1", StringComparison.Ordinal),
            "{\"contract\":",
        })
        {
            Assert.Throws<InvalidDataException>(() => ServiceInventoryV1.Decode(Encoding.UTF8.GetBytes(refused)));
        }

        Assert.Throws<InvalidDataException>(() => ServiceInventoryV1.Decode([]));
        Assert.Throws<InvalidDataException>(() => ServiceInventoryV1.Decode(new byte[ServiceInventoryV1.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => (original with { CaptureId = Guid.Empty }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Moment = (ServiceInventoryMoment)3 }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { ReadEndedUtc = original.ReadStartedUtc.AddTicks(-1) }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with
        {
            Services = [.. Enumerable.Range(1, ServiceInventoryV1.MaximumServices + 1).Select(index => new InventoriedServiceV1 { Name = "svc" + index, ProcessId = 1_000 })],
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Services = [original.Services[0] with { Name = string.Empty }] }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with
        {
            Services = [original.Services[0] with { Name = new string('s', ServiceInventoryV1.MaximumNameLength + 1) }],
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (original with { Services = [original.Services[0] with { ProcessId = 0 }] }).Encode());

        // One service twice is refused, its name compared ignoring case as the manager compares it; two services sharing a host
        // are two services, as a shared host's are, and a list of none is a list.
        Assert.Contains("one service twice", Assert.Throws<InvalidDataException>(() => (original with
        {
            Services = [original.Services[0], original.Services[0] with { Name = original.Services[0].Name.ToUpperInvariant(), ProcessId = 2_000 }],
        }).Encode()).Message, StringComparison.Ordinal);
        Assert.Equal(original.Services[0].ProcessId, original.Services[1].ProcessId);
        _ = (original with { Services = [] }).Encode();
    }

    [Fact(DisplayName = "§18.5: published service inventories are read back, the start's before the stop's, carried by later generations and re-derivation, never released, and a generation without them states none")]
    public void PublishedInventoriesAreCarriedAndKept()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "service-test");
        string start = ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, 1);
        string stop = ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, 2);
        Assert.Equal(("service-inventory-start-0000000001.json", "service-inventory-stop-0000000002.json"), (start, stop));
        CommittedBoundary boundary = CommitWith(store, [(start, Example(ServiceInventoryMoment.Start).Encode())], 1);
        Assert.Equal([ServiceInventoryMoment.Start], ServiceInventoryV1.Read(store.Root, store.Current!).Select(static inventory => inventory.Moment));

        // The stop's is published with the capture's last generation, which carries the start's, as every later one does.
        boundary = CommitWith(store, [(stop, Example(ServiceInventoryMoment.Stop).Encode())], 2, plan: false);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        IReadOnlyList<ServiceInventoryV1> both = ServiceInventoryV1.Read(reopened.Root, reopened.Current!);
        Assert.Equal([ServiceInventoryMoment.Start, ServiceInventoryMoment.Stop], both.Select(static inventory => inventory.Moment));
        Assert.Equal(Example(ServiceInventoryMoment.Stop).Services, both[1].Services);
        ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
            reopened.ReleaseDependencies([start], "try to release the service inventory", DateTimeOffset.UtcNow));
        Assert.Contains("retention cannot release it", refusal.Message, StringComparison.Ordinal);

        _ = reopened.CommitReplacingDerived([], 2, boundary, DateTimeOffset.UtcNow);
        SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        Assert.Equal(3, replaced.Current!.Generation);
        Assert.Equal([ServiceInventoryMoment.Start, ServiceInventoryMoment.Stop],
            ServiceInventoryV1.Read(replaced.Root, replaced.Current).Select(static inventory => inventory.Moment));

        // A capture published once at its stop publishes both with that one generation.
        using var once = new TemporaryStoreDirectory();
        SessionStore single = SessionStore.Open(LocalOwnedDirectory.Open(once.Path), Guid.NewGuid(), "service-test");
        _ = CommitWith(single, [(start, Example(ServiceInventoryMoment.Start).Encode()),
            (ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, 1), Example(ServiceInventoryMoment.Stop).Encode())], 1);
        Assert.Equal([ServiceInventoryMoment.Start, ServiceInventoryMoment.Stop],
            ServiceInventoryV1.Read(single.Root, single.Current!).Select(static inventory => inventory.Moment));

        // A generation that names none states none. One carrying two starts, a stop's published before its start's, or an
        // inventory named as another moment's or a later generation's is refused.
        using var none = new TemporaryStoreDirectory();
        SessionStore plain = SessionStore.Open(LocalOwnedDirectory.Open(none.Path), Guid.NewGuid(), "service-test");
        _ = CommitWith(plain, [], 1);
        Assert.Empty(ServiceInventoryV1.Read(plain.Root, plain.Current!));
        using var two = new TemporaryStoreDirectory();
        SessionStore doubled = SessionStore.Open(LocalOwnedDirectory.Open(two.Path), Guid.NewGuid(), "service-test");
        _ = CommitWith(doubled, [(start, Example(ServiceInventoryMoment.Start).Encode())], 1);
        _ = CommitWith(doubled, [(ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, 2), Example(ServiceInventoryMoment.Start).Encode())], 2, plan: false);
        Assert.Contains("one of its start and one of its stop", Assert.Throws<InvalidDataException>(() =>
            ServiceInventoryV1.Read(doubled.Root, doubled.Current!)).Message, StringComparison.Ordinal);
        using var early = new TemporaryStoreDirectory();
        SessionStore reversed = SessionStore.Open(LocalOwnedDirectory.Open(early.Path), Guid.NewGuid(), "service-test");
        _ = CommitWith(reversed, [(ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, 1), Example(ServiceInventoryMoment.Stop).Encode())], 1);
        _ = CommitWith(reversed, [(ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, 2), Example(ServiceInventoryMoment.Start).Encode())], 2, plan: false);
        Assert.Contains("published before its start's", Assert.Throws<InvalidDataException>(() =>
            ServiceInventoryV1.Read(reversed.Root, reversed.Current!)).Message, StringComparison.Ordinal);
        foreach (string misnamed in new[] { ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, 1), ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, 2) })
        {
            using var wrong = new TemporaryStoreDirectory();
            SessionStore named = SessionStore.Open(LocalOwnedDirectory.Open(wrong.Path), Guid.NewGuid(), "service-test");
            _ = CommitWith(named, [(misnamed, Example(ServiceInventoryMoment.Start).Encode())], 1);
            Assert.Contains("is not named as an inventory of its capture's start", Assert.Throws<InvalidDataException>(() =>
                ServiceInventoryV1.Read(named.Root, named.Current!)).Message, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "§18.5: a generation builder stages one service inventory of each moment of its own capture, named for its moment and its generation")]
    public void TheBuilderStagesOneInventoryOfItsCapture()
    {
        using var directory = new TemporaryStoreDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "service-test");
        var clockId = new Domain.ClockId(Guid.Parse("33334444-5555-4666-8777-888899990000"));
        var clock = new Domain.SourceClockDescriptor(clockId, Domain.HostId.Derive("service-inventory-tests"), Domain.SourceClockKind.Monotonic,
            Domain.TimestampEncoding.Qpc, 10_000_000, 0, Domain.TimestampRounding.NearestEven, Domain.SourceClockMath.SessionTicksPerSecond * 60);
        var identity = new SegmentIdentityV1
        {
            CaptureId = new Domain.CaptureId(Capture),
            ClockId = clockId,
            TimestampEncoding = Domain.TimestampEncoding.Qpc,
            Derivation = Domain.NormalizerContractVersion.V1,
        };
        using (DerivedGenerationBuilder builder = DerivedGenerationBuilder.Begin(store, identity, clock, DateTimeOffset.UtcNow))
        {
            builder.StageServiceInventory(Example(ServiceInventoryMoment.Start));
            builder.StageServiceInventory(Example(ServiceInventoryMoment.Stop));
            Assert.Throws<InvalidOperationException>(() => builder.StageServiceInventory(Example(ServiceInventoryMoment.Start)));
        }

        using DerivedGenerationBuilder other = DerivedGenerationBuilder.Begin(
            store, identity with { CaptureId = new Domain.CaptureId(Guid.NewGuid()) }, clock, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => other.StageServiceInventory(Example(ServiceInventoryMoment.Start)));
    }

    // Three running services: two sharing one host, whose creation time was read, and one whose host could not be opened.
    private static ServiceInventoryV1 Example(ServiceInventoryMoment moment) => new()
    {
        Contract = ServiceInventoryV1.ContractName,
        CaptureId = Capture,
        Moment = moment,
        ReadStartedUtc = moment == ServiceInventoryMoment.Start ? Read : Read.AddMinutes(1),
        ReadEndedUtc = (moment == ServiceInventoryMoment.Start ? Read : Read.AddMinutes(1)).AddMilliseconds(4),
        Services =
        [
            new() { Name = "Dnscache", ProcessId = 1_820, HostCreatedUtc = HostCreated },
            new() { Name = "nsi", ProcessId = 1_820, HostCreatedUtc = HostCreated },
            new() { Name = "Protected", ProcessId = 3_004 },
        ],
    };

    private static CommittedBoundary CommitWith(SessionStore store, (string Name, byte[] Bytes)[] files, long journalGeneration, bool plan = true)
    {
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach ((string fileName, byte[] bytes) in files)
            {
                StoreStagingFile file = store.Stage(fileName, StoreDependencyKind.ServiceInventory);
                staged.Add(file);
                file.Content.Write(bytes);
                _ = file.Complete();
            }

            byte[] journalBytes = Encoding.UTF8.GetBytes("journal " + journalGeneration);
            string journalName = $"journal-{journalGeneration:D10}.icatj";
            StoreStagingFile journal = store.Stage(journalName, StoreDependencyKind.Journal);
            staged.Add(journal);
            journal.Content.Write(journalBytes);
            _ = journal.Complete();
            // A capture retains its normalizer plan once, with its first journal chunk.
            if (plan)
            {
                StoreStagingFile retained = store.Stage($"normalizer-plan-{journalGeneration:D10}.json", StoreDependencyKind.DerivationPlan);
                staged.Add(retained);
                retained.Content.Write(Encoding.UTF8.GetBytes("retained plan"));
                _ = retained.Complete();
            }
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
                System.IO.Path.GetTempPath(), "InterCat.Storage.Tests.Services", Guid.NewGuid().ToString("N"));
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
