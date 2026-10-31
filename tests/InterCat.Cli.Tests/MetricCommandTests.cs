using System.Globalization;
using System.Text.Json;
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
/// icat metric as a person runs it (`contracts/metrics-v1.md`, R18, R21): RPC calls counted, timed and failed in words
/// with what each call's records establish; what it cannot answer, and why; rankings by mechanism, executable and the
/// other end; the records between two sets; and every option it cannot read refused in words.
/// </summary>
public sealed class MetricCommandTests : IDisposable
{
    private readonly TemporarySession session = new();
    private readonly string app;
    private readonly string services;

    public MetricCommandTests()
    {
        // app.exe's three calls to the service control manager, two served by services.exe through ALPC and one failed; a
        // server call of PID 2020 still open at capture end; and a stop of app.exe's that no start pairs, reporting a failure.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store,
        [
            .. rows.Select(row => row switch
            {
                { Mechanism: Mechanism.ProcessLifecycle, OwnerProcessId: 1_960 } => row with { ResourceName = @"C:\Windows\System32\services.exe" },
                { Mechanism: Mechanism.ProcessLifecycle, OwnerProcessId: 400 } => row with { ResourceName = @"C:\Apps\app.exe" },
                _ => row,
            }),
            RpcCall(400, ObservationKind.RequestStart, Direction.Inbound, raisedBy: 2_020, 70,
                activity: Guid.Parse("21212121-2222-4333-8444-555555555555"), interfaceUuid: ServiceControlInterface),
            RpcCall(410, ObservationKind.RequestEnd, Direction.Outbound, raisedBy: 400, 71,
                activity: Guid.Parse("31313131-2222-4333-8444-555555555555"), status: 5),
        ], fields: fields, coverage: RpcLedger(alpc: true));
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        app = overview.Nodes.Single(node => node.ProcessId == 400).Id.ToString();
        services = overview.Nodes.Single(node => node.ProcessId == 1_960).Id.ToString();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat metric says RPC calls started, completed, failed and timed in words, with what each call's records establish")]
    public async Task CallsAreCountedAndTimedInWords()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string text = await Answered("--basis", "logical-operations", "--metric", "operations-started");
        Assert.Matches(@"(?m)^  Value +7 calls started\r?$", text);
        Assert.Contains("  Calls whose start is in scope, by what their records establish:", text, Ordinal);
        Assert.Equal(["completed | 6 | yes", "open at capture end | 1 | yes"], Rows(text, "State"));
        text = await Answered("--basis", "logical-operations", "--metric", "operations-started", "--interval", "0:5");
        Assert.Contains("  Calls whose start is in scope, by what their records establish:\n  none\n", text.ReplaceLineEndings("\n"), Ordinal);

        text = await Answered("--basis", "logical-operations", "--metric", "operations-completed");
        Assert.Matches(@"(?m)^  Value +6 calls completed\r?$", text);
        Assert.Contains("  Calls whose stop is in scope, by what their records establish:", text, Ordinal);
        Assert.Equal(["completed | 6 | yes", "start not observed | 1 | no"], Rows(text, "State"));

        // Errors are the completed calls whose stop reports a failure; a failing stop no start pairs is said, never counted.
        text = await Answered("--basis", "logical-operations", "--metric", "errors");
        Assert.Matches(@"(?m)^  Value +2 failed calls\r?$", text);
        Assert.Matches($@"(?m)^  Status known on +{Regex.Escape(1.0.ToString("P1", culture))} of 6 completed calls\r?$", text);
        Assert.Equal(["completed | 6 | when failed", "start not observed | 1 | no"], Rows(text, "State"));
        Assert.Contains("  Of 6 completed calls: 2 failed, 4 succeeded, 0 without a status.", text, Ordinal);
        Assert.Contains("  1 stop paired with no start reports a failure status; stated, not counted.", text, Ordinal);
        text = await Answered("--basis", "logical-operations", "--metric", "errors", "--group-by", "process");
        Assert.Equal(
            ["1 | PID 400 app.exe - created " + Seconds(0, culture) + " | 1 | correlated 3",
                "2 | PID 1960 services.exe - running at capture start | 1 | correlated 3"],
            Rows(text, "Rank"));

