using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat processes (§7.2): each process instance with what witnessed it, its parent and terminal session where the source
/// named them, and what its evidence lacks, in the words a person reads them in; and what it lists written to a file.
/// </summary>
public sealed class ProcessesCommandTests
{
    [Fact(DisplayName = "§7.2: icat processes says a process's parent, terminal session and evidence gaps, how each instance was witnessed, the rest past --top, and writes what it lists to --output")]
    public async Task ProcessesSayTheirParentsSessionsAndGaps()
    {
        // parent.exe creates child.exe, in terminal session 1; orphan.exe names a parent the capture holds nothing of; svc.exe
        // was running when the capture began; PID 600 is seen only exiting; and PID 700 is created twice, no exit between.
        ObservationRowV1 child = Lifecycle(5, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\child.exe" };
        ObservationRowV1 orphan = Lifecycle(6, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\orphan.exe" };
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\parent.exe" },
            Lifecycle(2, ObservationKind.Inventory, 500, 2) with { ResourceName = @"C:\Tools\svc.exe" },
            child,
            orphan,
            Lifecycle(9, ObservationKind.Exit, 600, 5),
            Lifecycle(3, ObservationKind.Create, 700, 6) with { ResourceName = @"C:\Tools\first.exe" },
            Lifecycle(8, ObservationKind.Create, 700, 7) with { ResourceName = @"C:\Tools\second.exe" },
        ], fields:
        [
            Field(child, SourceField.ParentProcessId, 100),
            Field(child, SourceField.ProcessSessionId, 1),
            Field(orphan, SourceField.ParentProcessId, 999),
        ]);
        session.Store.ReleaseSegmentReaders();

        // Listed, each instance says how it was witnessed: created in the capture, running when it began, or first seen exiting.
        (InterCatExitCode code, string table, string said) = await Run("processes", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Witnessed +.*5 created in the capture", table);
        Assert.Contains("1 running at capture start", table, Ordinal);
        Assert.Contains("1 running before their first record", table, Ordinal);
        Assert.Matches(@"(?m)^  500 +svc\.exe +running at capture start ", table);
        Assert.Matches(@"(?m)^  600 +not witnessed +running before its first record, exited ", table);

        // A child names its parent's instance and its terminal session; one whose parent the capture holds nothing of says so.
        string detail = (await Run("processes", session.Path, "--pid", "300")).Output;
        Assert.Matches(@"(?m)^  Parent +PID 100, instance \S+ \([a-z ]+\)\r?$", detail);
        Assert.Matches(@"(?m)^  Session +1\r?$", detail);
        Assert.DoesNotContain("Evidence gaps", detail, Ordinal);
        Assert.Matches(@"(?m)^  Parent +PID 999, named by the source; no instance of it is in the capture\r?$",
            (await Run("processes", session.Path, "--pid", "400")).Output);

        // The PID's first holder, replaced with no exit seen, says that its exit is a gap in its evidence; the second does not.
        string reused = (await Run("processes", session.Path, "--pid", "700")).Output;
        Assert.Matches(@"(?m)^  700 #1 +first\.exe +created .*, replaced .* with no exit seen ", reused);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(reused, @"(?m)^  Evidence gaps +Exit not witnessed\r?$"));

        // Past --top, the rest are counted, and the way to list them said.
        Assert.Contains("  ... and 5 more. --top 0 lists every instance; --json always does.",
            (await Run("processes", session.Path, "--top", "2")).Output, Ordinal);

        // --output writes the document --json prints, into a folder it makes; a file there already is kept unless --overwrite.
        using var folder = new InterCat.Capture.Journal.Tests.TemporaryDirectory();
        string output = Path.Combine(folder.Path, "listed", "processes.json");
        (code, _, said) = await Run("processes", session.Path, "--output", output);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Process instances written to {output}.", said, Ordinal);
        Assert.Equal((await Run("processes", session.Path, "--json")).Output.TrimEnd(), File.ReadAllText(output).TrimEnd());
        File.WriteAllText(output, "kept");
        (code, _, said) = await Run("processes", session.Path, "--output", output);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{output} exists. Pass --overwrite to replace it.", said, Ordinal);
        Assert.Equal("kept", File.ReadAllText(output));
        (code, _, said) = await Run("processes", session.Path, "--output", output, "--overwrite");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.NotEqual("kept", File.ReadAllText(output));
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
