using System.Globalization;

namespace InterCat.Capture.Journal;

/// <summary>
/// The follow of a capture still recording (§9, ADR-027), which rides out a moment when one of its files cannot be read or
/// written - another program holding one just written, as a scanner or a backup can - rather than end the capture over it.
/// A follower moves on only past a chunk it published, so a pass that fails part way leaves it where its session is, and
/// the pass is tried again after each of the follow's pauses. A failure that outlasts them all is thrown, saying so; one
/// that is not about reading or writing a file is thrown at once.
/// </summary>
public sealed class LiveFollow
{
    /// <summary>The pauses before each try after a failure: 3.75 s in all, longer than a scanner holds a file it reads.</summary>
    public static IReadOnlyList<TimeSpan> DefaultPauses { get; } =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly Func<CancellationToken, LiveSessionFollower> open;
    private readonly IReadOnlyList<TimeSpan> pauses;
    private readonly Action<Exception>? failed;
    private LiveSessionFollower? follower;

    /// <param name="open">Opens the follower from what the derived session holds, as <see cref="LiveSessionFollower.Open"/> does.</param>
    /// <param name="pauses">The pauses before each try after a failure; <see cref="DefaultPauses"/> when null.</param>
    /// <param name="failed">Told of each failure the follow rides out, before its pause.</param>
    public LiveFollow(
        Func<CancellationToken, LiveSessionFollower> open,
        IReadOnlyList<TimeSpan>? pauses = null,
        Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        this.open = open;
        this.pauses = pauses ?? DefaultPauses;
        this.failed = failed;
    }

    /// <summary>Mirrors as <see cref="LiveSessionFollower.CatchUp"/> does, riding out a failure to read or write a file.</summary>
    public FollowStep CatchUp(int maximumChunks = int.MaxValue, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                follower ??= open(cancellationToken);
                return follower.CatchUp(maximumChunks, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == pauses.Count)
                {
                    TimeSpan waited = pauses.Aggregate(TimeSpan.Zero, (sum, pause) => sum + pause);
                    throw new IOException(
                        exception.Message.TrimEnd().TrimEnd('.') + ". It failed again each time the follow tried it, "
                        + (pauses.Count == 1 ? "once more" : pauses.Count.ToString(CultureInfo.InvariantCulture) + " more times")
                        + " over " + waited.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s.",
                        exception);
                }

                failed?.Invoke(exception);
                if (cancellationToken.WaitHandle.WaitOne(pauses[attempt]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
