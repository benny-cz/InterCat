using System.Runtime.CompilerServices;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The errors nothing mapped that a window contained (§20.6 Internal), counted for its support bundle rather than logged
/// (R11): each kind by its type and the methods it was thrown from, the first sixteen kinds listed and every error counted,
/// and never what one said, which may hold what a record holds (P16).
/// </summary>
public sealed class InternalErrorTallyTests
{
    [Fact(DisplayName = "§20.6: a contained error is counted by its kind and the methods it was thrown from, never by its words")]
    public void AnErrorIsCountedByItsKindAndWhereItWasThrown()
    {
        var tally = new InternalErrorTally();
        Assert.Equal((0L, 0), (tally.Counted().Contained, tally.Counted().Errors.Count));

        // Thrown twice from one place it is one kind, counted twice; from another place, or of another type, another kind.
        Action castRow = () => ThrowsCast("Row 7 of alice's leaky-tool.exe sent to 10.99.98.97:50505");
        for (int twice = 0; twice < 2; twice++)
        {
            tally.Count(Caught(castRow));
        }

        tally.Count(Caught(() => ThrowsFormat("The moment 2026-10-30 has no time zone")));
        tally.Count(Caught(() => ThrowsCastElsewhere("pipe \\\\.\\pipe\\secret-channel")));
        SupportErrors counted = tally.Counted();
        Assert.Equal(4, counted.Contained);
        Assert.Equal(
            [
                ("System.InvalidCastException", 2L, "ThrowsCast"),
                ("System.FormatException", 1L, "ThrowsFormat"),
                ("System.InvalidCastException", 1L, "ThrowsCastElsewhere"),
            ],
            counted.Errors.Select(error => (error.Kind, error.Count, error.ThrownFrom[0].Split('.')[^1])));
        Assert.Equal("InterCat.Application.Tests.InternalErrorTallyTests.ThrowsCast", counted.Errors[0].ThrownFrom[0]);
        Assert.Equal("InterCat.Application.Tests.InternalErrorTallyTests.Caught", counted.Errors[0].ThrownFrom[^1]);

        // What is counted is a copy: counting on changes the tally, not what was read of it.
        tally.Count(Caught(castRow));
        Assert.Equal((4L, 2L), (counted.Contained, counted.Errors[0].Count));
        Assert.Equal((5L, 3L), (tally.Counted().Contained, tally.Counted().Errors[0].Count));

        // A bundle holds them by kind and method, and none of what they said, and its listing says it does; one with
        // nothing counted, as icat support's, holds none and says none.
        SupportBundleV1 made = SupportBundle.Make([], capabilities: null, tally.Counted());
        Assert.Equal(SupportBundle.Holds(0, capabilities: false, unexpectedErrors: true), made.Holds);
        SupportBundleV1 uncounted = SupportBundle.Make([], capabilities: null, unexpectedErrors: null);
        Assert.Null(uncounted.UnexpectedErrors);
        Assert.Equal(SupportBundle.Holds(0, capabilities: false, unexpectedErrors: false), uncounted.Holds);
        string written = SupportBundle.Serialize(made);
        Assert.Contains("\"unexpectedErrors\": {", written, StringComparison.Ordinal);
        Assert.Contains("\"contained\": 5", written, StringComparison.Ordinal);
        Assert.Contains("\"InterCat.Application.Tests.InternalErrorTallyTests.ThrowsFormat\"", written, StringComparison.Ordinal);
        foreach (string said in new[] { "alice", "leaky-tool", "10.99.98.97", "time zone", "secret-channel" })
        {
            Assert.DoesNotContain(said, written, StringComparison.OrdinalIgnoreCase);
        }

        // An exception never thrown is counted by its kind, thrown from nowhere.
        tally.Count(new InvalidOperationException("never thrown"));
        SupportError unthrown = tally.Counted().Errors[^1];
        Assert.Equal(("System.InvalidOperationException", 1L), (unthrown.Kind, unthrown.Count));
        Assert.Empty(unthrown.ThrownFrom);
        Assert.Throws<ArgumentNullException>(() => tally.Count(null!));
    }

