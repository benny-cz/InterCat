using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat channels (§7.1, R18): pages of a session's paired channels, the busiest first, each named by the processes at its two
/// ends, as the window's channel browser lists them.
/// </summary>
public sealed class ChannelsCommandTests
{
    [Fact(DisplayName = "R18: icat channels pages the busiest channels first, channels of as many records by key, and its JSON names that order")]
    public async Task IcatChannelsListsTheBusiestFirst()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // The six records between PIDs 300 and 400 lead, then the two channels of two records each, and the list says so.
        (InterCatExitCode code, string text, string said) = await Run("channels", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Channels in scope +3\r?$", text);
        Assert.Matches(@"(?m)^  Ranked by +the records at their two ends, the busiest first\r?$", text);
        Assert.Equal([6, 2, 2], Regex.Matches(text, @"(?m) · (\d+) observed records\r?$")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        Assert.Matches(@"(?m)^  PID \d00 and PID \d00 · TCP · :(?:50001|9090) ↔ :(?:50001|9090) on 127\.0\.0\.1 · 6 observed records\r?$",
            text);

        // As JSON, the same order, the two of two records by key, under the contract that names it.
        (code, text, said) = await Run("channels", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("channel-page-v2", root.GetProperty("contract").GetString());
        JsonElement[] listed = [.. root.GetProperty("page").GetProperty("channels").EnumerateArray()];
        Assert.Equal([6L, 2L, 2L], listed.Select(channel => channel.GetProperty("observationCount").GetInt64()));
        string[] keys = [.. listed.Select(channel => channel.GetProperty("key").GetString()!)];
        Assert.Equal(keys[1..].Order(StringComparer.Ordinal), keys[1..]);

        // A page at a time, by the cursor each page gives, the same order.
        List<string> paged = [];
        string? cursor = null;
        do
        {
            (code, text, said) = await Run(["channels", session.Path, "--page-size", "1", "--json",
                .. cursor is null ? Array.Empty<string>() : ["--cursor", cursor]]);
            Assert.True(code == InterCatExitCode.Success, said);
            JsonElement page = JsonDocument.Parse(text).RootElement.GetProperty("page");
            paged.Add(Assert.Single(page.GetProperty("channels").EnumerateArray()).GetProperty("key").GetString()!);
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        Assert.Equal(keys, paged);

        // In words too, each page saying how to read the next, which the cursor it names reads.
        (code, text, said) = await Run("channels", session.Path, "--page-size", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m) · 6 observed records\r?$", text);
        string next = Regex.Match(text, @"Next page: icat channels <directory> --cursor (\S+)").Groups[1].Value;
        Assert.NotEmpty(next);
        (code, text, said) = await Run("channels", session.Path, "--page-size", "1", "--cursor", next);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m) · 2 observed records\r?$", text);

        // A cursor read after the session published another generation asks for a restart, no channel silently shifted.
        Publish(session.Store,
        [
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 1, 700, 11)
                .Between("127.0.0.1:50003", "127.0.0.1:6060") with { SessionRelativeTicks = 1_600 },
        ]);
        session.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("channels", session.Path, "--page-size", "1", "--cursor", next);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Contains("Restart from the first page; no channel was silently shifted.", Regex.Replace(text + said, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("observed record", text, Ordinal);

        // Its help says the order.
        (_, text, _) = await Run("channels", "--help");
        Assert.Contains("busiest first by the records at their two ends", Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "P6: icat channels --process names a process by the PID it alone held or by its instance, and refuses in words one naming no process or a reused PID, never listing it as a process with no channel")]
    public async Task AProcessIsNamedAsEveryCommandNamesOne()
    {
        // PID 100 was held twice, its first holder sending to PID 200 before it exited and another took the PID.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 100) with { ResourceName = @"C:\Tools\first.exe" },
            Lifecycle(15, ObservationKind.Exit, 100, 101),
            Lifecycle(16, ObservationKind.Create, 100, 102) with { ResourceName = @"C:\Tools\second.exe" },
            .. Rows(),
        ]);
        ProcessNode[] holders = [.. SessionOverviewProjector.Project(session.Store).Nodes.Where(node => node.ProcessId == 100)
            .OrderBy(node => node.PidHolder)];
        ProcessInstanceId server = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 300).Id;
        session.Store.ReleaseSegmentReaders();
        Assert.Equal(2, holders.Length);

        // A PID one process alone held names it, as its instance does: its one channel, the busiest's six records.
        foreach (string named in new[] { "300", server.ToString() })
        {
            (InterCatExitCode code, string text, string said) = await Run("channels", session.Path, "--process", named);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Process instance +" + Regex.Escape(server.ToString()) + @"\r?$", text);
            Assert.Matches(@"(?m)^  Channels in scope +1\r?$", text);
            Assert.Matches(@"(?m) · 6 observed records\r?$", text);
        }

        // The first holder of the reused PID, by its instance: the channel it held, and no other holder's.
        (InterCatExitCode first, string held, string why) = await Run("channels", session.Path, "--process", holders[0].Id.ToString());
        Assert.True(first == InterCatExitCode.Success, why);
        Assert.Matches(@"(?m)^  Channels in scope +1\r?$", held);
        Assert.Matches(@"(?m) · 2 observed records\r?$", held);

        // A reused PID, a PID or an instance naming no process, and what is neither are refused as every command refuses
        // them, a process's one-sided connections too, rather than listed as none.
        string nobody = Guid.NewGuid().ToString();
        foreach ((string[] args, string refusal) in new (string[], string)[]
        {
            (["--process", "100"], $"--process 100 names 2 processes, since the PID was reused: {holders[0].Id} (first.exe · PID 100 #1), "
                + $"{holders[1].Id} (second.exe · PID 100 #2). Name one by its instance id: --process <instance>."),
            (["--process", "999"], "--process 999 names no process: no process of the session held PID 999."),
            (["--process", nobody], $"--process {nobody} names no process instance of the session's current generation."),
            (["--process", nobody, "--one-sided"], $"--process {nobody} names no process instance of the session's current generation."),
            (["--process", "x"], "--process names a process by its PID or its instance id, as icat processes prints them; 'x' is neither."),
            (["--one-sided"], "--one-sided lists one process's connections: name it with --process <pid | instance>."),
        })
        {
            (InterCatExitCode code, string text, string said) = await Run(["channels", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains(refusal, Regex.Replace(said, @"\s+", " "), Ordinal);
            Assert.DoesNotContain("Channels in scope", text, Ordinal);
        }

        // Its help says how a process is named.
        (_, string help, _) = await Run("channels", "--help");
        Assert.Contains("--process <pid | instance>", help, Ordinal);
        Assert.Contains("by the PID it alone held or its instance id as icat processes prints it", Regex.Replace(help, @"\s+", " "), Ordinal);
    }

    /// <summary>
    /// PIDs 100 and 200 exchange two records over 50000 and 8080, PIDs 300 and 400 six over 50001 and 9090, and PIDs 500 and
    /// 600 two over 50002 and 7070.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 1)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
        Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 200, 2)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        .. Enumerable.Range(0, 3).SelectMany(index => new[]
        {
            Transfer(20 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 1, 300, (ulong)(3 + (2 * index)))
                .Between("127.0.0.1:50001", "127.0.0.1:9090") with { SessionRelativeTicks = 1_200 + (20 * index) },
            Transfer(21 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 400, (ulong)(4 + (2 * index)))
                .Between("127.0.0.1:9090", "127.0.0.1:50001") with { SessionRelativeTicks = 1_210 + (20 * index) },
        }),
        Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 500, 9)
            .Between("127.0.0.1:50002", "127.0.0.1:7070") with { SessionRelativeTicks = 1_400 },
        Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 600, 10)
            .Between("127.0.0.1:7070", "127.0.0.1:50002") with { SessionRelativeTicks = 1_500 },
    ];

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
