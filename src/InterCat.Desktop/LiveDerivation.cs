using System.Diagnostics;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>What one follow-and-project step found: the follow, and a new generation's overview when there was one.</summary>
/// <param name="Follow">The follow step, or null while the evidence session has published nothing to follow.</param>
/// <param name="Generation">The derived generation this step saw first, or null when it saw no new one.</param>
/// <param name="GenerationAt">When, from the user's start action, the step saw that generation.</param>
/// <param name="OverviewProblem">Why the new generation has no overview, when projection refused it.</param>
/// <param name="Rolling">
/// What the session's rolling retention did after this step's mirrors: a release, a pin holding it past its window, or why
/// the capture must stop. Null when the session keeps every record or this step mirrored nothing.
/// </param>
internal sealed record LiveDerivationStep(
    FollowStep? Follow,
    long? Generation,
    TimeSpan? GenerationAt,
    SessionOverviewBundle? Overview,
    TimeSpan Derivation,
    TimeSpan Projection,
    string? OverviewProblem,
    RollingStep? Rolling = null);

/// <summary>
/// Follows one live capture's published evidence into the user's own session and projects each new generation
/// (ADR-027). It holds the follower and the derived store for the capture's life. Steps run one at a time on the thread
/// pool, off the loop that reads status and renews the owner lease, and a step's state is read only once it finished.
/// </summary>
/// <param name="stores">
/// Where the derived store becomes the session's shared store, so the window's queries read through the store that
/// publishes; the process-wide registry unless a test gives its own.
/// </param>
/// <param name="policy">
/// The evidence policy each generation is projected under, asked for as each is projected; correlated evidence when null.
/// </param>
/// <param name="rolling">
/// The newest stretch of session time the session keeps, its older records released after a step's mirrors by this
/// follow's own writer, the only one that may publish beneath it (ADR-044, ADR-045); null keeps every record.
/// </param>
internal sealed class LiveDerivation(
    string evidencePath,
    string sessionPath,
    Stopwatch elapsed,
    SessionStoreRegistry? stores = null,
    Func<EvidencePolicy>? policy = null,
    RollingRetention? rolling = null) : IDisposable
{
    private readonly CancellationTokenSource halt = new();
    private LiveSessionFollower? follower;
    private SessionStore? derived;
    private long shownGeneration = -1;

    /// <summary>The last follow step, or null before the first.</summary>
    public FollowStep? Last { get; private set; }

    /// <summary>
    /// What publishing the session's derivation checkpoint did once the follow finished, so the session reopens without
    /// reading every record (derivation-checkpoint-v1); null until then.
    /// </summary>
    public CheckpointPublication? Checkpoint { get; private set; }

    /// <summary>Whether the user's session holds a derived generation.</summary>
    public bool HasSession => derived?.Current is not null;

    /// <summary>The store the capture's session is derived into, once the evidence has published something.</summary>
    internal SessionStore? Store => derived;

    /// <summary>The session's rolling retention: its window, its releases, the pin holding it and why it stopped; null when it keeps every record.</summary>
    public RollingRetention? Rolling => rolling;

    /// <summary>One step on the thread pool, cancelled by <paramref name="cancellationToken"/> or by <see cref="HaltAsync"/>.</summary>
    public Task<LiveDerivationStep> StepAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, halt.Token);
        return Step(linked.Token);
    }, cancellationToken);

    /// <summary>Cancels a step still running and waits for it, so nothing uses the stores once the runner has returned.</summary>
    public async Task HaltAsync(Task<LiveDerivationStep>? running)
    {
        await halt.CancelAsync().ConfigureAwait(false);
        if (running is null)
        {
            return;
        }

        try
        {
            _ = await running.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            // The runner has already reported how the capture ended; a halted step's own outcome changes nothing.
        }
    }

    public void Dispose() => halt.Dispose();

    private LiveDerivationStep Step(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (follower is null)
        {
            SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
            if (evidence.Current is not { } source)
            {
                return new(null, null, null, null, TimeSpan.Zero, TimeSpan.Zero, null);
            }

            Directory.CreateDirectory(sessionPath);
            derived = SessionStore.Open(LocalOwnedDirectory.Open(sessionPath), source.SessionId, source.SourceIdentity);

            // One store per session in this process: the window's queries read through the store that publishes, so
            // the session's readers are cached, and each new file hashed, once rather than once per store.
            (stores ?? SharedSessionStores.Registry).Adopt(sessionPath, derived);
            follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: cancellationToken);
        }

        long followStarted = Stopwatch.GetTimestamp();
        FollowStep step = follower.CatchUp(cancellationToken: cancellationToken);

        // Between two mirrors, by the follow's own writer: the records read before the newest window go once they are due,
        // up to the earliest pin, and a session that outgrew what such a pin allows says the capture must stop.
        RollingStep? kept = rolling is not null && step.MirroredChunks > 0
            ? rolling.Step(derived!, DateTimeOffset.UtcNow, cancellationToken)
            : null;
        TimeSpan derivation = Stopwatch.GetElapsedTime(followStarted);
        Last = step;
        if (step.Finished && Checkpoint is null)
        {
            // The session holds everything the capture published, compacted, so nothing will replace its segments. The
            // generation it adds names the same evidence and is shown as the next one, taken from the same derivation.
            Checkpoint = SessionCheckpoints.Publish(derived!, DateTimeOffset.UtcNow, cancellationToken);
        }

        if (derived!.Current is not { } current || current.Generation == shownGeneration)
        {
            return new(step, null, null, null, derivation, TimeSpan.Zero, null, kept);
        }

        shownGeneration = current.Generation;
        TimeSpan generationAt = elapsed.Elapsed;
        try
        {
            long projectionStarted = Stopwatch.GetTimestamp();
            SessionOverviewBundle overview = SessionOverviewProjector.Project(
                derived, policy?.Invoke() ?? EvidencePolicy.IncludeCorrelated, cancellationToken: cancellationToken);
            return new(step, current.Generation, generationAt, overview, derivation,
                Stopwatch.GetElapsedTime(projectionStarted), null, kept);
        }
        catch (InvalidOperationException exception)
        {
            return new(step, current.Generation, generationAt, null, derivation, TimeSpan.Zero, exception.Message, kept);
        }
    }
}
