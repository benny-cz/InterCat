using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat programs (§6.3, R18): the programs whose processes talked to each other, every pair of them the busiest first,
/// each named by which program's processes connected to which, and with --program the pairs one program is in, as the
/// window's inspector lists them for a chosen program.
/// </summary>
public sealed class ProgramsCommandTests
{
    [Fact(DisplayName = "R18: icat programs lists every pair of programs the session's relationships join, the busiest first, as many as asked")]
    public async Task IcatProgramsListsEveryPairOfPrograms()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("programs", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Pairs +3 pairs of programs, among 3 programs\r?$", text);
        Assert.Matches(@"(?m)^  Pair of programs +Relationships +Processes +Over +Records\r?$", text);
        Assert.Equal(
            ["browser.exe → server.exe | 3 | 4 | TCP at 8080 | 12", "browser.exe → browser.exe | 2 | 3 | TCP at 9222 | 8",
                "server.exe → helper.exe | 1 | 2 | TCP at 7000 | 4"],
            Rows(text));
        Assert.Contains("Each program is counted by its executable, and one named at both ends is its own processes connecting "
            + "to each other.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Contains("seen opening none. Over names the ports the connections arrived at, at the program connected to. "
            + "Processes counts", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("more, quieter", text, Ordinal);

        // As many as asked, and how many more there are.
        (code, text, said) = await Run("programs", session.Path, "--limit", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(["browser.exe → server.exe | 3 | 4 | TCP at 8080 | 12"], Rows(text));
        Assert.Contains("2 more, quieter; --limit 3 lists every one.", text, Ordinal);
        (code, text, said) = await Run("programs", session.Path, "--limit", "3");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(3, Rows(text).Length);
        Assert.DoesNotContain("more, quieter", text, Ordinal);

        // The JSON lists the same, with how many there are in all and which program's processes made the connections.
        (code, text, said) = await Run("programs", session.Path, "--limit", "2", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("program-pairs-v1", root.GetProperty("contract").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("program").ValueKind);
        Assert.Equal(3, root.GetProperty("pairs").GetInt32());
        JsonElement first = root.GetProperty("listed")[0];
        Assert.Equal(2, root.GetProperty("listed").GetArrayLength());
        Assert.Equal(("browser.exe", @"C:\Apps\browser.exe", "server.exe", "browser.exe → server.exe"),
            (first.GetProperty("first").GetProperty("name").GetString(), first.GetProperty("first").GetProperty("path").GetString(),
                first.GetProperty("second").GetProperty("name").GetString(), first.GetProperty("joined").GetString()));
        Assert.Equal((3, 4, 12L, 3, 0), (first.GetProperty("relationships").GetInt32(), first.GetProperty("processes").GetInt32(),
            first.GetProperty("records").GetInt64(), first.GetProperty("firstConnected").GetInt32(),
            first.GetProperty("secondConnected").GetInt32()));
        Assert.Equal(["TCP"], first.GetProperty("mechanisms").EnumerateArray().Select(mechanism => mechanism.GetString()));
        Assert.Equal([8080], first.GetProperty("acceptedPorts").EnumerateArray().Select(port => port.GetInt32()));
    }

    [Fact(DisplayName = "R18: icat programs --program lists the pairs one program is in, named as listed, by its executable's file name with or without its extension, or by its path")]
    public async Task IcatProgramsListsThePairsOneProgramIsIn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        foreach (string named in new[] { "server.exe", "SERVER.EXE", "server", @"C:\Apps\server.exe" })
        {
            (InterCatExitCode code, string text, string said) = await Run("programs", session.Path, "--program", named);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("PROGRAMS SERVER.EXE TALKED WITH", text, Ordinal);
            Assert.Matches(@"(?m)^  Program +server\.exe · 1 process\r?$", text);
            Assert.Matches(@"(?m)^  Executable +C:\\Apps\\server\.exe\r?$", text);
            Assert.Matches(@"(?m)^  Talked with +2 other programs\r?$", text);
            Assert.Equal(["browser.exe → server.exe | 3 | 4 | TCP at 8080 | 12", "server.exe → helper.exe | 1 | 2 | TCP at 7000 | 4"],
                Rows(text));
        }

        // A program whose processes connected to each other too says so.
        (InterCatExitCode own, string browser, _) = await Run("programs", session.Path, "--program", "browser.exe");
        Assert.Equal(InterCatExitCode.Success, own);
        Assert.Matches(@"(?m)^  Program +browser\.exe · 3 processes\r?$", browser);
        Assert.Matches(@"(?m)^  Talked with +1 other program, and its own processes with each other\r?$", browser);

        // One with no relationship lists none and says where its connections are.
        (InterCatExitCode quiet, string idle, _) = await Run("programs", session.Path, "--program", "idle.exe");
        Assert.Equal(InterCatExitCode.Success, quiet);
        Assert.Matches(@"(?m)^  Program +idle\.exe · 1 process\r?$", idle);
        Assert.Contains("No relationship joins idle.exe's processes to a process of this session.", idle, Ordinal);
        Assert.Empty(Rows(idle));

        // Two of one file name in different folders are listed apart, so the file name alone names neither; the name as
        // listed names one.
        (InterCatExitCode refused, string output, string error) = await Run("programs", session.Path, "--program", "git.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Empty(output);
        Assert.Contains("--program git.exe names 2 programs - git.exe (bin) and git.exe (cmd) - so name one as they are listed.",
            error, Ordinal);
        (InterCatExitCode chosen, string git, _) = await Run("programs", session.Path, "--program", "git.exe (cmd)");
        Assert.Equal(InterCatExitCode.Success, chosen);
        Assert.Matches(@"(?m)^  Executable +C:\\Tools\\cmd\\git\.exe\r?$", git);

        // A name no program has is refused, and so is none; the JSON names the program asked about.
        (refused, output, error) = await Run("programs", session.Path, "--program", "nothing.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Empty(output);
        Assert.Contains("No program of this session is named nothing.exe: a program is named by its executable's file name", error, Ordinal);

        // One a letter or two from a program's name, with its extension or without, is answered with it, and with each
        // program as near where two are.
        (refused, output, error) = await Run("programs", session.Path, "--program", "brwoser");
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (refused, output));
        Assert.Contains("No program of this session is named brwoser; did you mean browser.exe?", error, Ordinal);
        (_, _, error) = await Run("programs", session.Path, "--program", "gti.exe");
        Assert.Contains("No program of this session is named gti.exe; did you mean git.exe (bin) or git.exe (cmd)?", error, Ordinal);
        (refused, _, error) = await Run("programs", session.Path, "--program", " ");
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Contains("--program takes a program's name", error, Ordinal);
        string nowhere = Path.Combine(Path.GetTempPath(), "icat-programs-" + Guid.NewGuid().ToString("N"));
        (refused, output, error) = await Run("programs", nowhere);
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Empty(output);
        Assert.Contains($"No session directory at {nowhere}.", error, Ordinal);
        (refused, _, error) = await Run("programs", session.Path, "--limit", "0");
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Contains("--limit expects a number of pairs of 1 or more; '0' is not one.", error, Ordinal);
        (InterCatExitCode written, string json, string why) = await Run("programs", session.Path, "--program", "helper", "--json");
        Assert.True(written == InterCatExitCode.Success, why);
        JsonElement program = JsonDocument.Parse(json).RootElement.GetProperty("program");
        Assert.Equal(("helper.exe", @"C:\Apps\helper.exe", 1), (program.GetProperty("name").GetString(),
            program.GetProperty("path").GetString(), program.GetProperty("processes").GetInt32()));
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("pairs").GetInt32());
    }

    [Fact(DisplayName = "R18: icat programs says no program talked with another where no relationship joins two processes")]
    public async Task IcatProgramsSaysNoneTalked()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 10)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
        ]);
        session.Store.ReleaseSegmentReaders();
        (InterCatExitCode code, string text, string said) = await Run("programs", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No relationship joins two processes of this session, so no program talked with another.", text, Ordinal);
        Assert.DoesNotContain("Pair of programs", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat programs names no port where the capture saw none of a pair's connections open")]
    public async Task IcatProgramsNamesNoPortWhereNoOpenWasSeen()
    {
        // The browser's send reached the server, whose receive recorded it, and neither end was seen opening the connection.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 400, 2) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 110 },
            .. Connection(20, 300, 400, "127.0.0.1:50000", "127.0.0.1:8080").Skip(2),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("programs", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(["browser.exe and server.exe | 1 | 2 | TCP | 2"], Rows(text));
        Assert.DoesNotContain("Over names the ports", text, Ordinal);
        (code, text, said) = await Run("programs", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Empty(JsonDocument.Parse(text).RootElement.GetProperty("listed")[0].GetProperty("acceptedPorts").EnumerateArray());
    }

    /// <summary>
    /// Three of browser.exe's processes each connect to the server, and two of them to the first; the server connects to
    /// the helper. idle.exe and two git.exe, in different folders, have no relationship.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 301, 2) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 302, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 130 },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Apps\helper.exe", SessionRelativeTicks = 140 },
        Lifecycle(6, ObservationKind.Create, 600, 6) with { ResourceName = @"C:\Apps\idle.exe", SessionRelativeTicks = 150 },
        Lifecycle(7, ObservationKind.Create, 700, 7) with { ResourceName = @"C:\Tools\cmd\git.exe", SessionRelativeTicks = 160 },
        Lifecycle(8, ObservationKind.Create, 701, 8) with { ResourceName = @"C:\Tools\bin\git.exe", SessionRelativeTicks = 170 },
        .. Connection(20, 300, 400, "127.0.0.1:50000", "127.0.0.1:8080"),
        .. Connection(30, 301, 400, "127.0.0.1:50001", "127.0.0.1:8080"),
        .. Connection(40, 302, 400, "127.0.0.1:50002", "127.0.0.1:8080"),
        .. Connection(50, 301, 300, "127.0.0.1:50003", "127.0.0.1:9222"),
        .. Connection(60, 302, 300, "127.0.0.1:50004", "127.0.0.1:9222"),
        .. Connection(70, 400, 500, "127.0.0.1:50005", "127.0.0.1:7000"),
    ];

    /// <summary>One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened at both ends, and a send across it.</summary>
    private static ObservationRowV1[] Connection(long ticks, int maker, int taker, string from, string to) =>
    [
        Transfer(ticks, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker, (ulong)ticks).Between(from, to)
            with { SessionRelativeTicks = ticks * 100 },
        Transfer(ticks + 1, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, taker, (ulong)ticks + 1).Between(to, from)
            with { SessionRelativeTicks = (ticks + 1) * 100 },
        Transfer(ticks + 2, ObservationKind.Send, AccountingSide.SendSide, 64, maker, (ulong)ticks + 2).Between(from, to)
            with { SessionRelativeTicks = (ticks + 2) * 100 },
        Transfer(ticks + 3, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, taker, (ulong)ticks + 3).Between(to, from)
            with { SessionRelativeTicks = (ticks + 3) * 100 },
    ];

    /// <summary>The table's rows, each pair with its relationships, processes, what they ran over and their records.</summary>
    private static string[] Rows(string text) =>
        [.. Regex.Matches(text, @"(?m)^  (\S+ (?:→|⇄|and) \S+) +(\d+) +(\d+) +(\S+(?: \S+)*) +(\d+)\r?$")
            .Select(match => string.Join(" | ", match.Groups.Values.Skip(1).Select(group => group.Value)))];

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
