using System.Diagnostics;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>What one follow-and-project step found: the follow, and a new generation's overview when there was one.</summary>
/// <param name="Follow">The follow step, or null while the evidence session has published nothing to follow.</param>
/// <param name="Generation">The derived generation this step saw first, or null when it saw no new one.</param>
/// <param name="GenerationAt">When, from the user's start action, the step saw that generation.</param>
/// <param name="OverviewProblem">Why the new generation has no overview, when projection refused it.</param>
internal sealed record LiveDerivationStep(
    FollowStep? Follow,
    long? Generation,
    TimeSpan? GenerationAt,
    SessionOverviewBundle? Overview,
    TimeSpan Derivation,
    TimeSpan Projection,
    string? OverviewProblem);

/// <summary>
/// Follows one live capture's published evidence into the user's own session and projects each new generation
/// (ADR-027). It holds the follower and the derived store for the capture's life. Steps run one at a time on the thread
/// pool, off the loop that reads status and renews the owner lease, and a step's state is read only once it finished.
/// </summary>
internal sealed class LiveDerivation(string evidencePath, string sessionPath, Stopwatch elapsed) : IDisposable
{
    private readonly CancellationTokenSource halt = new();
    private LiveSessionFollower? follower;
    private SessionStore? derived;
    private long shownGeneration = -1;

    /// <summary>The last follow step, or null before the first.</summary>
    public FollowStep? Last { get; private set; }

    /// <summary>Whether the user's session holds a derived generation.</summary>
    public bool HasSession => derived?.Current is not null;

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
            follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: cancellationToken);
        }

        long followStarted = Stopwatch.GetTimestamp();
        FollowStep step = follower.CatchUp(cancellationToken: cancellationToken);
        TimeSpan derivation = Stopwatch.GetElapsedTime(followStarted);
        Last = step;
        if (derived!.Current is not { } current || current.Generation == shownGeneration)
        {
            return new(step, null, null, null, derivation, TimeSpan.Zero, null);
        }

        shownGeneration = current.Generation;
        TimeSpan generationAt = elapsed.Elapsed;
        try
        {
            long projectionStarted = Stopwatch.GetTimestamp();
            SessionOverviewBundle overview = SessionOverviewProjector.Project(derived, cancellationToken: cancellationToken);
            return new(step, current.Generation, generationAt, overview, derivation,
                Stopwatch.GetElapsedTime(projectionStarted), null);
        }
        catch (InvalidOperationException exception)
        {
            return new(step, current.Generation, generationAt, null, derivation, TimeSpan.Zero, exception.Message);
        }
    }
}