        // A duration is one side's calls, by its statistic, with their distribution, their time and how many were measured.
        text = await Answered("--basis", "logical-operations", "--metric", "duration", "--duration", "client-call");
        Assert.Matches($@"(?m)^  Value +{Regex.Escape(Duration(2_000, culture))}, the median of 3 client calls completed in scope\r?$", text);
        Assert.Matches($@"(?m)^  Distribution +minimum {Regex.Escape(Duration(1_000, culture))} · median {Regex.Escape(Duration(2_000, culture))} · "
            + $@"95th percentile {Regex.Escape(Duration(4_000, culture))} · maximum {Regex.Escape(Duration(4_000, culture))}\r?$", text);
        Assert.Matches($@"(?m)^  Call time +{Regex.Escape(Duration(7_000, culture))} summed, {Regex.Escape(Duration(7_000, culture))} busy \(their union\)\r?$", text);
        Assert.Matches($@"(?m)^  Measured on +{Regex.Escape(0.75.ToString("P1", culture))} of 4 calls in the cohort\r?$", text);
        Assert.Equal(["completed | 3 | measured", "start not observed | 1 | no duration"], Rows(text, "State"));
        Assert.Equal(["another named interval (the other side) | 3", "outside the interval | 0"], Rows(text, "Why"));
        text = await Answered("--basis", "logical-operations", "--metric", "duration", "--duration", "server-execution", "--statistic", "p95",
            "--cohort", "started");
        Assert.Matches($@"(?m)^  Value +{Regex.Escape(Duration(2_500, culture))}, the 95th percentile of 3 served calls started in scope\r?$", text);
        Assert.Equal(["completed | 3 | measured", "open at capture end | 1 | no duration"], Rows(text, "State"));
        text = await Answered("--basis", "logical-operations", "--metric", "duration", "--duration", "client-call", "--statistic", "max",
            "--group-by", "executable", "--top", "1");
        Assert.Matches($@"(?m)^  Value +{Regex.Escape(Duration(4_000, culture))}, the maximum of 3 client calls completed in scope\r?$", text);
        Assert.Equal([@"1 | C:\Apps\app.exe | " + Duration(4_000, culture) + " | 3 of 4 | correlated 4"], Rows(text, "Rank"));

        // As JSON: the calls in scope by what their records establish, a failing stop no start pairs, and the distribution.
        using (JsonDocument json = JsonDocument.Parse(await Answered("--basis", "logical-operations", "--metric", "duration", "--duration",
            "client-call", "--json")))
        {
            JsonElement operations = json.RootElement.GetProperty("operations");
            Assert.Equal(("rpc-call-operation-v1", "stop", 3L, 1L), (operations.GetProperty("rule").GetString(),
                operations.GetProperty("countedRecord").GetString(), operations.GetProperty("inScope").GetProperty("Completed").GetInt64(),
                operations.GetProperty("inScope").GetProperty("StartNotObserved").GetInt64()));
            JsonElement distribution = json.RootElement.GetProperty("distribution");
            Assert.Equal((3L, 1_000L, 2_000L, 4_000L, 4_000L, 7_000L, 7_000L), (distribution.GetProperty("count").GetInt64(),
                distribution.GetProperty("minimumNanoseconds").GetInt64(), distribution.GetProperty("medianNanoseconds").GetInt64(),
                distribution.GetProperty("percentile95Nanoseconds").GetInt64(), distribution.GetProperty("maximumNanoseconds").GetInt64(),
                distribution.GetProperty("summedNanoseconds").GetInt64(), distribution.GetProperty("busyNanoseconds").GetInt64()));
        }

        using (JsonDocument json = JsonDocument.Parse(await Answered("--basis", "logical-operations", "--metric", "errors", "--json")))
        {
            Assert.Equal((2L, 1L, "stop"), (json.RootElement.GetProperty("value").GetInt64(),
                json.RootElement.GetProperty("operations").GetProperty("unpairedFailures").GetInt64(),
                json.RootElement.GetProperty("operations").GetProperty("countedRecord").GetString()));
        }

