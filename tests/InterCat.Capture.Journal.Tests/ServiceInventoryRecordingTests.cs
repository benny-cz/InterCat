using System.ComponentModel;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// §18.5: a live capture reads the running services and their hosts once its session delivers and again as it stops
/// (`contracts/service-inventory-v1.md`, ADR-095), the start's published with its first generation and the stop's with its
/// last - both with the one of a capture published once - which a follower mirrors; a list that cannot be read is none.
/// </summary>
public sealed class ServiceInventoryRecordingTests
{
    private static readonly DateTimeOffset HostCreated = new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero).AddTicks(7);

    [Fact(DisplayName = "§18.5: a live capture publishes the services running as it started with its first generation and as it stopped with its last, which a follower mirrors once each")]
    public async Task ACapturePublishesItsServicesAtItsStartAndStop()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        var reads = new List<int>();
        ServiceInventorySource source = Scripted(reads,
            [new("Dnscache", 1_820, HostCreated), new("nsi", 1_820, HostCreated)],
            [new("Dnscache", 1_820, HostCreated), new("W32Time", 2_400, null)]);
        LiveRecordingResult recorded = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3], services: source);
        Assert.Equal(2, reads.Count);

        // The finished capture carries the start's under its first generation's name and the stop's under its last's.
        SessionStore finished = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        long last = finished.Current!.Generation;
        Assert.True(last > 1);
        Assert.Equal(
            [ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, 1), ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, last)],
            finished.Current.Dependencies.Where(static dependency => dependency.Kind == StoreDependencyKind.ServiceInventory)
                .Select(static dependency => dependency.Name).Order(StringComparer.Ordinal));
        IReadOnlyList<ServiceInventoryV1> read = ServiceInventoryV1.Read(finished.Root, finished.Current);
        Assert.Equal(["Dnscache", "nsi"], read[0].Services.Select(static service => service.Name));
        Assert.Equal(["Dnscache", "W32Time"], read[1].Services.Select(static service => service.Name));
        Assert.Equal((1_820, (DateTimeOffset?)HostCreated), (read[0].Services[0].ProcessId, read[0].Services[0].HostCreatedUtc));
        Assert.Null(read[1].Services[1].HostCreatedUtc);
        Assert.Single(read.Select(static inventory => inventory.CaptureId).Distinct());
        Assert.True(read[0].ReadEndedUtc <= read[1].ReadStartedUtc);
        Assert.Equal(read.Select(static inventory => inventory.Moment), recorded.Services.Select(static inventory => inventory.Moment));

        // A follower mirrors each once, however many chunks it mirrors.
        using (var followed = new TemporaryDirectory())
        {
            SessionStore derived = SessionStore.Open(
                LocalOwnedDirectory.Open(followed.Path), finished.Current.SessionId, finished.Current.SourceIdentity);
            Assert.True(LiveSessionFollower.Open(finished, derived).CatchUp().Finished);
            IReadOnlyList<ServiceInventoryV1> mirrored = ServiceInventoryV1.Read(derived.Root, derived.Current!);
            Assert.Equal(read.Select(static inventory => inventory.Services), mirrored.Select(static inventory => inventory.Services));
        }

        // A capture interrupted before its last publication keeps the start's, which a follower mirrors from it.
        _ = RewindToUnfinalized(evidenceDirectory.Path);
        SessionStore interrupted = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        Assert.Equal([ServiceInventoryMoment.Start], ServiceInventoryV1.Read(interrupted.Root, interrupted.Current!).Select(static inventory => inventory.Moment));
    }

    [Fact(DisplayName = "§18.5: a capture published once publishes both of its service inventories with that generation, and a list that cannot be read is recorded as none while the capture goes on")]
    public async Task OnePublicationHoldsBothAndAnUnreadableListIsNone()
    {
        using var once = new TemporaryDirectory();
        SessionStore single = SessionStore.Open(LocalOwnedDirectory.Open(once.Path), Guid.NewGuid(), "live-tests");
        var reads = new List<int>();
        _ = await LiveSessionRecorder.RecordAsync(Plan(), new ScriptedHost(), single, _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            services: Scripted(reads, [new("Dnscache", 1_820, HostCreated)], [new("Dnscache", 1_820, HostCreated)]));
        long generation = single.Current!.Generation;
        Assert.Equal(
            [ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Start, generation), ServiceInventoryV1.FileNameFor(ServiceInventoryMoment.Stop, generation)],
            single.Current.Dependencies.Where(static dependency => dependency.Kind == StoreDependencyKind.ServiceInventory)
                .Select(static dependency => dependency.Name).Order(StringComparer.Ordinal));

        // The manager refusing to list its services leaves the capture without an inventory, never with an empty one.
        using var refused = new TemporaryDirectory();
        var failing = new ServiceInventorySource { Read = static () => throw new Win32Exception(5, "Access is denied.") };
        LiveRecordingResult recorded = await RecordEvidence(refused.Path, ordinals: [1, 2, 3], services: failing);
        Assert.Empty(recorded.Services);
        SessionStore unread = SessionStore.OpenExisting(LocalOwnedDirectory.Open(refused.Path));
        Assert.Empty(ServiceInventoryV1.Read(unread.Root, unread.Current!));
        Assert.Equal(3, recorded.JournaledRecords);

        // A list no inventory can hold, as one naming a service twice, is none too; a recording asked for none publishes none.
        Assert.Null(Scripted([], [new("Dnscache", 1_820, null), new("DNSCACHE", 1_820, null)], []).Take(ServiceInventoryMoment.Start, Guid.NewGuid()));
        using var plain = new TemporaryDirectory();
        Assert.Empty((await RecordEvidence(plain.Path, ordinals: [1, 2, 3])).Services);
    }

    [Fact(DisplayName = "§18.5: an inventory taken from a source is the list it read, of its moment and capture, bracketed by when its read began and ended")]
    public void AnInventoryIsTheListRead()
    {
        var capture = Guid.NewGuid();
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ServiceInventoryV1 taken = Scripted([], [new("Dnscache", 1_820, HostCreated)], []).Take(ServiceInventoryMoment.Start, capture)!;
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.Equal((ServiceInventoryV1.ContractName, capture, ServiceInventoryMoment.Start), (taken.Contract, taken.CaptureId, taken.Moment));
        Assert.True(before <= taken.ReadStartedUtc && taken.ReadStartedUtc <= taken.ReadEndedUtc && taken.ReadEndedUtc <= after);
        Assert.Equal([new InventoriedServiceV1 { Name = "Dnscache", ProcessId = 1_820, HostCreatedUtc = HostCreated }], taken.Services);
    }

    // A source answering the start's list on its first read and the stop's on its second, counting its reads.
    private static ServiceInventorySource Scripted(List<int> reads, IReadOnlyList<RunningService> atStart, IReadOnlyList<RunningService> atStop) =>
        new()
        {
            Read = () =>
            {
                reads.Add(reads.Count);
                return reads.Count == 1 ? atStart : atStop;
            },
        };
}
