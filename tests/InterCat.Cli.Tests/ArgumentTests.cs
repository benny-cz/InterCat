using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>How icat reads its arguments: options in any order, operands that look like options, and verbs.</summary>
public sealed class ArgumentTests
{
    [Fact(DisplayName = "R18: an option keeps its value wherever it is written, and an operand may start with a dash")]
    public void AnOptionKeepsItsValueWhereverItIsWritten()
    {
        // Before the session or after it, an option's value is its own: options are read first.
        foreach (string[] args in new[]
        {
            new[] { "--metric", "observations", "session", "--json" },
            ["session", "--metric", "observations", "--json"],
            ["--json", "session", "--metric", "observations"],
        })
        {
            var reader = new CommandLine(args);
            Assert.Equal("observations", reader.TakeOption("--metric"));
            Assert.True(reader.TryTakeFlag("--json"));
            Assert.Equal("session", reader.TakePositional());
            Assert.False(reader.TryReportUnknown(out _));
        }

        // A negative number is an operand, such as a view that starts before the investigation's epoch.
        var view = new CommandLine(["view", "workspace", "early", "-5", "-.5", "10"]);
        Assert.Equal(["view", "workspace", "early", "-5", "-.5", "10"], Enumerable.Range(0, 6).Select(_ => view.TakePositional()));
        Assert.Null(view.TakePositional());

        // After a bare --, every argument is an operand: a note that starts with a dash, or one that reads as an option.
        var note = new CommandLine(["note", "workspace", "--json", "--", "-> retry storm", "--json"]);
        Assert.True(note.TryTakeFlag("--json"));
        Assert.Equal(["note", "workspace", "-> retry storm", "--json"], Enumerable.Range(0, 4).Select(_ => note.TakePositional()));
        Assert.False(note.TryReportUnknown(out _));

        // What is left is reported, an option before an operand.
        var stray = new CommandLine(["-x", "--", "extra"]);
        Assert.True(stray.TryReportUnknown(out string? unknown));
        Assert.Equal("-x", unknown);
        Assert.Equal("extra", stray.TakePositional());
        Assert.True(new CommandLine(["--", "extra"]).TryReportUnknown(out string? leftover));
        Assert.Equal("extra", leftover);
    }

    [Fact(DisplayName = "R18: a verb is read first, and no option is read after an operand")]
    public void AVerbIsReadFirst()
    {
        // A verb decides which options follow, so it is the first argument, and an option's value never is.
        var measure = new CommandLine(["udp", "--seed", "5"]);
        Assert.Equal("udp", measure.TakeVerb());
        Assert.Equal(5, measure.TakeIntegerOption("--seed"));
        Assert.Null(new CommandLine(["--seed", "5", "tcp"]).TakeVerb());
        Assert.Null(new CommandLine([]).TakeVerb());

        // Reading an option after an operand could have read its value as the operand, so it is a mistake in icat itself.
        var late = new CommandLine(["session", "--rows", "2"]);
        Assert.Equal("session", late.TakePositional());
        Assert.Contains("read every option first",
            Assert.Throws<InvalidOperationException>(() => late.TakeOption("--rows")).Message, StringComparison.Ordinal);
    }
}
