using System.Globalization;
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

        // Its help says the order.
        (_, text, _) = await Run("channels", "--help");
        Assert.Contains("busiest first by the records at their two ends", Regex.Replace(text, @"\s+", " "), Ordinal);
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