    [Fact(DisplayName = "§20.6: an error thrown in a method the compiler rewrote is placed by the method as it was written")]
    public async Task AnErrorIsPlacedByTheMethodAsWritten()
    {
        // An async method runs in a state machine the compiler made for it, which is named after it.
        var tally = new InternalErrorTally();
        tally.Count((await Record.ExceptionAsync(ThrowsLaterAsync))!);
        Assert.Equal("InterCat.Application.Tests.InternalErrorTallyTests.ThrowsLaterAsync", tally.Counted().Errors[0].ThrownFrom[0]);

        // An async lambda's state machine is named after the lambda, which is named after the method it is written in.
        Func<Task> written = async () =>
        {
            await Task.Yield();
            throw new TimeoutException("in a lambda");
        };
        tally.Count((await Record.ExceptionAsync(written))!);
        string lambda = tally.Counted().Errors[1].ThrownFrom[0];
        Assert.StartsWith("InterCat.Application.Tests.InternalErrorTallyTests.<AnErrorIsPlacedByTheMethodAsWritten>b__",
            lambda, StringComparison.Ordinal);

        // A generic type is named as it is declared, never by what it was made of.
        tally.Count(Caught(Generic<int>.Throws));
        Assert.Equal("InterCat.Application.Tests.InternalErrorTallyTests+Generic`1.Throws", tally.Counted().Errors[2].ThrownFrom[0]);
    }

    [Fact(DisplayName = "§20.6: a tally lists the first sixteen kinds of error and counts every one, each by its innermost thirty-two methods")]
    public void ATallyIsBounded()
    {
        // Seventeen kinds thrown from one place: the first sixteen are listed, and all seventeen counted.
        Func<string, Exception>[] kinds =
        [
            words => new InvalidCastException(words), words => new FormatException(words), words => new TimeoutException(words),
            words => new NotSupportedException(words), words => new InvalidOperationException(words),
            words => new ArgumentException(words), words => new KeyNotFoundException(words),
            words => new DivideByZeroException(words), words => new OverflowException(words),
            words => new ArithmeticException(words), words => new NotImplementedException(words), words => new IOException(words),
            words => new FileNotFoundException(words), words => new DirectoryNotFoundException(words),
            words => new EndOfStreamException(words), words => new PlatformNotSupportedException(words),
            words => new UnauthorizedAccessException(words),
        ];
        var tally = new InternalErrorTally();
        Exception ThrownAs(Func<string, Exception> kind) => Caught(() => Throws(kind("one of seventeen")));
        foreach (Func<string, Exception> kind in kinds)
        {
            tally.Count(ThrownAs(kind));
        }

        Assert.Equal((17L, InternalErrorTally.MostListed), (tally.Counted().Contained, tally.Counted().Errors.Count));
        Assert.Equal("System.InvalidCastException", tally.Counted().Errors[0].Kind);
        Assert.Equal("System.PlatformNotSupportedException", tally.Counted().Errors[^1].Kind);

        // One listed counts on; the seventeenth again is counted in all, and listed no more than before.
        tally.Count(ThrownAs(kinds[0]));
        tally.Count(ThrownAs(kinds[^1]));
        Assert.Equal((19L, 16, 2L), (tally.Counted().Contained, tally.Counted().Errors.Count, tally.Counted().Errors[0].Count));

        // A deep error keeps the innermost of the methods it was thrown from, where it was thrown.
        var deep = new InternalErrorTally();
        deep.Count(Caught(() => _ = Recurse(40)));
        IReadOnlyList<string> thrownFrom = deep.Counted().Errors.Single().ThrownFrom;
        Assert.Equal(InternalErrorTally.MostFrames, thrownFrom.Count);
        Assert.All(thrownFrom, method => Assert.Equal("InterCat.Application.Tests.InternalErrorTallyTests.Recurse", method));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Caught(Action act)
    {
        try
        {
            act();
        }
        catch (Exception thrown) when (thrown is not OutOfMemoryException)
        {
            return thrown;
        }

        throw new InvalidOperationException("Nothing was thrown.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowsCast(string words) => throw new InvalidCastException(words);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowsCastElsewhere(string words) => throw new InvalidCastException(words);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowsFormat(string words) => throw new FormatException(words);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throws(Exception exception) => throw exception;

    /// <summary>A recursion that is never a tail call, so each level keeps its frame in every build.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth) => depth == 0 ? throw new InvalidCastException("at the bottom") : Recurse(depth - 1) + 1;

    private static async Task ThrowsLaterAsync()
    {
        await Task.Yield();
        throw new TimeoutException("later");
    }

    private static class Generic<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throws() => throw new NotSupportedException(typeof(T).Name);
    }
}
