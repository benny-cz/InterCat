using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Journal.Tests.EvidenceRecordings;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// A viewer that crashes loses no evidence, and the next launch offers to finish its session from the evidence the
/// broker kept (plan §3.1 step 6). The follow's ticket says where that evidence is; the evidence and the capture's owner
/// lease say what finishing can still do.
/// </summary>
public sealed class InterruptedFollowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact(DisplayName = "§3.1: a follow's ticket is offered only once no follow holds it, and only for the session beside it")]
    public void ATicketIsOfferedOnlyOnceNoFollowHoldsIt()
    {
        using var root = new TemporaryDirectory();
        string session = Path.Combine(root.Path, "explore-1");
        LiveFollowTicket ticket = LiveFollowTicket.For(
            Guid.NewGuid(), Path.Combine(root.Path, "evidence"), session, Now, Now.AddSeconds(30));

        // A running follow holds its ticket: nothing is interrupted, and nothing can take or remove it.
        using (LiveFollowHold running = ticket.Hold())
        {
            Assert.Empty(LiveFollowTicket.FindInterrupted(root.Path));
            Assert.Null(LiveFollowTicket.TryTake(ticket));
            Assert.False(LiveFollowTicket.Remove(session));

            // Each renewal records the broker's new expiry; an older one changes nothing.
            running.Renew(Now.AddSeconds(40));
            running.Renew(Now.AddSeconds(35));
            Assert.Equal(Now.AddSeconds(40), running.Ticket.OwnerLeaseExpiresUtc);
        }

        // Once the follow ends early, its ticket is found as it was last written.
        LiveFollowTicket found = Assert.Single(LiveFollowTicket.FindInterrupted(root.Path));
        Assert.Equal(ticket with { OwnerLeaseExpiresUtc = Now.AddSeconds(40) }, found);

        // A ticket naming another session than the one beside it, and a file that is not a ticket, are never offered.
        File.Copy(LiveFollowTicket.PathFor(session), Path.Combine(root.Path, "explore-2" + LiveFollowTicket.Suffix));
        File.WriteAllText(Path.Combine(root.Path, "explore-3" + LiveFollowTicket.Suffix), "{ not a ticket");
        File.WriteAllText(
            Path.Combine(root.Path, "explore-4" + LiveFollowTicket.Suffix),
            File.ReadAllText(LiveFollowTicket.PathFor(session)).Replace("live-follow-v1", "live-follow-v2", StringComparison.Ordinal)
                .Replace("explore-1", "explore-4", StringComparison.Ordinal));
        Assert.Equal(found, Assert.Single(LiveFollowTicket.FindInterrupted(root.Path)));

        // A follow that completes takes its ticket with it.
        found.Hold().Complete();
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
        Assert.Empty(LiveFollowTicket.FindInterrupted(root.Path));
    }

    [Fact(DisplayName = "§3.1: a crashed follow is finished from the evidence the broker kept, and its ticket goes")]
    public async Task ACrashedFollowIsFinishedFromTheEvidence()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        SessionManifestV1 source = evidence.Current!;
        int published = LiveSessionFollower.Progress(source).Chunks;
        Assert.InRange(published, 2, 3);

        // The viewer followed one chunk and crashed: its ticket stays, released, beside its session.
        string session = Path.Combine(root.Path, "explore-1");
        Directory.CreateDirectory(session);
        SessionStore crashed = SessionStore.Open(LocalOwnedDirectory.Open(session), source.SessionId, source.SourceIdentity);
        _ = LiveSessionFollower.Open(evidence, crashed).CatchUp(maximumChunks: 1);
        LiveFollowTicket.For(source.SessionId, evidencePath, session, Now, Now.AddSeconds(30)).Hold().Dispose();

        LiveFollowTicket ticket = Assert.Single(LiveFollowTicket.FindInterrupted(root.Path));
        InterruptedFollow found = InterruptedFollow.Assess(ticket, Now);
        Assert.Equal(InterruptedFollowState.Finishable, found.State);
        Assert.Equal((1, published, published - 1, true), (found.SessionChunks, found.EvidenceChunks, found.MissingChunks, found.HasSession));

        var steps = new List<FollowStep>();
        InterruptedFollowResult finished = InterruptedFollow.Finish(ticket, new Progress(steps), Now);

        Assert.True(finished.Completed);
        Assert.True(finished.Step.Finished);
        Assert.Equal((published, published, 4L), (finished.Step.DerivedChunks, finished.Step.EvidenceChunks, finished.Step.DerivedRecords));
        Assert.NotEmpty(steps);
        Assert.Equal(4, JournalRederivation.Verify(finished.Session).ObservationRows);
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
        Assert.Empty(LiveFollowTicket.FindInterrupted(root.Path));
        Assert.Equal(InterruptedFollowState.Complete, InterruptedFollow.Assess(ticket, Now).State);
    }

    [Fact(DisplayName = "§3.1: a finish waits for a follow that still holds its ticket, and refuses evidence it did not follow")]
    public async Task AFinishRefusesWhileHeldAndForOtherEvidence()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        string otherPath = Path.Combine(root.Path, "other");
        Directory.CreateDirectory(evidencePath);
        Directory.CreateDirectory(otherPath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);
        _ = await RecordEvidence(otherPath, ordinals: [1, 2, 3, 4]);
        string session = Path.Combine(root.Path, "explore-1");
        LiveFollowTicket ticket = LiveFollowTicket.For(Guid.NewGuid(), evidencePath, session, Now, Now.AddSeconds(30));

        using (ticket.Hold())
        {
            Assert.Contains(
                "Another InterCat window",
                Assert.Throws<InvalidOperationException>(() => InterruptedFollow.Finish(ticket, nowUtc: Now)).Message,
                StringComparison.Ordinal);
        }

        _ = InterruptedFollow.Finish(ticket, nowUtc: Now);

        // A ticket rewritten to name another capture's evidence cannot splice it into this session.
        LiveFollowTicket forged = ticket with { EvidenceDirectory = otherPath };
        forged.Hold().Dispose();
        _ = Assert.ThrowsAny<Exception>(() => InterruptedFollow.Finish(forged, nowUtc: Now));
        Assert.True(File.Exists(LiveFollowTicket.PathFor(session)));
    }

    [Fact(DisplayName = "§3.1: a capture that ended without finalizing is finished from what it published, once its lease settled")]
    public async Task AnUnfinalizedCaptureIsFinishedOnceItSettled()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);

        // The broker died after its first publication: the evidence names one chunk and no finality.
        SessionManifestV1 first = JsonSerializer.Deserialize<SessionManifestV1>(
            File.ReadAllText(Path.Combine(evidencePath, SessionManifestV1.FileNameFor(1))), SessionManifestV1.Json)!;
        File.WriteAllText(
            Path.Combine(evidencePath, SessionPointerV1.FileName),
            JsonSerializer.Serialize(SessionPointerV1.For(first), SessionManifestV1.Json));
        Assert.Equal((1, false), LiveSessionFollower.Progress(
            SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath)).Current));
        string session = Path.Combine(root.Path, "explore-1");

        // Within the owner lease and the broker's time to finalize, the capture may still be recording.
        LiveFollowTicket recent = LiveFollowTicket.For(first.SessionId, evidencePath, session, Now, Now.AddSeconds(20));
        Assert.Equal(InterruptedFollowState.StillRecording, InterruptedFollow.Assess(recent, Now).State);
        Assert.Equal(
            InterruptedFollowState.EndedUnfinalized,
            InterruptedFollow.Assess(recent, Now.AddSeconds(20) + InterruptedFollow.FinalizationAllowance).State);

        // Once it settled, finishing derives what it published. The session has no finality, and never will.
        LiveFollowTicket settled = recent with { OwnerLeaseExpiresUtc = Now.AddMinutes(-5) };
        settled.Hold().Dispose();
        InterruptedFollowResult finished = InterruptedFollow.Finish(settled, nowUtc: Now);

        Assert.True(finished.Completed);
        Assert.False(finished.Step.Finished);
        Assert.Equal((1, 1), (finished.Step.DerivedChunks, finished.Step.EvidenceChunks));
        Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
    }

    [Fact(DisplayName = "§3.1: evidence that cannot be read leaves the session as it was followed")]
    public async Task UnreadableEvidenceLeavesTheSession()
    {
        using var root = new TemporaryDirectory();
        string evidencePath = Path.Combine(root.Path, "capture");
        Directory.CreateDirectory(evidencePath);
        _ = await RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        string session = Path.Combine(root.Path, "explore-1");
        Directory.CreateDirectory(session);
        SessionStore followed = SessionStore.Open(
            LocalOwnedDirectory.Open(session), evidence.Current!.SessionId, evidence.Current.SourceIdentity);
        _ = LiveSessionFollower.Open(evidence, followed).CatchUp(maximumChunks: 1);
        LiveFollowTicket ticket = LiveFollowTicket.For(Guid.NewGuid(), evidencePath, session, Now, Now.AddSeconds(30));

        // Damaged evidence is named, not guessed at.
        File.WriteAllText(Path.Combine(evidencePath, SessionPointerV1.FileName), "{");
        File.Delete(Path.Combine(evidencePath, SessionPointerV1.PreviousFileName));
        InterruptedFollow damaged = InterruptedFollow.Assess(ticket, Now);
        Assert.Equal(InterruptedFollowState.EvidenceGone, damaged.State);
        Assert.True(damaged.HasSession);
        Assert.False(string.IsNullOrWhiteSpace(damaged.Problem));

        // Removed evidence can no longer finish the session, but it keeps what was followed.
        Directory.Delete(evidencePath, recursive: true);
        Assert.Equal(InterruptedFollowState.StillRecording, InterruptedFollow.Assess(ticket, Now).State);
        InterruptedFollow gone = InterruptedFollow.Assess(ticket, Now.AddMinutes(5));
        Assert.Equal((InterruptedFollowState.EvidenceGone, 1, null), (gone.State, gone.SessionChunks, gone.EvidenceChunks));
    }

    [Theory(DisplayName = "§3.1: an unfinished capture's state follows what its session and evidence hold and whether its lease settled")]
    [InlineData(true, 1, 3, false, true, false, InterruptedFollowState.Complete)]
    [InlineData(false, 1, null, false, false, false, InterruptedFollowState.EvidenceGone)]
    [InlineData(false, 0, null, false, true, false, InterruptedFollowState.StillRecording)]
    [InlineData(false, 1, null, false, true, true, InterruptedFollowState.EvidenceGone)]
    [InlineData(false, 0, null, false, true, true, InterruptedFollowState.Complete)]
    [InlineData(false, 1, 3, true, true, false, InterruptedFollowState.Finishable)]
    [InlineData(false, 1, 3, true, true, true, InterruptedFollowState.Finishable)]
    [InlineData(false, 1, 3, false, true, false, InterruptedFollowState.StillRecording)]
    [InlineData(false, 1, 3, false, true, true, InterruptedFollowState.EndedUnfinalized)]
    [InlineData(false, 3, 3, false, true, false, InterruptedFollowState.StillRecording)]
    [InlineData(false, 3, 3, false, true, true, InterruptedFollowState.Complete)]
    [InlineData(false, 0, 0, false, true, true, InterruptedFollowState.Complete)]
    public void TheStateFollowsTheSessionTheEvidenceAndTheLease(
        bool sessionFinished,
        int sessionChunks,
        int? evidenceChunks,
        bool evidenceFinished,
        bool readable,
        bool settled,
        InterruptedFollowState expected) =>
        Assert.Equal(
            expected,
            InterruptedFollow.Decide(sessionFinished, sessionChunks, evidenceChunks, evidenceFinished, readable, settled));

    private sealed class Progress(List<FollowStep> steps) : IProgress<FollowStep>
    {
        public void Report(FollowStep value) => steps.Add(value);
    }
}
