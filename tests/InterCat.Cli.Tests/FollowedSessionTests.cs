using InterCat.Analysis.Tests;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A session a capture is being followed into is its follow's alone to publish into: a generation published beside the
/// follow makes the follow's next publication fail, which ends it (ADR-044). So icat checkpoint, compact, rederive and
/// retain refuse to publish there, saying what can be done instead, and what only measures goes ahead.
/// </summary>
public sealed class FollowedSessionTests : IDisposable
{
    private const string Followed = "right now, and only its follow publishes into it: anything published beside it would end "
        + "the follow. ";

    private readonly TemporarySession session = new();

    public FollowedSessionTests()
    {
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 10_000_000 },
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 11)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 30_000_000 },
        ]);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "§20.2: a session a capture is being followed into is refused to every command that would publish into it, and measured as asked")]
    public async Task AFollowedSessionIsItsFollowsToPublishInto()
    {
        Func<string, bool> detected = FollowedSessions.IsFollowed;
        FollowedSessions.IsFollowed = path => string.Equals(path, session.Path, StringComparison.Ordinal);
        long published = Generation();
        try
        {
            foreach ((string[] args, string instead) in new (string[], string)[]
            {
                (["checkpoint", session.Path], "Its follow publishes the checkpoint when it finishes."),
                (["compact", session.Path], "Its follow compacts what it publishes as it goes: compact the session once its "
                    + "capture has finished, or measure what compacting would do now with --check."),
                (["rederive", session.Path], "Re-derive the session once its capture has finished, or see what a re-derivation "
                    + "would change now with --check."),
                (["retain", session.Path, "--release-before", "2s", "--confirm", "--reason", "room for the rest"],
                    "Keep what matters with icat pin, which every release keeps to; release from the session once its capture "
                    + "has finished, or see what a release would give up now without --confirm."),
            })
            {
                (InterCatExitCode code, string output, string said) = await Run(args);
                string invocation = "icat " + string.Join(' ', args);
                Assert.True(code == InterCatExitCode.PermissionOrCapabilityFailure && output.Length == 0, $"{invocation} exited {code}: {said}");
                Assert.Contains($"A capture is being followed into {session.Path} {Followed}{instead}", said, StringComparison.Ordinal);
                Assert.Equal(published, Generation());
            }

            // What only measures publishes nothing, so the follow is no reason to refuse it, whatever else it finds; a test
            // session has no journal plan to re-derive from, which a check says for itself.
            foreach (string[] args in new string[][]
            {
                ["compact", session.Path, "--check"], ["rederive", session.Path, "--check"],
                ["retain", session.Path, "--release-before", "2s"],
            })
            {
                (InterCatExitCode code, _, string said) = await Run(args);
                Assert.False(said.Contains(Followed, StringComparison.Ordinal), $"icat {string.Join(' ', args)} exited {code}: {said}");
                Assert.Equal(published, Generation());
            }

            // Once no follow holds the session, it is published into as before.
            FollowedSessions.IsFollowed = _ => false;
            (InterCatExitCode checkpointed, _, string why) = await Run("checkpoint", session.Path);
            Assert.True(checkpointed == InterCatExitCode.Success, why);
            Assert.True(Generation() > published);
        }
        finally
        {
            FollowedSessions.IsFollowed = detected;
        }
    }

    [Fact(DisplayName = "§20.2: a session is followed while its follow holds its ticket, and not without one, nor once the follow let it go")]
    public void ASessionIsFollowedWhileItsTicketIsHeld()
    {
        // No ticket, no follow.
        Assert.False(FollowedSessions.IsFollowed(session.Path));
        LiveFollowTicket ticket = LiveFollowTicket.For(Guid.NewGuid(), session.Path + "-evidence", session.Path,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30));
        try
        {
            using (ticket.Hold())
            {
                // Only Windows enforces the hold between two handles in one process, so elsewhere it cannot be told here.
                if (OperatingSystem.IsWindows())
                {
                    Assert.True(FollowedSessions.IsFollowed(session.Path));
                }
            }

            // A follow that let its ticket go without completing it was interrupted: its session is finished, not followed.
            Assert.True(File.Exists(LiveFollowTicket.PathFor(session.Path)));
            Assert.False(FollowedSessions.IsFollowed(session.Path));
        }
        finally
        {
            _ = LiveFollowTicket.Remove(session.Path);
        }
    }

    private long Generation() => SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path)).Current!.Generation;

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
