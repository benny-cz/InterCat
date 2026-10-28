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

        // A row's difference timeline says the same, once beneath its columns.
        (code, text, said) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--timeline", "1",
            "--columns", "2");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(2, Regex.Count(text, @"(?m) not comparable \[1\]\r?$"));
        Assert.Matches(@"(?m)^  \[1\] TCP has a partial gap over the baseline, not extrapolated: .*3 lost events.*\.\r?$", text);

        // Nothing in either interval: no row, and why none.
        (code, text, _) = await Run("compare", session.Path, "--baseline", "31s:32s", "--interval", "33s:35s");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No relationship the overview draws, and no process's connections to another host, has a record in either "
            + "interval, so none is compared.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Matches(@"(?m)^  Rows +0 between processes · 0 to other hosts, with records in either interval · 4 quiet in both\r?$", text);
    }

    [Fact(DisplayName = "R22: icat compare --group-by executable states each executable's processes together, grouped by path")]
    public async Task IcatCompareGroupsByExecutable()
    {
        // client.exe's process exchanged with server.exe until it exited at 9 s, and its next process from 11 s; two
        // processes no record names the executable of exchanged every other second throughout.
        using var session = new TemporarySession();
        List<ObservationRowV1> rows =
        [
            Timed(Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(9 * Second, ObservationKind.Exit, 100, (ulong)(9 * Second))),
            Timed(Lifecycle(10 * Second, ObservationKind.Create, 150, (ulong)(10 * Second)) with { ResourceName = @"C:\Tools\client.exe" }),
        ];
        for (long at = 1; at < 9; at++) rows.AddRange(Exchange(at * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080", 8));
        for (long at = 11; at < 19; at++) rows.AddRange(Exchange(at * Second, 150, 200, "127.0.0.1:50001", "127.0.0.1:8080", 8));
        for (long at = 1; at < 19; at += 2) rows.AddRange(Exchange((at * Second) + 7, 300, 400, "127.0.0.1:50002", "127.0.0.1:9090", 8));
        Publish(session.Store, [.. rows.OrderBy(row => row.NativeTicks)], coverage: Ledger());
        session.Store.ReleaseSegmentReaders();

        string[] compare = ["compare", session.Path, "--baseline", "0:10s", "--interval", "10s:20s", "--group-by", "executable"];
        (InterCatExitCode code, string text, string said) = await Run(compare);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Grouped by +executable, each one's processes together by the path their image was witnessed at\r?$", text);
        Assert.Matches(@"(?m)^  Rows +1 between executables · 0 to other hosts, with records in either interval\r?$", text);
        Assert.Matches(@"(?m)^  client\.exe and server\.exe +TCP +16 · 1\.60/s +16 · 1\.60/s +unchanged\r?$", text);
        string flowing = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("Each row is an executable's processes together, grouped by the path their image was witnessed at, never "
            + "by a name alone.", flowing, Ordinal);
        Assert.Contains("Processes whose executable was not witnessed are in no row, since no path groups them: 1 of their "
            + "relationships and connections to other hosts, with 10 records in the baseline and 8 in the compared interval.",
            flowing, Ordinal);

        // By process, the restart is a relationship that disappeared and another that appeared, and nothing is counted apart.
        (_, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:20s");
        Assert.Matches(@"(?m)^  Grouped by +process\r?$", text);
        Assert.Contains(" disappeared", text, Ordinal);
        Assert.DoesNotContain("Processes whose executable was not witnessed", text, Ordinal);
        Assert.DoesNotContain("Each row is an executable's", text, Ordinal);

        // The same as JSON: each end an executable's group, name and path, and what no path groups counted.
        (_, text, _) = await Run([.. compare, "--json"]);
        JsonElement comparison = JsonDocument.Parse(text).RootElement.GetProperty("comparison");
        Assert.Equal("Executable", comparison.GetProperty("groupedBy").GetString());
        Assert.Equal((1, 10L, 8L), (comparison.GetProperty("unmatched").GetProperty("rows").GetInt32(),
            comparison.GetProperty("unmatched").GetProperty("baselineRecords").GetInt64(),
            comparison.GetProperty("unmatched").GetProperty("comparedRecords").GetInt64()));
        JsonElement row = Assert.Single(comparison.GetProperty("rows").EnumerateArray());
        Assert.Equal(("Executables", "Unchanged", "Unobserved"), (row.GetProperty("kind").GetString(), row.GetProperty("change").GetString(),
            row.GetProperty("connected").GetString()));
        Assert.Equal(("executable:C:\\TOOLS\\CLIENT.EXE", "client.exe", @"C:\Tools\client.exe"), (row.GetProperty("source").GetProperty("group").GetString(),
            row.GetProperty("source").GetProperty("name").GetString(), row.GetProperty("source").GetProperty("path").GetString()));
        Assert.Equal("server.exe", row.GetProperty("target").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("address").ValueKind);
        (_, text, _) = await Run("compare", session.Path, "--baseline", "0:10s", "--interval", "10s:20s", "--json");
        Assert.Equal(("Process", JsonValueKind.Null), (JsonDocument.Parse(text).RootElement.GetProperty("comparison").GetProperty("groupedBy").GetString(),
            JsonDocument.Parse(text).RootElement.GetProperty("comparison").GetProperty("unmatched").ValueKind));

        // Shared redacted, an executable is a token and no process is named.
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-executables-");
        try
        {
            string report = Path.Combine(folder.FullName, "share.json");
            (code, _, said) = await Run([.. compare, "--output", report, "--share-redacted"]);
            Assert.True(code == InterCatExitCode.Success, said);
            string written = File.ReadAllText(report);
            foreach (string kept in new[] { "client.exe", "server.exe", "Tools", "TOOLS", "PID " })
                Assert.DoesNotContain(kept, written, Ordinal);
            JsonElement shared = JsonDocument.Parse(written).RootElement;
            Assert.Equal("Executable", shared.GetProperty("comparison").GetProperty("groupedBy").GetString());
            Assert.Equal(1, shared.GetProperty("comparison").GetProperty("unmatched").GetProperty("rows").GetInt32());
            JsonElement sharedRow = Assert.Single(shared.GetProperty("rows").EnumerateArray());
            Assert.Equal(("Executables", JsonValueKind.Null, JsonValueKind.Null), (sharedRow.GetProperty("kind").GetString(),
                sharedRow.GetProperty("sourceToken").ValueKind, sharedRow.GetProperty("targetToken").ValueKind));
            Assert.StartsWith("executable-", sharedRow.GetProperty("sourceExecutableToken").GetString(), Ordinal);
            Assert.StartsWith("executable-", sharedRow.GetProperty("targetExecutableToken").GetString(), Ordinal);
            Assert.NotEqual(sharedRow.GetProperty("sourceExecutableToken").GetString(), sharedRow.GetProperty("targetExecutableToken").GetString());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "I12: icat compare --baseline-session compares two captures by executable, paired by path")]
    public async Task IcatCompareComparesTwoCaptures()
    {
        // client.exe exchanged with server.exe each second in the first capture, and twice a second in the second, of
        // another host, where each ran under another PID.
        using var first = new TemporarySession();
        using var second = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000756"));
        ObservationRowV1[] Capture(int client, int server, long every)
        {
            List<ObservationRowV1> rows =
            [
                Timed(Lifecycle(1, ObservationKind.Inventory, client, 1) with { ResourceName = @"C:\Tools\client.exe" }),
                Timed(Lifecycle(2, ObservationKind.Inventory, server, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            ];
            for (long at = Second / 2; at < 10 * Second; at += every)
            {
                rows.AddRange(Exchange(at, client, server, "127.0.0.1:50000", "127.0.0.1:8080", 8));
            }

            return [.. rows.OrderBy(row => row.NativeTicks)];
        }

        Publish(first.Store, Capture(100, 200, Second), coverage: Ledger());
        Publish(second.Store, Capture(300, 400, Second / 2), coverage: Ledger(), clock: ClockFor(Clock, "another-host"));
        first.Store.ReleaseSegmentReaders();
        second.Store.ReleaseSegmentReaders();

        string[] compare = ["compare", second.Path, "--baseline", "0:10s", "--interval", "0:10s", "--baseline-session", first.Path];
        (InterCatExitCode code, string text, string said) = await Run(compare);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Compared session +" + Regex.Escape(second.Path) + @"\r?$", text);
        Assert.Matches(@"(?m)^  Baseline session +" + Regex.Escape(first.Path) + @"\r?$", text);
        Assert.Matches(@"(?m)^  Baseline +\[0\.0+, 10\.0+\) s, 10\.0+ s long of the baseline's session\r?$", text);
        Assert.Matches(@"(?m)^  Grouped by +executable, each one's processes together by the path their image was witnessed at\r?$", text);
        Assert.Matches(@"(?m)^  client\.exe and server\.exe +TCP +20 · 2\.00/s +38 · 3\.80/s +up 1\.80/s \(\+90%\)\r?$", text);
        string flowing = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("Each row pairs the processes each capture ran from one path, grouped by the path their image was witnessed "
            + "at and never by a name alone: a grouping, not an identity", flowing, Ordinal);
        Assert.Contains("The captures name two host identities", flowing, Ordinal);

        // As JSON, both sessions and the generation each was read at.
        (_, text, _) = await Run([.. compare, "--json"]);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((second.Path, first.Path), (root.GetProperty("sessionPath").GetString(), root.GetProperty("baselineSessionPath").GetString()));
        JsonElement comparison = root.GetProperty("comparison");
        Assert.Equal((TestSessions.Session, Guid.Parse("0c0c0c0c-0000-4000-8000-000000000756"), false, "Executable"),
            (comparison.GetProperty("baselineSessionId").GetGuid(), comparison.GetProperty("sessionId").GetGuid(),
                comparison.GetProperty("sameHost").GetBoolean(), comparison.GetProperty("groupedBy").GetString()));
        Assert.Equal(1, comparison.GetProperty("baselineGeneration").GetInt64());
        (_, text, _) = await Run("compare", second.Path, "--baseline", "0:10s", "--interval", "0:10s", "--json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("baselineSessionPath").ValueKind);

        // Its difference timeline cuts each capture's interval on its own clock, the pair's columns judged across both.
        (code, text, said) = await Run([.. compare, "--timeline", "1", "--columns", "2"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Baseline session +" + Regex.Escape(first.Path) + @"\r?$", text);
        Assert.Matches(@"(?m)^  Row +1 of 1, client\.exe and server\.exe over TCP\r?$", text);
        Assert.Matches(@"(?m)^  1 +\[0\.0+, 5\.0+\) s +10 · 2\.00/s +\[0\.0+, 5\.0+\) s +18 · 3\.60/s +up 1\.60/s \(\+80%\)\r?$", text);

        // Shared redacted, it says it compared two captures, each on its own clock, and names neither session.
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-captures-");
        try
        {
            string report = Path.Combine(folder.FullName, "share.json");
            (code, _, said) = await Run([.. compare, "--output", report, "--share-redacted"]);
            Assert.True(code == InterCatExitCode.Success, said);
            string written = File.ReadAllText(report);
            foreach (string kept in new[] { first.Path, second.Path, TestSessions.Session.ToString(), "0c0c0c0c", "client.exe" })
                Assert.DoesNotContain(kept, written, OrdinalIgnoreCase);
            JsonElement shared = JsonDocument.Parse(written).RootElement.GetProperty("comparison");
            Assert.Equal((2, false, RedactedShareExport.NoAlignmentAcross), (shared.GetProperty("captures").GetInt32(),
                shared.GetProperty("sameHost").GetBoolean(), shared.GetProperty("alignment").GetString()));
        }
        finally
        {
            folder.Delete(recursive: true);
        }

        // Refused: one session as both captures, and two captures compared by process.
        (code, text, said) = await Run("compare", first.Path, "--baseline", "0:1s", "--interval", "1s:2s", "--baseline-session", first.Path);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
        Assert.StartsWith("x " + SessionComparison.OneSession, said, Ordinal);
        (code, text, said) = await Run([.. compare, "--group-by", "process"]);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
        Assert.StartsWith("x --baseline-session compares two captures by executable: a process of one capture is never another's.",
            said, Ordinal);
        (code, _, said) = await Run("compare", second.Path, "--baseline", "0:1s", "--interval", "1s:2s", "--baseline-session",
            Path.Combine(first.Path, "missing"));
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.StartsWith("x No session directory at ", said, Ordinal);
    }

    [Fact(DisplayName = "R22: icat workspace comparison saves a comparison of an investigation's sessions, and icat compare --saved runs it as saved")]
    public async Task IcatCompareRunsASavedComparison()
    {
        using var first = new TemporarySession();
        using var second = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000758"));
        Publish(first.Store, Rows(), coverage: Ledger());
        Publish(second.Store, Rows(), coverage: Ledger(), capture: CaptureId.New(), clock: ClockFor(Clock, "another-host"));
        first.Store.ReleaseSegmentReaders();
        second.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-saved-comparison-");
        string workspace = Path.Combine(folder.FullName, "update.icat-workspace");
        string a = TestSessions.Session.ToString("N")[..8];
        const string b = "0c0c0c0c";
        static string Unsaved(string text) => Regex.Replace(text, @"(?m)^  Saved as .*\r?\n", string.Empty)
            .Replace("icat compare --workspace <workspace> --saved <name> --json", "icat compare <directory> --json", Ordinal);
        try
        {
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, first.Path, second.Path)).Code);

            // Saved by name, its session named as a member is and each interval as icat compare takes it, by records and of
            // processes unless said otherwise; the investigation then lists it in the same words.
            (InterCatExitCode code, string text, string said) = await Run("workspace", "comparison", workspace, "Before the update",
                "--session", a, "--baseline", "0:10s", "--interval", "10s:30s");
            Assert.True(code == InterCatExitCode.Success, said);
            string words = $"[10.0, 30.0) s of session {a} against [0.0, 10.0) s of it, by records, a row per process";
            Assert.Contains($"Comparison 'Before the update' saved: {words} (comparison revision 1).", said, Ordinal);
            Assert.Contains($"Before the update: {words}. Each interval is in its own session's time, never the investigation's, and "
                + "is read afresh each time the comparison runs: icat compare --workspace <workspace> --saved <name>.",
                Regex.Replace(text, @"\s+", " "), Ordinal);

            // Run, it says what icat compare says given the same, and which comparison it ran; as JSON, the same comparison.
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", " before THE update ");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Saved as +'Before the update', comparison revision 1 of " + Regex.Escape(workspace) + @"\r?$", text);
            (_, string direct, _) = await Run("compare", first.Path, "--baseline", "0:10s", "--interval", "10s:30s");
            Assert.Equal(direct, Unsaved(text));
            Assert.Contains("icat compare --workspace <workspace> --saved <name> --json returns each row", text, Ordinal);
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Before the update", "--timeline", "1",
                "--columns", "2");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Saved as +'Before the update', comparison revision 1 of ", text);
            Assert.Matches(@"(?m)^  Columns +2 columns of each interval, 5\.0+ s of the baseline and 10\.0+ s of the compared interval each\r?$", text);
            (_, text, _) = await Run("compare", "--workspace", workspace, "--saved", "Before the update", "--json");
            JsonElement root = JsonDocument.Parse(text).RootElement;
            JsonElement saved = root.GetProperty("saved");
            Assert.Equal((workspace, "Before the update", 1, first.Path), (saved.GetProperty("workspace").GetString(),
                saved.GetProperty("name").GetString(), saved.GetProperty("revision").GetInt32(), root.GetProperty("sessionPath").GetString()));
            (_, direct, _) = await Run("compare", first.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--json");
            JsonElement unsaved = JsonDocument.Parse(direct).RootElement;
            Assert.Equal(JsonValueKind.Null, unsaved.GetProperty("saved").ValueKind);
            Assert.Equal(unsaved.GetProperty("comparison").GetRawText(), root.GetProperty("comparison").GetRawText());

            // One session's intervals by executable are saved and run so.
            (code, _, said) = await Run("workspace", "comparison", workspace, "By executable", "--session", a, "--baseline", "0:10s",
                "--interval", "10s:30s", "--group-by", "executable");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("[10.0, 30.0) s of session " + a + " against [0.0, 10.0) s of it, by records, a row per executable "
                + "(comparison revision 2).", said, Ordinal);
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "By executable");
            Assert.True(code == InterCatExitCode.Success, said);
            (_, direct, _) = await Run("compare", first.Path, "--baseline", "0:10s", "--interval", "10s:30s", "--group-by", "executable");
            Assert.Equal(direct, Unsaved(text));
            Assert.Contains("Grouped by            executable", text, Ordinal);

            // Two captures' intervals are saved by executable, which they compare by alone, and run as such.
            (code, _, said) = await Run("workspace", "comparison", workspace, "Yesterday", "--session", b, "--baseline-session", a,
                "--baseline", "0:10s", "--interval", "0:10s", "--rank-by", "bytes-sent");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains($"Comparison 'Yesterday' saved: [0.0, 10.0) s of session {b} against [0.0, 10.0) s of session {a}, by bytes "
                + "sent across, a row per executable (comparison revision 3).", said, Ordinal);
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Yesterday");
            Assert.True(code == InterCatExitCode.Success, said);
            (_, direct, _) = await Run("compare", second.Path, "--baseline", "0:10s", "--interval", "0:10s", "--baseline-session",
                first.Path, "--rank-by", "bytes-sent");
            Assert.Equal(direct, Unsaved(text));
            JsonElement comparisons = JsonDocument.Parse((await Run("workspace", "show", workspace, "--json")).Output).RootElement
                .GetProperty("comparisons");
            Assert.Equal((3, "BytesSent", "Executable"), (comparisons.GetArrayLength(), comparisons[2].GetProperty("metric").GetString(),
                comparisons[2].GetProperty("groupedBy").GetString()));

            // A redacted report of it never names it, a name being a person's own words.
            string report = Path.Combine(folder.FullName, "share.json");
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Yesterday", "--output", report, "--share-redacted");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Saved as +'Yesterday', comparison revision 3 of ", text);
            Assert.DoesNotContain("Yesterday", File.ReadAllText(report), OrdinalIgnoreCase);

            // A session not where the investigation recorded it is named, with why; removed, a comparison is not run.
            string recorded = File.ReadAllText(workspace);
            File.WriteAllText(workspace, recorded.Replace(Path.GetFileName(first.Path), "moved-away", Ordinal));
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Before the update");
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
            Assert.StartsWith($"x The comparison's compared session {a} is not where the investigation recorded it: Nothing is at this path",
                said, Ordinal);
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Yesterday");
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
            Assert.StartsWith($"x The comparison's baseline session {a} is not where", said, Ordinal);
            File.WriteAllText(workspace, recorded);
            (code, _, said) = await Run("workspace", "comparison", workspace, "yesterday", "--remove");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("Comparison 'Yesterday' removed (comparison revision 4); its revisions are kept.", said, Ordinal);
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Yesterday");
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
            Assert.StartsWith("x No comparison of this investigation is named 'Yesterday'.", said, Ordinal);

            // Refused: a saved comparison with what it was saved with, either option alone, a workspace that is not there, and
            // a comparison saved wrongly.
            string never = Path.Combine(folder.FullName, "never.icat-workspace");
            (string[] Args, string Said)[] refused =
            [
                (["compare", "--saved", "Before the update"],
                    "x --saved names a comparison saved in an investigation; pass --workspace <workspace> with it."),
                (["compare", "--workspace", workspace],
                    "x --workspace names the investigation a comparison was saved in; pass --saved <name> with it."),
                (["compare", first.Path, "--workspace", workspace, "--saved", "Before the update"],
                    "x --saved runs the comparison as it was saved, its sessions and intervals with it; pass no session directory with it."),
                (["compare", "--workspace", workspace, "--saved", "Before the update", "--interval", "0:1"],
                    "x --saved runs the comparison as it was saved, its sessions and intervals with it; pass no --interval with it."),
                (["compare", "--workspace", workspace, "--saved", "Before the update", "--group-by", "executable"],
                    "x --saved runs the comparison as it was saved, its sessions and intervals with it; pass no --group-by with it."),
                (["compare", "--workspace", workspace, "--saved", "Before the update", "--limit", "0"],
                    "x --limit expects a number of relationships of 1 or more; '0' is not one."),
                (["compare", "--workspace", never, "--saved", "Before the update"], "x No investigation is at "),
                (["workspace", "comparison", workspace, "Again", "--session", a, "--interval", "0:1"],
                    "x Use icat workspace comparison <workspace> <name> (--session <session> --interval <start:end> "),
                (["workspace", "comparison", workspace, "Again", "--remove", "--session", a],
                    "x Use icat workspace comparison <workspace> <name> "),
                (["workspace", "view", workspace, "Again", "0", "1", "--session", a], "x Use icat workspace view <workspace> <name> "),
                (["workspace", "view", workspace, "Again", "0", "1", "--interval", "0:1"], "x Use icat workspace view <workspace> <name> "),
                (["workspace", "comparison", workspace, "Again", "--session", a, "--interval", "0:1", "--baseline", "0:1", "--columns", "9"],
                    "x Use icat workspace comparison <workspace> <name> "),
                (["workspace", "comparison", workspace, "Again", "--session", "ffffffff", "--interval", "0:1", "--baseline", "0:1"],
                    "x No session of this investigation"),
                (["workspace", "comparison", workspace, "Again", "--session", b, "--baseline-session", a, "--interval", "0:1",
                    "--baseline", "0:1", "--group-by", "process"], "x " + InvestigationWorkspace.CapturesByExecutable),
                (["workspace", "comparison", workspace, "Again", "--session", a, "--interval", "0:1", "--baseline", "0:1",
                    "--rank-by", "calls"], "x --rank-by compares records or bytes-sent; 'calls' is neither."),
                (["workspace", "comparison", workspace, "Again", "--session", a, "--interval", "2:1", "--baseline", "0:1"],
                    "x --interval is start:end, "),
                (["workspace", "comparison", workspace, "Again", "--session", a, "--interval", "0:1", "--baseline", "2:1"],
                    "x --baseline is start:end, "),
            ];
            foreach ((string[] args, string expected) in refused)
            {
                (code, string output, string error) = await Run(args);
                string invocation = "icat " + string.Join(' ', args);
                Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
                Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");
                Assert.StartsWith(expected, error, Ordinal);
            }

            Assert.Equal(4, InvestigationWorkspace.Read(workspace).Comparisons.Count);

            // An investigation's file that cannot be read is refused as corrupted input, as icat workspace refuses it.
            string broken = Path.Combine(folder.FullName, "broken.icat-workspace");
            File.WriteAllText(broken, "not an investigation");
            (code, text, said) = await Run("compare", "--workspace", broken, "--saved", "Before the update");
            Assert.Equal((InterCatExitCode.CorruptedInput, string.Empty), (code, text));
            Assert.StartsWith($"x {broken} is not a readable investigation: ", said, Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "R22: a note about a saved comparison or one of its rows is said when the comparison runs, each row's by its rank and ends")]
    public async Task ANotesAboutASavedComparisonAreSaidWhenItRuns()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-noted-comparison-");
        string workspace = Path.Combine(folder.FullName, "update.icat-workspace");
        string a = TestSessions.Session.ToString("N")[..8];
        try
        {
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session.Path)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "comparison", workspace, "Before the update", "--session", a,
                "--baseline", "0:10s", "--interval", "10s:30s")).Code);
            string key = JsonDocument.Parse((await Run("compare", "--workspace", workspace, "--saved", "Before the update", "--json")).Output)
                .RootElement.GetProperty("comparison").GetProperty("rows")[0].GetProperty("key").GetString()!;

            // A note about the whole comparison, one about its first row by key, and one about a row it does not compare.
            (InterCatExitCode code, string text, string said) = await Run("workspace", "note", workspace, "Grew after the patch",
                "--about", "before THE update");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"Note [0-9a-f]{8} added, about the comparison 'Before the update' \(note revision 1\)\.", said);
            (code, _, said) = await Run("workspace", "note", workspace, "The worker's queue", "--about", "Before the update", "--row", key);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains($", about row {key} of the comparison 'Before the update' (note revision 2).", said, Ordinal);
            (code, _, said) = await Run("workspace", "note", workspace, "Gone since", "--about", "Before the update", "--row", "tcp:gone");
            Assert.True(code == InterCatExitCode.Success, said);

            // Run, the comparison says them beneath its rows, a row's by its rank and ends in this run, and one it does not
            // compare as such; its JSON carries them with each row's key and rank.
            (code, text, said) = await Run("compare", "--workspace", workspace, "--saved", "Before the update");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("NOTES ON THIS COMPARISON", text, Ordinal);
            string flowed = Regex.Replace(text, @"\s+", " ");
            Assert.Matches(@"[0-9a-f]{8}: Grew after the patch", flowed);
            Assert.Matches(@"[0-9a-f]{8}, about row 1, PID 100 → 142\.250\.1\.1: The worker's queue", flowed);
            Assert.Matches(@"[0-9a-f]{8}, about row tcp:gone, which this run does not compare: Gone since", flowed);
            JsonElement notes = JsonDocument.Parse((await Run("compare", "--workspace", workspace, "--saved", "Before the update", "--json"))
                .Output).RootElement.GetProperty("saved").GetProperty("notes");
            Assert.Equal(
                [("Grew after the patch", (string?)null, (int?)null), ("The worker's queue", key, 1), ("Gone since", "tcp:gone", null)],
                notes.EnumerateArray().Select(note => (note.GetProperty("text").GetString(), note.GetProperty("row").GetString(),
                    note.GetProperty("rank").ValueKind == JsonValueKind.Null ? (int?)null : note.GetProperty("rank").GetInt32())));

            // The investigation lists them with what they are about; removed, a comparison keeps them, said no longer saved.
            (_, text, _) = await Run("workspace", "show", workspace);
            Assert.Contains("(about the comparison 'Before the update'): Grew after the patch", Regex.Replace(text, @"\s+", " "), Ordinal);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "comparison", workspace, "Before the update", "--remove")).Code);
            (_, text, _) = await Run("workspace", "show", workspace);
            Assert.Contains("(about the comparison 'Before the update', which is no longer saved): Grew after the patch",
                Regex.Replace(text, @"\s+", " "), Ordinal);

            // Refused: a row with no comparison, a note both pinned and about a comparison, or about one never saved.
            foreach ((string[] args, string expected) in new (string[], string)[]
            {
                (["workspace", "note", workspace, "Which?", "--row", key], "x Use icat workspace note <workspace> "),
                (["workspace", "note", workspace, "Both", "--about", "Before the update", "--at", a + "@1"], "x Use icat workspace note "),
                (["workspace", "note", workspace, "Reworded", "--about", "Before the update", "--replace", "x"], "x Use icat workspace note "),
                (["workspace", "view", workspace, "Again", "0", "1", "--about", "Before the update"], "x Use icat workspace view "),
                (["workspace", "note", workspace, "Never", "--about", "Before the update"],
                    "x No comparison of this investigation is named 'Before the update'."),
            })
            {
                (code, string output, string error) = await Run(args);
                string invocation = "icat " + string.Join(' ', args);
                Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
                Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");
                Assert.StartsWith(expected, error, Ordinal);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "R18: icat compare --timeline states a row's difference timeline column by column, adding up to the row")]
    public async Task IcatCompareStatesARowsDifferenceTimeline()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        string[] compare = ["compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s"];

        // The most changed row, 100's connections to 142.250.1.1: its connect at half a second, then from the tenth a send
        // each two seconds, each answered.
        (InterCatExitCode code, string text, string said) = await Run([.. compare, "--timeline", "1", "--columns", "2"]);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Row +1 of 4, PID 100 → 142\.250\.1\.1 over TCP at 443\r?$", text);
        Assert.Matches(@"(?m)^  Whole intervals +1 · 0\.10/s → 20 · 1\.00/s, up 0\.90/s \(\+900%\)\r?$", text);
        Assert.Matches(@"(?m)^  Columns +2 columns of each interval, 5\.0+ s of the baseline and 10\.0+ s of the compared interval each\r?$", text);
        Assert.Matches(@"(?m)^  1 +\[0\.0+, 5\.0+\) s +1 · 0\.20/s +\[10\.0+, 20\.0+\) s +10 · 1\.00/s +up 0\.80/s \(\+400%\)\r?$", text);
        Assert.Matches(@"(?m)^  2 +\[5\.0+, 10\.0+\) s +0 +\[20\.0+, 30\.0+\) s +10 · 1\.00/s +appeared\r?$", text);
        string flowing = Regex.Replace(text, @"\s+", " ");
        Assert.Contains(CompareCommand.TimelineNote, flowing, Ordinal);
        Assert.DoesNotContain(ComparisonText.RatesNote, flowing, Ordinal);

        // As JSON, the comparison with the row's columns, each with its two intervals, adding up to the row.
        (_, text, _) = await Run([.. compare, "--timeline", "1", "--columns", "2", "--json"]);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        JsonElement timeline = root.GetProperty("timeline");
        JsonElement first = root.GetProperty("comparison").GetProperty("rows")[0];
        Assert.Equal((1, first.GetProperty("key").GetString()), (timeline.GetProperty("rank").GetInt32(), timeline.GetProperty("key").GetString()));
        JsonElement[] columns = [.. timeline.GetProperty("columns").EnumerateArray()];
        Assert.Equal(["Increased", "Appeared"], columns.Select(column => column.GetProperty("change").GetString()));
        Assert.Equal((0L, 50_000_000L, 100_000_000L, 200_000_000L), (columns[0].GetProperty("baselineInterval").GetProperty("startTicks").GetInt64(),
            columns[0].GetProperty("baselineInterval").GetProperty("endTicks").GetInt64(),
            columns[0].GetProperty("comparedInterval").GetProperty("startTicks").GetInt64(),
            columns[0].GetProperty("comparedInterval").GetProperty("endTicks").GetInt64()));
        Assert.Equal((first.GetProperty("baseline").GetProperty("records").GetInt64(), first.GetProperty("compared").GetProperty("records").GetInt64()),
            (columns.Sum(column => column.GetProperty("baseline").GetProperty("records").GetInt64()),
                columns.Sum(column => column.GetProperty("compared").GetProperty("records").GetInt64())));
        Assert.Equal((0.8, 1.0), (columns[0].GetProperty("differencePerSecond").GetDouble(), columns[1].GetProperty("differencePerSecond").GetDouble()));
        (_, text, _) = await Run([.. compare, "--json"]);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("timeline").ValueKind);

        // Ten columns by default; written to a file with the comparison, every row with it.
        (_, text, _) = await Run([.. compare, "--timeline", "2"]);
        Assert.Matches(@"(?m)^  Columns +10 columns of each interval, 1\.0+ s of the baseline and 2\.0+ s of the compared interval each\r?$", text);
        Assert.Matches(@"(?m)^  10 +\[9\.0+, 10\.0+\) s ", text);
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-timeline-");
        try
        {
            string file = Path.Combine(folder.FullName, "timeline.json");
            (code, text, said) = await Run([.. compare, "--timeline", "2", "--output", file]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Timeline +of row 2, 10 columns of each interval, ", text);
            JsonElement written = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            Assert.Equal((2, 10, 4), (written.GetProperty("timeline").GetProperty("rank").GetInt32(),
                written.GetProperty("timeline").GetProperty("columns").GetArrayLength(),
                written.GetProperty("comparison").GetProperty("rows").GetArrayLength()));
            Assert.Equal(written.GetProperty("comparison").GetProperty("rows")[1].GetProperty("key").GetString(),
                written.GetProperty("timeline").GetProperty("key").GetString());
        }
        finally
        {
            folder.Delete(recursive: true);
        }

        // Refused: a rank the comparison has no row of, columns without a timeline, too many or too fine columns, and a
        // timeline with --limit or in a redacted report.
        (string[] Args, string Said)[] refused =
        [
            ([.. compare, "--timeline", "5"], "x --timeline 5 names no row: the comparison compares 4 rows."),
            ([.. compare, "--timeline", "0"], "x --timeline names a row by its rank, 1 for the most changed; '0' is not one."),
            ([.. compare, "--timeline", "first"], "x --timeline names a row by its rank, 1 for the most changed; 'first' is not one."),
            ([.. compare, "--columns", "2"], "x --columns cuts a row's difference timeline, which --timeline <rank> asks for."),
            ([.. compare, "--timeline", "1", "--columns", "101"], "x --columns cuts each interval into 1 to 100 columns; '101' is not one."),
            ([.. compare, "--timeline", "1", "--columns", "0"], "x --columns cuts each interval into 1 to 100 columns; '0' is not one."),
            (["compare", session.Path, "--baseline", "0:3", "--interval", "10s:30s", "--timeline", "1", "--columns", "4"],
                "x " + SessionComparison.ColumnsPastTicks),
            ([.. compare, "--timeline", "1", "--limit", "3"], "x --limit lists rows and --timeline one row's columns; pass one of them."),
            ([.. compare, "--timeline", "1", "--output", Path.Combine(Path.GetTempPath(), "never.json"), "--share-redacted"],
                "x A redacted report holds each row over the whole intervals, never a row's timeline; pass one of --timeline and "
                    + "--share-redacted."),
        ];
        foreach ((string[] args, string expected) in refused)
        {
            (code, string output, string error) = await Run(args);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
            Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");
            Assert.StartsWith(expected, error, Ordinal);
        }
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

    [Fact(DisplayName = "I22: icat compare --output writes the comparison whole, or with --share-redacted a report that names no process")]
    public async Task IcatCompareWritesTheComparisonOrARedactedReport()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-output-");
        try
        {
            string[] compare = ["compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s"];

            // A report to share: every row, each end a token, with what the capture covered; the processes' PIDs, the
            // hosts' addresses and the session's folder stay behind.
            string report = Path.Combine(folder.FullName, "share.json");
            (InterCatExitCode code, string text, string said) = await Run([.. compare, "--output", report, "--share-redacted"]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Written to +" + Regex.Escape(report) + @"\r?$", text);
            Assert.Matches(@"(?m)^  Contract +intercat-share-report-v1, as JSON\r?$", text);
            Assert.Matches(@"(?m)^  Rows +4, every one compared\r?$", text);
            Assert.Contains("no name, PID, address or port is written", Regex.Replace(text, @"\s+", " "), Ordinal);
            Assert.Contains("Coverage in the baseline: covered for TCP", text, Ordinal);
            string written = File.ReadAllText(report);
            foreach (string kept in new[] { "PID ", "142.250.1.1", "10.0.0.5", "127.0.0.1", "192.168.1.10", session.Path })
                Assert.DoesNotContain(kept, written, Ordinal);
            using (JsonDocument json = JsonDocument.Parse(written))
            {
                Assert.Equal(("comparison", 4, true), (json.RootElement.GetProperty("kind").GetString(),
                    json.RootElement.GetProperty("rows").GetArrayLength(),
                    json.RootElement.GetProperty("comparison").GetProperty("complete").GetBoolean()));
                Assert.Equal(["OtherHost", "Processes", "OtherHost", "Processes"],
                    json.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("kind").GetString()));
            }

            // Named, the format is the one asked for, whatever the file's name says.
            string named = Path.Combine(folder.FullName, "share.csv.txt");
            (code, _, said) = await Run([.. compare, "--output", named, "--share-redacted", "--format", "json"]);
            Assert.True(code == InterCatExitCode.Success, said);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(named)))
            {
                Assert.Equal("comparison", json.RootElement.GetProperty("kind").GetString());
            }

            // A file there is replaced only when asked, and then with as many rows as --limit asks.
            (code, text, said) = await Run([.. compare, "--output", report, "--share-redacted"]);
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, text));
            Assert.StartsWith($"x {report} exists. Pass --overwrite to replace it.", said, Ordinal);
            Assert.Equal(written, File.ReadAllText(report));
            (code, text, _) = await Run([.. compare, "--output", report, "--share-redacted", "--overwrite", "--limit", "1"]);
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Matches(@"(?m)^  Rows +1 of 4 compared, the most changed; --limit 4 writes every one\r?$", text);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(report)))
            {
                Assert.Equal((1, false), (json.RootElement.GetProperty("rows").GetArrayLength(),
                    json.RootElement.GetProperty("comparison").GetProperty("complete").GetBoolean()));
            }

            // As CSV by its name, a line a row.
            string csv = Path.Combine(folder.FullName, "share.csv");
            (code, text, _) = await Run([.. compare, "--output", csv, "--share-redacted"]);
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Matches(@"(?m)^  Contract +intercat-share-report-v1, as CSV\r?$", text);
            string[] lines = File.ReadAllText(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(5, lines.Length);
            Assert.StartsWith("contract,report_id,policy,kind,", lines[0], Ordinal);

            // Unredacted, the file is the --json document, every row unless --limit says otherwise.
            string full = Path.Combine(folder.FullName, "full.json");
            (code, text, _) = await Run([.. compare, "--output", full]);
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Matches(@"(?m)^  Contract +comparison-v1, as JSON\r?$", text);
            Assert.DoesNotContain("pseudonymized", text, Ordinal);
            (_, string printed, _) = await Run([.. compare, "--json"]);
            Assert.Equal(printed.TrimEnd(), File.ReadAllText(full));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "R18: a file icat compare writes holds every row unless --limit is given, where the screen lists fifty")]
    public async Task AFileHoldsEveryRowUnlessLimited()
    {
        // Fifty-one processes each reached another host once, in the compared interval alone.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, 51).Select(index => Timed(Transfer((15 * Second) + index,
            ObservationKind.Send, AccountingSide.SendSide, 10, 1_000 + index)
            .Between(string.Create(CultureInfo.InvariantCulture, $"192.168.1.10:{20_000 + index}"), "142.250.1.1:443")))],
            coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-every-");
        try
        {
            string[] compare = ["compare", session.Path, "--baseline", "0:10s", "--interval", "10s:30s"];
            (_, string printed, _) = await Run([.. compare, "--json"]);
            Assert.Equal(50, JsonDocument.Parse(printed).RootElement.GetProperty("comparison").GetProperty("rows").GetArrayLength());
            string report = Path.Combine(folder.FullName, "share.json");
            (InterCatExitCode code, string text, string said) = await Run([.. compare, "--output", report, "--share-redacted"]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Rows +51, every one compared\r?$", text);
            Assert.Equal(51, JsonDocument.Parse(File.ReadAllText(report)).RootElement.GetProperty("rows").GetArrayLength());
            string full = Path.Combine(folder.FullName, "full.json");
            (code, _, _) = await Run([.. compare, "--output", full]);
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Equal(51, JsonDocument.Parse(File.ReadAllText(full)).RootElement.GetProperty("comparison").GetProperty("rows")
                .GetArrayLength());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "R18: icat compare refuses what names no comparison, saying what it takes")]
    public async Task IcatCompareRefusesWhatNamesNoComparison()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        string never = Path.Combine(Path.GetTempPath(), $"intercat-compare-{Guid.NewGuid():N}.json");
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
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--group-by", "thread"],
                "x --group-by compares process or executable rows; 'thread' is neither."),
            (["compare", Path.Combine(session.Path, "missing"), "--baseline", "0:1", "--interval", "1:2"], "x No session directory at "),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--share-redacted"],
                "x --share-redacted shapes a file written with --output <path>, which was not given."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--format", "csv"],
                "x --format shapes a file written with --output <path>, which was not given."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--overwrite"],
                "x --overwrite shapes a file written with --output <path>, which was not given."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--output", never, "--json"],
                "x --json prints the comparison and --output writes it to a file; pass one of them."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--output", never, "--share-redacted", "--format", "xml"],
                "x --format writes json or csv; 'xml' is neither."),
            (["compare", session.Path, "--baseline", "0:1", "--interval", "1:2", "--output", never + ".csv"],
                "x Only a redacted report is written as CSV: pass --share-redacted with it, or write the comparison as JSON."),
        ];
        foreach ((string[] args, string expected) in refused)
        {
            (InterCatExitCode code, string output, string error) = await Run(args);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
            Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");
            Assert.StartsWith(expected, error, Ordinal);
        }

        Assert.False(File.Exists(never) || File.Exists(never + ".csv"));
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
