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
/// icat compare (R18, `contracts/comparison-v1.md`): two intervals of a session compared row by row, as rates over each
/// whole interval, the most changed first, with both values and the reason wherever no change can be stated.
/// </summary>
public sealed class CompareCommandTests
{
    private const long Second = 10_000_000;

    [Fact(DisplayName = "R18: icat compare states each row's values, rates and change, the most changed first, its JSON the same")]
    public async Task IcatCompareStatesEachRowsChange()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Baseline +\[0\.0+, 10\.0+\) s, 10\.0+ s long\r?$", text);
        Assert.Matches(@"(?m)^  Rows +2 between processes · 2 to other hosts, with records in either interval\r?$", text);
        Assert.Contains("  Coverage in the baseline: covered for TCP · no other mechanism collected", text, Ordinal);
        Assert.Matches(@"(?m)^  Relationship +Over +Baseline +Compared +Change\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  ((?:PID |10\.).+?) {2,}(TCP\S*(?: at \d+)?) +(.+?) {2,}(.+?) {2,}(\S.*?)\r?$")
            .Select(match => string.Join(" | ", match.Groups.Values.Skip(1).Select(group => group.Value)))];
        Assert.Equal(
            [
                "PID 100 → 142.250.1.1 | TCP at 443 | 1 · 0.10/s | 20 · 1.00/s | up 0.90/s (+900%)",
                "PID 400 and PID 300 | TCP | 6 · 0.60/s | 0 | disappeared",
                "10.0.0.5 → PID 100 | TCP at 3389 | 0 | 2 · 0.10/s | appeared",
                "PID 100 and PID 200 | TCP | 20 · 2.00/s | 40 · 2.00/s | unchanged",
            ],
            rows);
        Assert.Contains("Rates are over each whole interval, never over the part of it the capture covered.", text, Ordinal);

        // By bytes: what was sent across each, a host's what the process sent there.
        (code, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--rank-by", "bytes-sent");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("Compared by           bytes sent across per second, each transfer counted once at its sender", text, Ordinal);
        Assert.Matches(@"(?m)^  PID 100 → 142\.250\.1\.1 +TCP at 443 +nothing sent +1,000 B · 50 B/s +appeared\r?$", text);
        Assert.Matches(@"(?m)^  PID 100 and PID 200 +TCP +80 B · 8 B/s +160 B · 8 B/s +unchanged\r?$", text);
        Assert.Contains("Of another host, only what the process sent there is counted", text, Ordinal);
        Assert.DoesNotContain("A value marked ≥", text, Ordinal);

        // As many as asked, saying how many more there are.
        (code, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--limit", "1");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("3 more, less changed; --limit 4 lists every one.", text, Ordinal);
        Assert.DoesNotContain("PID 300", text, Ordinal);

        // The same as JSON: what it compared, by what, and each row with both intervals' values and coverage.
        (code, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("comparison-v1", session.Path), (root.GetProperty("contract").GetString(), root.GetProperty("sessionPath").GetString()));
        JsonElement comparison = root.GetProperty("comparison");
        Assert.Equal(("Records", "IncludeCorrelated", 4, 0), (comparison.GetProperty("metric").GetString(),
            comparison.GetProperty("policy").GetString(), comparison.GetProperty("relationships").GetInt32(), comparison.GetProperty("quiet").GetInt32()));
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("byteDomain").ValueKind);
        Assert.Equal((10 * Second, 30 * Second), (comparison.GetProperty("compared").GetProperty("interval").GetProperty("startTicks").GetInt64(),
            comparison.GetProperty("compared").GetProperty("interval").GetProperty("endTicks").GetInt64()));
        JsonElement host = comparison.GetProperty("rows")[0];
        Assert.Equal(("OtherHost", "142.250.1.1", "SourceToTarget", "Increased"), (host.GetProperty("kind").GetString(),
            host.GetProperty("address").GetString(), host.GetProperty("connected").GetString(), host.GetProperty("change").GetString()));
        Assert.Equal((1L, 20L), (host.GetProperty("baseline").GetProperty("records").GetInt64(), host.GetProperty("compared").GetProperty("records").GetInt64()));
        Assert.Equal(0.9, host.GetProperty("differencePerSecond").GetDouble());
        Assert.Equal([443], host.GetProperty("hostPorts").EnumerateArray().Select(port => port.GetInt32()));
        Assert.Equal(JsonValueKind.Null, host.GetProperty("target").ValueKind);
        Assert.Equal("Covered", host.GetProperty("compared").GetProperty("coverage").GetProperty("state").GetString());
        JsonElement gone = comparison.GetProperty("rows")[1];
        Assert.Equal(("Processes", "Disappeared"), (gone.GetProperty("kind").GetString(), gone.GetProperty("change").GetString()));
        Assert.Equal([300, 400], new[] { gone.GetProperty("source"), gone.GetProperty("target") }
            .Select(end => end.GetProperty("processId").GetInt32()).Order());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("reason").ValueKind);

        // A host that connected to the process: its connections arrived at the process's own port.
        JsonElement accepted = comparison.GetProperty("rows")[2];
        Assert.Equal(("10.0.0.5", "TargetToSource", "Appeared"), (accepted.GetProperty("address").GetString(),
            accepted.GetProperty("connected").GetString(), accepted.GetProperty("change").GetString()));
        Assert.Equal([3389], accepted.GetProperty("acceptedPorts").EnumerateArray().Select(port => port.GetInt32()));

        // As many rows as asked, every row counted; a rate is rounded to three decimals, half to even.
        (_, text, _) = await Run("compare", session.Path, "--baseline", "0:3s", "--interval", "3s:30s", "--json", "--limit", "1");
        comparison = JsonDocument.Parse(text).RootElement.GetProperty("comparison");
        Assert.Equal((4, 1), (comparison.GetProperty("relationships").GetInt32(), comparison.GetProperty("rows").GetArrayLength()));
        Assert.Equal(0.667, comparison.GetProperty("rows")[0].GetProperty("baseline").GetProperty("perSecond").GetDouble());

        // By bytes, JSON names the byte domain and accounting it counted.
        (_, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--rank-by", "bytes-sent", "--json");
        comparison = JsonDocument.Parse(text).RootElement.GetProperty("comparison");
        Assert.Equal(("BytesSent", "TransportObserved", "SendSide"), (comparison.GetProperty("metric").GetString(),
            comparison.GetProperty("byteDomain").GetString(), comparison.GetProperty("accounting").GetString()));
    }

    [Fact(DisplayName = "R21: icat compare says why it states no change, each reason once beneath the table, never a zero")]
    public async Task IcatCompareSaysWhyItStatesNoChange()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: TransportLedger(tcp: true, udp: false, lost: 3) with
        {
            Epochs = [TransportLedger(tcp: true, udp: false, lost: 3).Epochs[0] with { LastDeliveredNativeTicks = 40 * Second }],
        });
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(4, Regex.Count(text, @"(?m) not comparable \[1\]\r?$"));
        Assert.Matches(@"(?m)^  \[1\] TCP has a partial gap over the baseline, not extrapolated: .*3 lost events.*\.\r?$", text);
        Assert.DoesNotContain("[2]", text, Ordinal);
        Assert.Contains("  Coverage in the baseline: a partial gap, not extrapolated, for TCP: ", text, Ordinal);

        // Nothing in either interval: no row, and why none.
        (code, text, _) = await Run("compare", session.Path, "--baseline", "31s:32s", "--interval", "33s:35s");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No relationship the overview draws, and no process's connections to another host, has a record in either "
            + "interval, so none is compared.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Matches(@"(?m)^  Rows +0 between processes · 0 to other hosts, with records in either interval · 4 quiet in both\r?$", text);
    }

    [Fact(DisplayName = "R3: icat compare marks a lower bound, stating a change from it only where the bound decides it")]
    public async Task IcatCompareMarksALowerBound()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Exchange(1 * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080", 100),
            .. Exchange(11 * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080", 400),
            .. Exchange(12 * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080", null),
        ], coverage: Ledger());
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run(
            "compare", session.Path, "--baseline", "0:10s", "--interval", "10s:20s", "--rank-by", "bytes-sent");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  PID (?:100 and PID 200|200 and PID 100) +TCP +100 B · 10 B/s +≥400 B · ≥40 B/s +up at least 30 B/s\r?$", text);
        Assert.Contains("A value marked ≥ is a lower bound", text, Ordinal);

        // Notes say only what the rows need: no other host, and no arrow, is listed.
        Assert.DoesNotContain("another host", text, OrdinalIgnoreCase);
        Assert.DoesNotContain("An arrow points", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat compare refuses what names no comparison, saying what it takes")]
    public async Task IcatCompareRefusesWhatNamesNoComparison()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        (string[] Args, string Said)[] refused =
        [
            (["compare", "--baseline", "0:1", "--interval", "1:2"], "x A session directory is required: icat compare <directory>."),
            (["compare", session.Path, "--interval", "1:2"], "x --baseline is required: start:end, "),
            (["compare", session.Path, "--baseline", "0:1"], "x --interval is required: start:end, "),
            (["compare", session.Path, "--baseline", "2:1", "--interval", "1:2"], "x --baseline is required: "),
            (["compare", session.Path, "--baseline", "-9223372036854775808:0", "--interval", "1:2"], "x --baseline -9223372036854775808:0 ends "),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--rank-by", "calls"],
                "x --rank-by compares records or bytes-sent; 'calls' is neither."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--limit", "0"],
                "x --limit expects a number of relationships of 1 or more; '0' is not one."),
            (["compare", Path.Combine(session.Path, "missing"), "--baseline", "0:1", "--interval", "1:2"], "x No session directory at "),
        ];
        foreach ((string[] args, string expected) in refused)
        {
            (InterCatExitCode code, string output, string error) = await Run(args);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
            Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");
            Assert.StartsWith(expected, error, Ordinal);
        }

        (InterCatExitCode helped, string help, _) = await Run("compare", "--help");
        Assert.Equal(InterCatExitCode.Success, helped);
        Assert.Contains("icat compare <session-directory> --baseline <start:end> --interval <start:end>", help, Ordinal);
    }

    /// <summary>
    /// Two relationships and two processes' connections to other hosts: an exchange each second between 100 and 200
    /// throughout; three in the first ten seconds between 300 and 400; 100's connection to 142.250.1.1:443 at half a second,
    /// then a send each two seconds from the tenth, each answered; and 10.0.0.5's connection to 100's own port 3389 at the
    /// fifteenth, with what it sent.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        List<ObservationRowV1> rows = [];
        for (long at = 0; at < 30; at++)
        {
            rows.AddRange(Exchange(at * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080", 8));
        }

        foreach (long at in new long[] { 2, 4, 6 })
        {
            rows.AddRange(Exchange((at * Second) + 1, 300, 400, "127.0.0.1:50001", "127.0.0.1:9090", 8));
        }

        rows.Add(Timed(Transfer(Second / 2, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100)
            .Between("192.168.1.10:52000", "142.250.1.1:443")));
        for (long at = 10; at < 30; at += 2)
        {
            rows.Add(Timed(Transfer((at * Second) + 2, ObservationKind.Send, AccountingSide.SendSide, 100, 100)
                .Between("192.168.1.10:52000", "142.250.1.1:443")));
            rows.Add(Timed(Transfer((at * Second) + 3, ObservationKind.Receive, AccountingSide.ReceiveSide, 1_000, 100)
                .Between("192.168.1.10:52000", "142.250.1.1:443")));
        }

        rows.Add(Timed(Transfer((15 * Second) + 5, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 100)
            .Between("192.168.1.10:3389", "10.0.0.5:51000")));
        rows.Add(Timed(Transfer((16 * Second) + 5, ObservationKind.Receive, AccountingSide.ReceiveSide, 200, 100)
            .Between("192.168.1.10:3389", "10.0.0.5:51000")));
        return [.. rows.OrderBy(row => row.NativeTicks)];
    }

    /// <summary>One exchange at a tick: the client's send of <paramref name="bytes"/> and the server's receive of it.</summary>
    private static ObservationRowV1[] Exchange(long ticks, int client, int server, string clientEnd, string serverEnd, long? bytes) =>
    [
        Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, bytes, client).Between(clientEnd, serverEnd)),
        Timed(Transfer(ticks + 1_000, ObservationKind.Receive, AccountingSide.ReceiveSide, bytes, server).Between(serverEnd, clientEnd)),
    ];

    /// <summary>A live capture's one epoch over the first forty seconds, which collected TCP, delivering and losing nothing else.</summary>
    private static CoverageLedgerV1 Ledger() => TransportLedger(tcp: true, udp: false) with
    {
        Epochs = [TransportLedger(tcp: true, udp: false).Epochs[0] with { LastDeliveredNativeTicks = 40 * Second }],
    };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

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
