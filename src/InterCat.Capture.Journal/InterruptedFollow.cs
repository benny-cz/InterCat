using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>What a follow that ended early left, and what finishing it can do (plan §3.1 step 6).</summary>
public enum InterruptedFollowState
{
    /// <summary>The capture stopped and was finalized, and its evidence holds chunks the session does not.</summary>
    Finishable = 1,

    /// <summary>
    /// The capture is not finalized and its owner lease has not been lapsed long enough for the broker to have stopped
    /// it, so it may still be recording: as in the half-minute after a viewer crashed.
    /// </summary>
    StillRecording = 2,

    /// <summary>
    /// The capture's owner lease lapsed long ago and it was never finalized, as when the broker or the machine stopped
    /// first. Its published chunks can still be derived; records after its last publication were never kept.
    /// </summary>
    EndedUnfinalized = 3,

    /// <summary>The evidence can no longer be read; the session keeps what was followed before the viewer stopped.</summary>
    EvidenceGone = 4,

    /// <summary>The session holds everything the capture published, or the capture published nothing.</summary>
    Complete = 5,
}

/// <summary>What a finish derived, and the store it derived into.</summary>
/// <param name="Completed">Whether the session now holds everything the capture will ever publish; its ticket is gone.</param>
public sealed record InterruptedFollowResult(FollowStep Step, SessionStore Session, bool Completed);

/// <summary>One interrupted follow, as a later launch finds it.</summary>
/// <param name="SessionChunks">Chunks the user's session holds.</param>
/// <param name="EvidenceChunks">Chunks the evidence holds, or null when there is no evidence to read.</param>
/// <param name="Problem">Why the evidence could not be read, for <see cref="InterruptedFollowState.EvidenceGone"/>.</param>
public sealed record InterruptedFollow(
    LiveFollowTicket Ticket,
    InterruptedFollowState State,
    int SessionChunks,
    int? EvidenceChunks,
    string? Problem)
{
    /// <summary>
    /// How long after its owner lease lapses a broker has to stop and finalize a capture. The broker stops a capture
    /// within seconds of the lapse and finalizes it within a few more; a capture unfinalized after this never will be.
    /// </summary>
    public static readonly TimeSpan FinalizationAllowance = TimeSpan.FromSeconds(60);

    /// <summary>How many chunks a finish derives between two progress reports.</summary>
    private const int ChunksPerStep = 16;

    /// <summary>Whether the user's session exists and holds a generation to open.</summary>
    public bool HasSession => SessionChunks > 0;

    /// <summary>Chunks the evidence holds and the session does not.</summary>
    public int MissingChunks => EvidenceChunks is { } evidence ? Math.Max(0, evidence - SessionChunks) : 0;

    /// <summary>
    /// When the capture can no longer be recording unless it was finalized: its owner lease's expiry at the follow's last
    /// renewal, plus the broker's time to stop and finalize.
    /// </summary>
    public DateTimeOffset SettledUtc => Ticket.OwnerLeaseExpiresUtc + FinalizationAllowance;

    /// <summary>What the ticket's evidence and session hold now. Reads only; writes nothing.</summary>
    public static InterruptedFollow Assess(LiveFollowTicket ticket, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        (int sessionChunks, bool sessionFinished) = SessionProgress(ticket.SessionDirectory);
        int? evidenceChunks = null;
        bool evidenceFinished = false;
        string? problem = null;
        if (Directory.Exists(ticket.EvidenceDirectory))
        {
            try
            {
                (evidenceChunks, evidenceFinished) = LiveSessionFollower.Progress(
                    SessionStore.OpenExisting(LocalOwnedDirectory.Open(ticket.EvidenceDirectory)).Current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                problem = exception.Message;
            }
        }

        InterruptedFollowState state = Decide(
            sessionFinished, sessionChunks, evidenceChunks, evidenceFinished, readable: problem is null,
            now >= ticket.OwnerLeaseExpiresUtc + FinalizationAllowance);
        return new(ticket, state, sessionChunks, evidenceChunks, problem);
    }

    /// <summary>
    /// The state of an interrupted follow from what its session and evidence hold. <paramref name="evidenceChunks"/> is
    /// null when the evidence directory does not exist, which it does not until the capture's first publication.
    /// </summary>
    public static InterruptedFollowState Decide(
        bool sessionFinished,
        int sessionChunks,
        int? evidenceChunks,
        bool evidenceFinished,
        bool readable,
        bool settled)
    {
        if (sessionFinished)
        {
            return InterruptedFollowState.Complete;
        }

        if (!readable)
        {
            return InterruptedFollowState.EvidenceGone;
        }

        if (evidenceChunks is not { } published)
        {
            // No evidence yet: a capture that may still publish is waited for; one that cannot has lost it, or had none.
            return !settled ? InterruptedFollowState.StillRecording
                : sessionChunks > 0 ? InterruptedFollowState.EvidenceGone
                : InterruptedFollowState.Complete;
        }

        // Finality comes with a capture's last chunk, so a finalized capture the session lacks finality for still has a
        // chunk to derive; one the session holds every chunk of cannot be finished further.
        return published <= sessionChunks ? (evidenceFinished || settled ? InterruptedFollowState.Complete
                : InterruptedFollowState.StillRecording)
            : evidenceFinished ? InterruptedFollowState.Finishable
            : settled ? InterruptedFollowState.EndedUnfinalized
            : InterruptedFollowState.StillRecording;
    }

    /// <summary>
    /// Derives every chunk the session does not hold yet, a few at a time so <paramref name="progress"/> hears each step.
    /// The ticket is taken for the finish, so a second launch cannot finish the same capture at once, and removed once
    /// the session holds everything the capture will publish: its finality, or every chunk of a capture that settled
    /// unfinalized. Evidence that is not the session's own, or that released chunks the session needs, is refused.
    /// </summary>
    public static InterruptedFollowResult Finish(
        LiveFollowTicket ticket,
        IProgress<FollowStep>? progress = null,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using LiveFollowHold hold = LiveFollowTicket.TryTake(ticket)
            ?? throw new InvalidOperationException(
                "Another InterCat window is following or finishing this capture. Finish it from there.");
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(ticket.EvidenceDirectory));
        SessionManifestV1 source = evidence.Current
            ?? throw new InvalidOperationException("The capture published nothing, so there is nothing to finish.");
        Directory.CreateDirectory(ticket.SessionDirectory);
        SessionStore derived = SessionStore.Open(
            LocalOwnedDirectory.Open(ticket.SessionDirectory), source.SessionId, source.SourceIdentity);
        LiveSessionFollower follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: cancellationToken);
        FollowStep step;
        do
        {
            step = follower.CatchUp(ChunksPerStep, cancellationToken);
            progress?.Report(step);
        }
        while (!step.Finished && step.MirroredChunks > 0);

        bool settled = (nowUtc ?? DateTimeOffset.UtcNow) >= ticket.OwnerLeaseExpiresUtc + FinalizationAllowance;
        bool completed = step.Finished || (settled && step.DerivedChunks >= step.EvidenceChunks);
        if (completed)
        {
            hold.Complete();
        }

        return new(step, derived, completed);
    }

    private static (int Chunks, bool Finished) SessionProgress(string sessionDirectory)
    {
        try
        {
            return Directory.Exists(sessionDirectory)
                ? LiveSessionFollower.Progress(SessionStore.OpenExisting(LocalOwnedDirectory.Open(sessionDirectory)).Current)
                : (0, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (0, false);
        }
    }
}
