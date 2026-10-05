using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>How icat lays out what a person reads (§20.4).</summary>
public sealed class ConsoleUiTests
{
    [Fact(DisplayName = "§20.4: a group of fields puts its values in one column, past its longest label, and keeps its place among other lines")]
    public void AGroupOfFieldsLinesUp()
    {
        // stdout and stderr are one writer here, so the order a person would see them in is the order they were written in.
        (string written, _) = Written(() =>
        {
            ConsoleUi.Field("Path", "/a");
            ConsoleUi.Field("Staging owner markers", "0");
            ConsoleUi.Field("Files", "1");
            ConsoleUi.Warn("Said on stderr, after the group before it.");
            ConsoleUi.Field("Path", "/b");
            ConsoleUi.Field("Generation", "2");
            ConsoleUi.Field("Metric", "what it means", width: 26);
            ConsoleUi.Note("A note ends a group too.");
            ConsoleUi.Field("Last", "written when the command ends");
        }, oneWriter: true);

        Assert.Equal(
            [
                "  Path                   /a",
                "  Staging owner markers  0",
                "  Files                  1",
                "! Said on stderr, after the group before it.",
                "  Path                      /b",
                "  Generation                2",
                "  Metric                    what it means",
                "  A note ends a group too.",
                "  Last                  written when the command ends",
            ],
            written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact(DisplayName = "§20.4: whatever else is written ends a group of fields first, so a report reads in the order it was written")]
    public void EveryOtherWriteEndsAGroup()
    {
        (Action Write, string Marker)[] writes =
        [
            (() => ConsoleUi.Heading("Heading"), "HEADING"),
            (() => ConsoleUi.Line("a line"), "a line"),
            (() => ConsoleUi.Bullet("a bullet"), "a bullet"),
            (() => ConsoleUi.Note("a note"), "a note"),
            (() => ConsoleUi.Progress("progress"), "progress"),
            (() => ConsoleUi.Warn("a warning"), "a warning"),
            (() => ConsoleUi.Failure("a failure"), "a failure"),
            (() => ConsoleUi.Success("a success"), "a success"),
            (() => ConsoleUi.Table(["a table"], []), "a table"),
            (() => ConsoleUi.Explain(() => ConsoleUi.Line("an explanation")), "an explanation"),
        ];
        foreach ((Action write, string marker) in writes)
        {
            (string both, _) = Written(() =>
            {
                ConsoleUi.Field("Before", "the field");
                write();
            }, oneWriter: true);
            int field = both.IndexOf("  Before", StringComparison.Ordinal);
            int after = both.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(field >= 0 && after > field, $"'{marker}' was written before the field it followed: {both}");
        }

        // A field written while help is explained on stderr goes there, and the one before it stays on stdout.
        (string output, string error) = Written(() =>
        {
            ConsoleUi.Field("Answer", "on stdout");
            ConsoleUi.Explain(() => ConsoleUi.Field("Explained", "on stderr"));
        }, oneWriter: false);
        Assert.Equal(("  Answer                on stdout", "  Explained             on stderr"), (output.Trim('\n', '\r'), error.Trim('\n', '\r')));
    }

    /// <summary>What <paramref name="write"/> writes to stdout and stderr, every field included.</summary>
    private static (string Output, string Error) Written(Action write, bool oneWriter)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(oneWriter ? answered : said);
        try
        {
            write();
            ConsoleUi.Flush();
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }

        return (answered.ToString(), said.ToString());
    }
}
