using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat pin (store-v1 §8) as a person reads it: a pin placed with the allowance asked for, said in words with what it keeps
/// and what it does not; the pins standing listed; and what it cannot do refused before anything is written.
/// </summary>
public sealed class PinCommandTests
{
    [Fact(DisplayName = "§20.1: icat pin places a pin in words with the allowance asked for, says the content it does not keep, lists the pins standing, and refuses an identity two pins share, a reason past its bound and a session with no session time")]
    public async Task APinIsPlacedAndListedInWords()
    {
        // Four sends a second apart, the content of each kept.
        ObservationRowV1[] sends = [.. Enumerable.Range(0, 4).Select(index =>
            Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 17, 100, (ulong)(index + 1))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = index * 1_000_000_000L })];
        using var session = new TemporarySession();
        Publish(session.Store, sends,
            content: (ContentHeader(64), [.. sends.Select(row => Content(row, "Host: example.com"u8.ToArray()))]));
        session.Store.ReleaseSegmentReaders();

        // Placed from a second in, allowing 64 MiB: said in words, with what it keeps and allows, and that the content of the
        // records it keeps is restricted evidence a person may still release on its own.
        string allowed = ByteSizeText.Of(64L * 1024 * 1024);
        (InterCatExitCode code, string output, string said) = await Run(
            "pin", session.Path, "--from", "1 s", "--reason", "the incident", "--allow-mib", "64");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.StartsWith("PIN PLACED", output.TrimStart(), Ordinal);
        Assert.Matches(new Regex($@"(?m)^  Pin [0-9a-f]{{8}} +keeps every record from 1\.000 s · allows the session {Regex.Escape(allowed)} "
            + @"· placed \d{4}-\d\d-\d\d \d\d:\d\d UTC · the incident\r?$"), output);
        Assert.Contains($"stopping once the session holds more than {allowed}.", output, Ordinal);
        Assert.DoesNotContain("It allows the session what it holds now", output, Ordinal);
        Assert.Contains("A pin keeps records and their rows. The content kept of their messages is restricted evidence a person "
            + "may still release on its own, with icat retain --release-content.", output, Ordinal);
        RetentionPin placed = Assert.Single(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path)).Pins());
        Assert.Equal((1_000_000_000L, 64L * 1024 * 1024), (placed.FromNanoseconds, placed.AllowanceBytes));

        // Asked for with no change, the pins standing are listed in the same words.
        (code, output, said) = await Run("pin", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.StartsWith("PINS", output.TrimStart(), Ordinal);
        Assert.Matches($@"(?m)^  Pin {placed.ShortId} +keeps every record from 1\.000 s · ", output);

        // A reason past its bound is refused before anything is written.
        (code, _, said) = await Run("pin", session.Path, "--from", "2 s", "--reason", new string('x', RetentionPin.MaximumReasonLength + 1));
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"A pin's reason is at most {RetentionPin.MaximumReasonLength} characters; this one is "
            + $"{RetentionPin.MaximumReasonLength + 1:N0}.", said, Ordinal);

        // Pins placed until two share the first digit of their identities: that digit names both, so neither is removed.
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        while (store.Pins().GroupBy(pin => pin.ShortId[0]).All(digit => digit.Count() == 1))
        {
            _ = store.Pin(2_000_000_000L, 64L * 1024 * 1024, "another", DateTimeOffset.UtcNow);
        }

        RetentionPin[] shared = [.. store.Pins().GroupBy(pin => pin.ShortId[0]).Single(digit => digit.Count() > 1)];
        int standing = store.Pins().Count;
        (code, _, said) = await Run("pin", session.Path, "--remove", shared[0].ShortId[..1]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{shared[0].ShortId[..1]} names 2 pins of this session: ", said, Ordinal);
        Assert.Contains(". Give more of its identity.", said, Ordinal);
        Assert.All(shared, pin => Assert.Contains(pin.ShortId, said, Ordinal));
        Assert.Equal(standing, store.Pins().Count);

        // A session none of whose records has a session time has no moment a pin could keep records from.
        using var untimed = new TemporarySession();
        Publish(untimed.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 17, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        untimed.Store.ReleaseSegmentReaders();
        (code, _, said) = await Run("pin", untimed.Path, "--from", "1 s", "--reason", "nothing timed");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("No record of this session has a session time, so 1 s places nothing in it.", said, Ordinal);
        Assert.Empty(SessionStore.OpenExisting(LocalOwnedDirectory.Open(untimed.Path)).Pins());
    }

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