        using (JsonDocument json = JsonDocument.Parse(await Answered("--basis", "logical-operations", "--metric", "active-peers", "--participant",
            app, "--json")))
        {
            JsonElement unlinked = json.RootElement.GetProperty("contributions").GetProperty("unlinkedCalls");
            Assert.Equal((2L, 1L, 1L), (unlinked.GetProperty("NotReached").GetInt64(), unlinked.GetProperty("NotCompleted").GetInt64(),
                unlinked.GetProperty("NoSend").GetInt64()));
        }
    }

    [Fact(DisplayName = "R21: icat metric says what it cannot answer and why, never that a session without what a request needs holds nothing")]
    public async Task WhatCannotBeAnsweredIsSaid()
    {
        // Peers of calls grouped with no focus: no operation this version derives answers it, though the session holds calls.
        (InterCatExitCode code, string text, _) = await Run(["metric", session.Path, "--basis", "logical-operations", "--metric", "active-peers",
            "--group-by", "process"]);
        Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
        Assert.Matches(@"(?m)^  Reason +No derived operation answers it\r?$", text);
        Assert.Contains("name it with owner(P) or participant(P)", text, Ordinal);

        // No record identifies a channel, over the whole scope or grouped: unmeasured, never zero, each saying why.
        foreach (string[] channels in new[] { new[] { "--metric", "active-channels" }, ["--metric", "active-channels", "--group-by", "process"] })
        {
            (code, text, _) = await Run(["metric", session.Path, .. channels]);
            Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
            Assert.Matches(@"(?m)^  Reason +Nothing measured\r?$", text);
            Assert.Contains("None of the 18 records in scope that have another end identifies a channel (NoRelationRule 18), so none can "
                + "be counted.", text, Ordinal);
            Assert.DoesNotContain("0 channels", text, Ordinal);
        }

        // A rate with no interval, of a session whose capture recorded no stop, says to name one.
        (code, text, _) = await Run(["metric", session.Path, "--metric", "rate", "--rate-numerator", "observations"]);
        Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
        Assert.Matches(@"(?m)^  Reason +No interval\r?$", text);
        Assert.Contains("this session's capture recorded no stop, as an import or an older capture does not. Name an interval with "
            + "--interval <start>:<end>.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // A focus's peers are a lower bound beside the calls no ALPC link reached, each counted by why.
        text = await Answered("--basis", "logical-operations", "--metric", "active-peers", "--participant", app);
        Assert.Matches(@"(?m)^  Value +at least 1 process at the other end\r?$", text);
        Assert.Contains("  Calls whose other end no ALPC link reached: no linked client call reached it (2), not completed, so no "
            + "window to follow (1), no ALPC send on its thread during the call (1)", text, Ordinal);
        Assert.Equal(["outside the process filter | 2", "of which the other end is unresolved | 2", "outside the interval | 0"], Rows(text, "Why"));

        // A count of no observation says coverage is stated apart, and claims no missing ledger of a session that has one.
        text = await Answered("--metric", "observations", "--participant", app, "--peer", services);
        Assert.Matches($@"(?m)^  Other end +{services} only\r?$", text);
        Assert.Matches(@"(?m)^  Value +0 observations\r?$", text);
        Assert.Contains("coverage is stated separately from data (R21).", text, Ordinal);
        Assert.DoesNotContain("coverage ledger", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat metric ranks by mechanism, executable, the other end and per second in words, a remainder past --top, and a ranking with no row said")]
    public async Task RankingsAreSaidInWords()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string text = await Answered("--metric", "observations", "--group-by", "mechanism");
        Assert.Contains("  By mechanism:", text, Ordinal);
        Assert.Equal(["1 | RPC | 14", "2 | ALPC | 4", "3 | Process | 2"], Rows(text, "Rank"));

        text = await Answered("--metric", "observations", "--group-by", "executable", "--top", "1");
        Assert.Contains("OBSERVATIONS BY EXECUTABLE", text, Ordinal);
        Assert.Equal([@"1 | C:\Apps\app.exe | 8 | direct 1, correlated 7", "... | 1 more executable | 7 | -"], Rows(text, "Rank"));

        // A ranking by the other end none of whose rows resolves says so in place of an empty table, its contributions beneath.
        text = await Answered("--metric", "observations", "--owner", app, "--group-by", "peer");
        Assert.Contains($"  By the process at the other end from owner {app} (", text, Ordinal);
        Assert.Contains("  No row: every contribution is listed beneath by why it belongs to none.", text, Ordinal);
        Assert.DoesNotContain("  Rank  Process instance", text, Ordinal);
        Assert.Equal(["no relation rule covers this mechanism yet | 8 | 8"], Rows(text, "Reason"));
        text = await Answered("--basis", "logical-operations", "--metric", "operations-started", "--participant", app, "--group-by", "peer");
        Assert.Equal(["1 | PID 1960 services.exe - running at capture start | 4 | correlated 4"], Rows(text, "Rank"));
        Assert.Equal(["an RPC call whose other end was not linked through ALPC | 1 | 1"], Rows(text, "Reason"));

        // Per second, each group's rate over the whole interval.
        text = await Answered("--metric", "rate", "--rate-numerator", "observations", "--interval", "0:1000", "--group-by", "process");
        Assert.Equal(
            [
                "1 | PID 400 app.exe - created " + Seconds(0, culture) + " | " + PerSecond(80_000, culture) + " records/s | direct 1, correlated 7",
                "2 | PID 1960 services.exe - running at capture start | " + PerSecond(70_000, culture) + " records/s | direct 1, correlated 6",
                "3 | PID 2020 - seen only in its own records | " + PerSecond(10_000, culture) + " records/s | correlated 1",
            ],
            Rows(text, "Rank"));
    }

    [Fact(DisplayName = "R18: icat metric keeps the records between two sets, writes its answer to a file when asked, and refuses what it cannot read in words")]
    public async Task BetweenOutputAndRefusals()
    {
        // Between two sets, one way or either, said with the sets and the direction, and in JSON.
        string text = await Answered("--metric", "observations", "--between", app, "--and", services, "--direction", "first-to-second");
        Assert.Matches($@"(?m)^  First set +{app}\r?$", text);
        Assert.Matches($@"(?m)^  Second set +{services}\r?$", text);
        Assert.Matches(@"(?m)^  Direction +data from the first set to the second\r?$", text);
        text = await Answered("--basis", "logical-operations", "--metric", "operations-started", "--between", app, "--and", services);
        Assert.Matches(@"(?m)^  Value +4 calls started\r?$", text);
        Assert.Matches(@"(?m)^  Direction +either way, with connects and disconnects\r?$", text);
        text = await Answered("--metric", "observations", "--between", app, "--and", services, "--direction", "second-to-first");
        Assert.Matches(@"(?m)^  Direction +data from the second set to the first\r?$", text);
        using (JsonDocument json = JsonDocument.Parse(await Answered("--metric", "observations", "--between", app, "--and", services,
            "--direction", "second-to-first", "--json")))
        {
            JsonElement between = json.RootElement.GetProperty("specification").GetProperty("between");
            Assert.Equal((app, services, "SecondToFirst"), (between.GetProperty("firstInstanceIds")[0].GetString(),
                between.GetProperty("secondInstanceIds")[0].GetString(), between.GetProperty("direction").GetString()));
        }

        // --print-canonical names the query without answering it.
        using (JsonDocument canonical = JsonDocument.Parse(await Answered("--metric", "observations", "--print-canonical", "--json")))
        {
            Assert.Equal("query-identity-v1", canonical.RootElement.GetProperty("contract").GetString());
            Assert.StartsWith("v1:sha256:", canonical.RootElement.GetProperty("token").GetString(), Ordinal);
        }

        // --output writes the answer once, and replaces it only when asked.
        string output = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"), "metric.json");
        try
        {
            (InterCatExitCode code, _, string said) = await Run(["metric", session.Path, "--metric", "observations", "--output", output]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains($"Metric written to {output}.", said, Ordinal);
            using (JsonDocument written = JsonDocument.Parse(File.ReadAllText(output)))
            {
                Assert.Equal("metrics-v1", written.RootElement.GetProperty("contract").GetString());
            }

            (code, _, said) = await Run(["metric", session.Path, "--metric", "observations", "--output", output]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"{output} exists. Pass --overwrite to replace it.", said, Ordinal);
            (code, _, said) = await Run(["metric", session.Path, "--metric", "observations", "--output", output, "--overwrite"]);
            Assert.True(code == InterCatExitCode.Success, said);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(output)!, recursive: true);
        }

        // Every option it cannot read is refused, saying what it expects.
        foreach ((string[] args, string why) in new (string[], string)[]
        {
            (["--metric", "observations", "--group-by", "bogus"], "--group-by expects process, executable, session, mechanism, peer, instance-only,"),
            (["--metric", "observations", "--top", "0"], "--top expects a number of ranked rows from 1 to 100,000; '0' is not one."),
            (["--metric", "observations", "--evidence", "x"], "--evidence expects a whole number of records up to 1,000; 'x' is not one."),
            (["--metric", "observations", "--interval", "5:5"], "--interval's end must be after its start; '5:5' names an empty or reversed interval."),
            (["--metric", "observations", "--interval", "1.5q:2"], "--interval expects <start>:<end>, half-open."),
            (["--metric", "observations", "--owner", "123"], "--owner requires a process instance id, as icat processes or a process-grouped "
                + "result prints it. '123' is not one; a PID is not an instance identity."),
            (["--metric", "observations", "--between", "x", "--and", "y"], "--between requires comma-separated process instance ids"),
            (["--metric", "observations", "--between", app], "A between filter names both sets: --between <ids> --and <ids>"),
            (["--metric", "observations", "--between", app + ",", "--and", services], "It holds an empty entry."),
            (["--metric", "observations", "--interval", "0.5ns:2"], "'0.5ns' is finer than a nanosecond, which is the finest session time "
                + "InterCat keeps."),
            (["--metric", "observations", "--interval", "99999999999999s:999999999999999s"], "'99999999999999s' is after every reading "
                + "this session's clock can hold, so the interval holds none."),
        })
        {
            (InterCatExitCode code, string answered, string said) = await Run(["metric", session.Path, .. args]);
            Assert.True(code == InterCatExitCode.InvalidInvocation && answered.Length == 0, string.Join(' ', args) + ": " + said);
            Assert.Contains(why, said, Ordinal);
        }

        string nowhere = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"));
        (InterCatExitCode missing, _, string gone) = await Run(["metric", nowhere, "--metric", "observations"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains($"No session directory at {nowhere}.", gone, Ordinal);

        (InterCatExitCode matrix, _, string unknown) = await Run(["metric", "--matrix", "--bogus"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, matrix);
        Assert.Contains("--bogus", unknown, Ordinal);
    }

    [Fact(DisplayName = "R18: icat metric ranks bytes by mechanism and per second by process, beside how much of each was measured")]
    public async Task BytesAreRankedBesideTheirMeasurement()
    {
        // client.exe's connection to server.exe: one send of 100 bytes, and one whose size was not recorded, each received.
        using var tcp = new TemporarySession();
        Publish(tcp.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Apps\server.exe" },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 20).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 21).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, null, 100, 30).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 31).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ], coverage: TransportLedger(tcp: true, udp: false));
        tcp.Store.ReleaseSegmentReaders();
        CultureInfo culture = CultureInfo.CurrentCulture;
        string half = 0.5.ToString("P1", culture);
        (InterCatExitCode code, string text, string said) = await Run(["metric", tcp.Path, "--metric", "bytes-sent", "--byte-domain",
            "transport-observed", "--side", "send", "--group-by", "mechanism"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(["1 | TCP | 100 B | " + half], Rows(text, "Rank"));
        (code, text, said) = await Run(["metric", tcp.Path, "--metric", "rate", "--rate-numerator", "bytes-sent", "--byte-domain",
            "transport-observed", "--side", "send", "--interval", "0:100", "--group-by", "process"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(["1 | PID 100 client.exe - created " + Seconds(1, culture) + " | " + PerSecond(10_000_000, culture) + " B/s | " + half
            + " | correlated 2"], Rows(text, "Rank"));

        // Its one connection is one channel, over the whole scope and grouped, and its two ends each other's peers.
        foreach ((string[] args, string value) in new (string[], string)[]
        {
            (["--metric", "active-channels"], "1 channel"),
            (["--metric", "active-channels", "--group-by", "process"], "1 channel in scope"),
            (["--metric", "active-peers", "--group-by", "process"], "2 processes have at least one resolved peer"),
        })
        {
            (code, text, said) = await Run(["metric", tcp.Path, .. args]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches($@"(?m)^  Value +{value}\r?$", text);
        }
    }

    [Fact(DisplayName = "R18: icat metric says when its --interval holds no reading, where its readings lie, and what bare numbers name elsewhere")]
    public async Task AnIntervalHoldingNoReadingIsSaid()
    {
        // Three UDP sends 1, 2 and 3 ms into a capture whose clock read 1,000,000,000 at its epoch, so its native ticks
        // are not the session ticks every other command reads a bare number as.
        var clock = new SourceClockDescriptor(TestSessions.Clock, HostId.Derive("cli-interval-missed"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 10_000_000, 1_000_000_000, TimestampRounding.NearestEven, SourceClockMath.SessionTicksPerSecond * 60);
        using var sent = new TemporarySession();
        Publish(sent.Store,
        [
            .. Enumerable.Range(1, 3).Select(index => Transfer(1_000_000_000L + (index * 10_000), ObservationKind.Send,
                AccountingSide.SendSide, 10, 100, (ulong)index).Between("192.168.1.5:61000", "8.8.8.8:53") with
            {
                Mechanism = Mechanism.Udp,
                SessionRelativeTicks = index * 10_000L,
            }),
        ], clock: clock);
        sent.Store.ReleaseSegmentReaders();
        CultureInfo culture = CultureInfo.CurrentCulture;
        string readings = $"its readings run from {Seconds(10_000, culture)} to {Seconds(30_000, culture)} after capture start "
            + string.Create(culture, $"(native ticks {1_000_010_000:N0} to {1_000_030_000:N0}).");
        const string Bare = " A bare number here is a native tick of the session's clock, where every other command reads one as "
            + "100-nanosecond session ticks";

        // Bare numbers before every reading, which as session ticks would name the first 5 ms, are answered with how to
        // write those; written so, the interval holds the three sends, and nothing more is said. An interval holding the
        // first reading or the last at its edge holds a reading, and one ending at the first, or beginning past the last,
        // holds none.
        foreach ((string interval, string? missed, int held) in new (string, string?, int)[]
        {
            ("0:50000", "No reading of this session lies within --interval 0:50000: " + readings + Bare
                + ": to measure 0s to 0.005s after capture start, write --interval 0s:0.005s.", 0),
            ("0s:0.005s", null, 3),
            ("1000010000:1000010001", null, 1),
            ("1000030000:1000040000", null, 1),
            ("1000000000:1000010000", "No reading of this session lies within --interval 1000000000:1000010000: " + readings
                + Bare + ".", 0),
            ("30000:40000", "No reading of this session lies within --interval 30000:40000: " + readings + Bare
                + ": to measure 0.003s to 0.004s after capture start, write --interval 0.003s:0.004s.", 0),

            // Times past every reading are answered with where the readings lie, as is a time after a bare number; bare
            // numbers that as session ticks name no reading either, or before capture start, or no session time at all,
            // with what a bare number is here.
            ("1s:2s", "No reading of this session lies within --interval 1s:2s: " + readings, 0),
            ("1000040000:1s", "No reading of this session lies within --interval 1000040000:1s: " + readings, 0),
            ("0:10000", "No reading of this session lies within --interval 0:10000: " + readings + Bare + ".", 0),
            ("900000:950000", "No reading of this session lies within --interval 900000:950000: " + readings + Bare + ".", 0),
            ("-5:50000", "No reading of this session lies within --interval -5:50000: " + readings + Bare + ".", 0),
            ("184467440737095517:184467440737115517", "No reading of this session lies within --interval "
                + "184467440737095517:184467440737115517: " + readings + Bare + ".", 0),
        })
        {
            (InterCatExitCode code, string text, string said) = await Run(["metric", sent.Path, "--metric", "observations", "--interval", interval]);
            Assert.True(code == InterCatExitCode.Success, $"{interval}: {said}");
            string flowed = Regex.Replace(said, @"\s+", " ");
            if (missed is null)
            {
                Assert.DoesNotContain("No reading", flowed, Ordinal);
                Assert.Matches($@"(?m)^  Value +{held} ", text);
            }
            else
            {
                Assert.Contains(missed, flowed, Ordinal);
                Assert.Equal(missed.Contains(Bare, Ordinal), flowed.Contains(Bare, Ordinal));
                Assert.Equal(missed.Contains("to measure", Ordinal), flowed.Contains("to measure", Ordinal));
            }
        }
    }

    private async Task<string> Answered(params string[] args)
    {
        (InterCatExitCode code, string text, string said) = await Run(["metric", session.Path, .. args]);
        Assert.True(code == InterCatExitCode.Success, string.Join(' ', args) + ": " + said);
        return text;
    }

    private static string Duration(long nanoseconds, CultureInfo culture) => OperationText.Duration(nanoseconds, culture);

    private static string PerSecond(long perSecond, CultureInfo culture) => perSecond.ToString("N3", culture);

    /// <summary>A reading of the test clock as a process's label says it: its seconds after capture start, to the microsecond.</summary>
    private static string Seconds(long ticks, CultureInfo culture) => (ticks / 10_000_000.0).ToString("F6", culture) + " s";

    /// <summary>The rows of the first table whose header starts with <paramref name="first"/>, each cell joined by " | ".</summary>
    private static string[] Rows(string text, string first)
    {
        string[] lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        int header = Array.FindIndex(lines, line => line.StartsWith("  " + first + " ", Ordinal));
        Assert.True(header >= 0, $"No table headed {first}.");
        return [.. lines.Skip(header + 1).TakeWhile(line => line.StartsWith("  ", Ordinal) && Regex.IsMatch(line, @"\S\s{2,}\S"))
            .Select(line => string.Join(" | ", Regex.Split(line.Trim(), @"\s{2,}")))];
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
