using System.Diagnostics;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// §19.5: a live capture names the processes that collected it - its broker and the client that asked for it, or the
/// recorder - by PID and creation time in its first generation (`contracts/collector-identities-v1.md`), which every later
/// generation carries, a follower mirrors and a capture interrupted before its last publication keeps.
/// </summary>
public sealed class CollectorRecordingTests
{
    [Fact(DisplayName = "§19.5: a live capture names its collectors in its first generation, which its later ones carry, a follower mirrors and an interrupted capture keeps")]
    public async Task ACaptureNamesItsCollectorsFromItsFirstGeneration()
    {
        using var evidenceDirectory = new TemporaryDirectory();
        CollectorProcessV1 broker = CollectorProcesses.Current(CollectorRole.Broker);
        var client = new CollectorProcessV1 { Role = CollectorRole.Client, ProcessId = 7_008 };
        LiveRecordingResult recorded = await RecordEvidence(evidenceDirectory.Path, ordinals: [1, 2, 3], collectors: [broker, client]);

        // The finished capture carries them under its first generation's name, as published.
        SessionStore finished = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        Assert.True(finished.Current!.Generation > 1);
        StoreDependency file = Assert.Single(finished.Current.Dependencies,
            dependency => dependency.Kind == StoreDependencyKind.CollectorIdentities);
        Assert.Equal(DerivedGenerationBuilder.CollectorIdentitiesFileName(1), file.Name);
        CollectorIdentitiesV1 named = CollectorIdentitiesV1.Read(finished.Root, finished.Current)!;
        Assert.Equal([broker, client], named.Processes);
        Assert.Equal(recorded.Collectors!.CaptureId, named.CaptureId);

        // A follower mirrors them once, however many chunks it mirrors after the first.
        using (var followed = new TemporaryDirectory())
        {
            SessionStore derived = SessionStore.Open(
                LocalOwnedDirectory.Open(followed.Path), finished.Current.SessionId, finished.Current.SourceIdentity);
            Assert.True(LiveSessionFollower.Open(finished, derived).CatchUp().Finished);
            Assert.Equal(named.Processes, CollectorIdentitiesV1.Read(derived.Root, derived.Current!)!.Processes);
        }

        // A capture interrupted before its last publication still names them, and a follower mirrors them from it; resumed
        // once the last publication is back, the follow mirrors it and still names them once.
        string pointer = Path.Combine(evidenceDirectory.Path, SessionPointerV1.FileName);
        string last = File.ReadAllText(pointer);
        _ = RewindToUnfinalized(evidenceDirectory.Path);
        SessionStore interrupted = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path));
        Assert.Equal(named.Processes, CollectorIdentitiesV1.Read(interrupted.Root, interrupted.Current!)!.Processes);
        using (var followed = new TemporaryDirectory())
        {
            SessionStore derived = SessionStore.Open(
                LocalOwnedDirectory.Open(followed.Path), interrupted.Current!.SessionId, interrupted.Current.SourceIdentity);
            Assert.False(LiveSessionFollower.Open(interrupted, derived).CatchUp().Finished);
            Assert.Equal(named.Processes, CollectorIdentitiesV1.Read(derived.Root, derived.Current!)!.Processes);
            File.WriteAllText(pointer, last);
            SessionStore resumed = SessionStore.OpenExisting(LocalOwnedDirectory.Open(followed.Path));
            Assert.True(LiveSessionFollower.Open(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidenceDirectory.Path)), resumed).CatchUp().Finished);
            Assert.Equal(named.Processes, CollectorIdentitiesV1.Read(resumed.Root, resumed.Current!)!.Processes);
        }

        // A capture that publishes once, when it stops, names them in that generation.
        using var once = new TemporaryDirectory();
        SessionStore single = SessionStore.Open(LocalOwnedDirectory.Open(once.Path), Guid.NewGuid(), "live-tests");
        _ = await LiveSessionRecorder.RecordAsync(Plan(), new ScriptedHost(), single, _ => Task.CompletedTask, DateTimeOffset.UtcNow,
            collectors: [broker]);
        Assert.Equal(DerivedGenerationBuilder.CollectorIdentitiesFileName(single.Current!.Generation), Assert.Single(
            single.Current.Dependencies, dependency => dependency.Kind == StoreDependencyKind.CollectorIdentities).Name);
        Assert.Equal([broker], CollectorIdentitiesV1.Read(single.Root, single.Current)!.Processes);

        // A recording that names none publishes none, and collectors no file could hold refuse the capture before it starts.
        using var plain = new TemporaryDirectory();
        Assert.Null((await RecordEvidence(plain.Path, ordinals: [1, 2, 3])).Collectors);
        SessionStore unnamed = SessionStore.OpenExisting(LocalOwnedDirectory.Open(plain.Path));
        Assert.Null(CollectorIdentitiesV1.Read(unnamed.Root, unnamed.Current!));
        using var refused = new TemporaryDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => RecordEvidence(refused.Path, ordinals: [1], collectors: [broker, broker]));
    }

    [Fact(DisplayName = "§19.5: a collector is named by its PID and the creation time its process reports, left out where the process cannot be read")]
    public void ACollectorIsNamedByItsPidAndCreation()
    {
        using Process process = Process.GetCurrentProcess();
        CollectorProcessV1 self = CollectorProcesses.Current(CollectorRole.Recorder);
        Assert.Equal((CollectorRole.Recorder, process.Id), (self.Role, self.ProcessId));
        Assert.Equal(process.StartTime.ToUniversalTime(), self.CreatedUtc!.Value.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, self.CreatedUtc.Value.Offset);
        Assert.Equal(self with { Role = CollectorRole.Client }, CollectorProcesses.Of(process.Id, CollectorRole.Client));
        Assert.Null(CollectorProcesses.Of(int.MaxValue - 3, CollectorRole.Client).CreatedUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorProcesses.Of(0, CollectorRole.Client));

        // A broker names itself, and the client that asked it to record when its control pipe named one.
        Assert.Equal([self with { Role = CollectorRole.Broker }], CollectorProcesses.ForBroker(null));
        Assert.Equal([self with { Role = CollectorRole.Broker }, self with { Role = CollectorRole.Client }],
            CollectorProcesses.ForBroker(process.Id));
    }
}
