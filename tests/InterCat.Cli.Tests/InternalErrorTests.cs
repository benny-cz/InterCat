using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// An error nothing in icat mapped (§20.6 Internal) is a defect in InterCat. Where the process runs it is contained: said as
/// one, written whole for whoever fixes it, and answered with exit code 70, as the broker's is, never as an unexplained crash.
/// A test runs icat without it, so a defect fails the test that meets it.
/// </summary>
public sealed class InternalErrorTests
{
    private const string Said =
        "icat stopped on an error InterCat did not expect, a defect in InterCat rather than in what was asked. ";

    [Fact(DisplayName = "§20.6: an error nothing in icat mapped ends it with a sentence, the error written whole and exit code 70")]
    public async Task AnErrorNothingMappedEndsWithASentenceAndSeventy()
    {
        // Thrown before anything was awaited, and after.
        (InterCatExitCode code, string output, string said) = await Contained(() => throw new InvalidCastException("A row was read as the wrong kind"));
        Assert.Equal((InterCatExitCode.InternalError, 70, string.Empty), (code, (int)code, output));
        Assert.Contains(Said + "InvalidCastException: A row was read as the wrong kind. What went wrong follows, for whoever fixes it."
            + Environment.NewLine + "System.InvalidCastException: A row was read as the wrong kind" + Environment.NewLine + "   at ", said,
            StringComparison.Ordinal);

        (code, output, said) = await Contained(async () =>
        {
            await Task.Yield();
            throw new ArgumentException("The bound is past the clock's range", "interval");
        });
        Assert.Equal((InterCatExitCode.InternalError, string.Empty), (code, output));
        Assert.Contains(Said + "ArgumentException: The bound is past the clock's range (Parameter 'interval'). What went wrong",
            said, StringComparison.Ordinal);

        // What icat answered before it stopped stays answered, and nothing more is added to it.
        (code, output, said) = await Contained(async () =>
        {
            await Console.Out.WriteLineAsync("{ \"partial\": true");
            throw new FormatException("The moment has no time zone");
        });
        Assert.Equal((InterCatExitCode.InternalError, "{ \"partial\": true" + Environment.NewLine), (code, output));
        Assert.Contains(Said + "FormatException: The moment has no time zone. What went wrong", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§20.6: what icat mapped passes through unchanged, and running out of memory is not contained")]
    public async Task WhatIcatMappedPassesThrough()
    {
        foreach (InterCatExitCode mapped in Enum.GetValues<InterCatExitCode>().Where(value => value != InterCatExitCode.InternalError))
        {
            (InterCatExitCode code, string output, string said) = await Contained(() => Task.FromResult(mapped));
            Assert.Equal((mapped, string.Empty, string.Empty), (code, output, said));
        }

        // A real invocation runs as it would uncontained.
        (InterCatExitCode help, string answered, _) = await Contained(() => Icat.RunAsync(["--help"], CancellationToken.None));
        Assert.Equal(InterCatExitCode.Success, help);
        Assert.Contains("70 an error InterCat did not expect, a defect in InterCat itself.", answered, StringComparison.Ordinal);

        // Running out of memory, as a reservation that could not be met says, leaves nothing safe to say it with.
        await Assert.ThrowsAsync<InsufficientMemoryException>(
            () => Icat.RunContainedAsync(() => throw new InsufficientMemoryException("No room for the segment")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Icat.RunContainedAsync(null!));
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Contained(Func<Task<InterCatExitCode>> invocation)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunContainedAsync(invocation);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
