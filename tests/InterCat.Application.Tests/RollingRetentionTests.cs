using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §20.2's rolling retention of a session a follow derives (S5, ADR-043): it keeps the newest window of session time, and
/// releases the oldest records only once the session holds a quarter more, a quarter apart, saying why.
/// </summary>
public sealed class RollingRetentionTests
{
    [Fact(DisplayName = "S5: rolling retention keeps the newest window of session time, releasing the oldest records a quarter of it apart")]
    public void RollingRetentionKeepsTheNewestWindow()
    {
        using var session = new TemporarySession();

        // Four chunks of two sends a second apart: at 0 and 1 s, 2 and 3 s, 4 and 5 s, 6 and 7 s.
        for (int chunk = 0; chunk < 4; chunk++)
        {
            Publish(session.Store, Sends(chunk * 2_000, (chunk * 2_000) + 1_000));
        }

        var rolling = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(2)));
        Assert.Equal(("the last 2 seconds", TimeSpan.FromMilliseconds(500)), (rolling.Policy.Window, rolling.Policy.Slack));

        // Seven seconds held is more than two and a half: the records read before 5 s go, a whole chunk at a time, so the
        // two chunks wholly before it; the one holding 5 s stays whole, and so does what the newest chunk rests on.
        IntervalReleaseResult first = Assert.IsType<IntervalReleaseResult>(rolling.Step(session.Store, Committed));
        Assert.Equal((5_000_000_000L, 3_000_000_001L, 2), (first.Preview.RequestedNanoseconds, first.Preview.BoundaryNanoseconds!.Value,
            first.Preview.ReleasedUnits));
        Assert.Equal("rolling retention keeps the last 2 seconds", session.Store.Current!.Retention!.Reason);
        Assert.Equal(1, rolling.Releases);

        // Nothing more until the session has grown by a quarter of the window: not at once, nor at 7.3 s, though the chunk
        // at 4 and 5 s now lies wholly before the newest window.
        long generation = session.Store.Current.Generation;
        Assert.Null(rolling.Step(session.Store, Committed));
        Assert.Equal(generation, session.Store.Current.Generation);
        Publish(session.Store, Sends(7_300));
        generation = session.Store.Current!.Generation;
        Assert.Null(rolling.Step(session.Store, Committed));
        Assert.Equal(generation, session.Store.Current.Generation);

        // Past 7.5 s: the records read before 7 s go, which is the chunk at 4 and 5 s.
        Publish(session.Store, Sends(8_000, 9_000));
        IntervalReleaseResult second = Assert.IsType<IntervalReleaseResult>(rolling.Step(session.Store, Committed));
        Assert.Equal((7_000_000_000L, 5_000_000_001L, 1), (second.Preview.RequestedNanoseconds, second.Preview.BoundaryNanoseconds!.Value,
            second.Preview.ReleasedUnits));
        Assert.Equal(2, rolling.Releases);
    }

    [Fact(DisplayName = "S5: rolling retention leaves a session within its window, and one whose oldest chunk reaches into it, as they are")]
    public void RollingRetentionReleasesOnlyWhatIsDue()
    {
        // Within its window: three seconds held, ten kept.
        using var short_ = new TemporarySession();
        Publish(short_.Store, Sends(0, 1_000));
        Publish(short_.Store, Sends(2_000, 3_000));
        var tenSeconds = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(10)));
        Assert.Null(tenSeconds.Step(short_.Store, Committed));
        Assert.Null(short_.Store.Current!.Retention);

        // Past its window but not yet a quarter past it: 2.3 s held, two kept, though the chunk at 0 and 0.2 s lies wholly
        // before the newest two seconds.
        using var barely = new TemporarySession();
        Publish(barely.Store, Sends(0, 200));
        Publish(barely.Store, Sends(2_300));
        Assert.Null(new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(2))).Step(barely.Store, Committed));
        Assert.Null(barely.Store.Current!.Retention);

        // A session released just now, by a follow before this one: from its boundary at 3 s it holds 2.4 s, so a fresh
        // policy leaves it, though its oldest record is 5.4 s old and the chunk at 3.2 s lies before the newest window.
        using var released = new TemporarySession();
        Publish(released.Store, Sends(0, 1_000));
        Publish(released.Store, Sends(2_000, 3_000));
        Publish(released.Store, Sends(3_200));
        Publish(released.Store, Sends(5_400));
        Assert.Equal(3_000_000_001L, IntervalRelease.Release(released.Store, 3_100_000_000, "rolling retention keeps the last 2 seconds",
            Committed).Preview.BoundaryNanoseconds);
        long generation = released.Store.Current!.Generation;
        Assert.Null(new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(2))).Step(released.Store, Committed));
        Assert.Equal(generation, released.Store.Current.Generation);

        // Past its window, but its oldest chunk holds a record read at 6 s, inside it, and a chunk is released whole.
        using var reaching = new TemporarySession();
        Publish(reaching.Store, Sends(0, 6_000));
        Publish(reaching.Store, Sends(7_000));
        var twoSeconds = new RollingRetention(new RollingRetentionPolicy(TimeSpan.FromSeconds(2)));
        Assert.Null(twoSeconds.Step(reaching.Store, Committed));
        Assert.Null(reaching.Store.Current!.Retention);
        Assert.Equal(0, twoSeconds.Releases);

        // A window is a second to a day.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingRetentionPolicy(TimeSpan.FromMilliseconds(999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingRetentionPolicy(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1)));
        int[] windows = [3_600, 10_800, 60, 600, 1, 90];
        Assert.Equal(["the last hour", "the last 3 hours", "the last minute", "the last 10 minutes", "the last second", "the last 90 seconds"],
            windows.Select(seconds => new RollingRetentionPolicy(TimeSpan.FromSeconds(seconds)).Window));
    }

    /// <summary>One connection's sends from PID 100 at the given milliseconds of session time.</summary>
    private static ObservationRowV1[] Sends(params int[] milliseconds) =>
    [
        .. milliseconds.Select(millisecond => Transfer(millisecond * 10_000L, ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(millisecond + 1))
            .Between("127.0.0.1:50000", "10.0.0.9:443") with { SessionRelativeTicks = millisecond * 1_000_000L }),
    ];
}
