using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Two intervals of one session compared relationship by relationship (`contracts/comparison-v1.md`, plan M12): rates
/// over each whole interval, compared exactly, and a change stated only where both intervals measured alike.
/// </summary>
public sealed class SessionComparisonTests
{
    // Five relationships, each a client's connection to a server of its own; then two more clients of the first server,
    // which the tests by executable run: the first client's next process, and another.
    private static readonly (int Client, int Server, string ClientEnd, string ServerEnd)[] Pairs =
    [
        (100, 200, "127.0.0.1:50000", "127.0.0.1:8080"),
        (300, 400, "127.0.0.1:50001", "127.0.0.1:9090"),
        (500, 600, "127.0.0.1:50002", "127.0.0.1:7070"),
        (700, 800, "127.0.0.1:50003", "127.0.0.1:6060"),
        (900, 1_000, "127.0.0.1:50004", "127.0.0.1:5050"),
        (150, 200, "127.0.0.1:50005", "127.0.0.1:8080"),
        (300, 200, "127.0.0.1:50006", "127.0.0.1:8080"),
        (100, 400, "127.0.0.1:50007", "127.0.0.1:9090"),
    ];

    private const string ClientPath = @"C:\Tools\client.exe", OtherClientPath = @"D:\Other\client.exe";
    private const string ServerPath = @"C:\Tools\server.exe", WorkerPath = @"C:\Tools\worker.exe";

    /// <summary>Another capture than the tests' one session, which every other temporary session holds.</summary>
    private static readonly Guid OtherCapture = Guid.Parse("0c0c0c0c-0000-4000-8000-000000000756");

    [Fact]
    public void AnIntervalComparedWithItselfIsUnchangedEverywhere()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted([.. Every(0, 200, 10, pair: 0), .. Every(5, 200, 25, pair: 1, bytes: 40)]),
            coverage: Covered());

        foreach (ComparisonMetric metric in Enum.GetValues<ComparisonMetric>())
        {
            IntervalComparison same = SessionComparison.Compare(session.Store, new(0, 200), new(0, 200), metric);
            Assert.Equal(2, same.Relationships.Count);
            Assert.All(same.Relationships, row =>
            {
                Assert.Equal((ComparisonChange.Unchanged, (string?)null, false), (row.Change, row.Reason, row.FromLowerBound));
                Assert.Equal(row.Baseline, row.Compared);
            });
            Assert.Equal((metric, EvidencePolicy.IncludeCorrelated, 0), (same.Metric, same.Policy, same.Quiet));
            Assert.Equal(same.BaselineCoverage, same.ComparedCoverage);
        }
    }

    [Fact]
    public void RatesAreOverEachWholeIntervalAndTheMostChangedRankFirst()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            // Steady: an exchange every 10 ticks throughout.
            .. Every(0, 300, 10, pair: 0),
            // A burst: one exchange in the baseline, six in the compared interval.
            .. Exchange(50, pair: 1), .. Every(100, 160, 10, pair: 1),
            // Gone: three exchanges in the baseline alone.
            .. Every(20, 50, 10, pair: 2),
            // New: two exchanges in the compared interval alone.
            .. Exchange(250, pair: 3), .. Exchange(260, pair: 3),
        ]), coverage: Covered());

        // The compared interval is twice the baseline's length: twice the records there is the same rate.
        IntervalComparison comparison = SessionComparison.Compare(session.Store, new(0, 100), new(100, 300));
        Assert.Equal([2, 1, 3, 0], comparison.Relationships.Select(row => PairOf(comparison, row)));
        Assert.Equal(
            [ComparisonChange.Disappeared, ComparisonChange.Increased, ComparisonChange.Appeared, ComparisonChange.Unchanged],
            comparison.Relationships.Select(row => row.Change));
        ComparedRelationship steady = Row(comparison, 0);
        Assert.Equal((20L, 40L), (steady.Baseline.Value!.Value, steady.Compared.Value!.Value));
        Assert.Equal(steady.Baseline.PerSecond(comparison.Baseline), steady.Compared.PerSecond(comparison.Compared));
        Assert.Equal(2_000_000d, steady.Baseline.PerSecond(comparison.Baseline));

        // The rows rank by the difference of their rates, exactly: 12 against 2 records, 0 against 6, 4 against 0.
        ComparedRelationship burst = Row(comparison, 1), gone = Row(comparison, 2), fresh = Row(comparison, 3);
        Assert.Equal(((Int128)800, (Int128)(-1_200), (Int128)400, Int128.Zero),
            (burst.ScaledDifference, gone.ScaledDifference, fresh.ScaledDifference, steady.ScaledDifference));
        Assert.Equal((6L, 0L, 0L, 4L), (gone.Baseline.Records, gone.Compared.Records, fresh.Baseline.Records, fresh.Compared.Records));
        Assert.All(comparison.Relationships, row => Assert.Null(row.Reason));

        // A relationship with no record in either interval has no row, and is counted as quiet.
        IntervalComparison earlier = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200));
        Assert.Equal(1, earlier.Quiet);
        Assert.DoesNotContain(earlier.Relationships, row => PairOf(earlier, row) == 3);
    }

    [Fact]
    public void AProcesssConnectionsToAnotherHostAreComparedAddressByAddress()
    {
        const string Local = "192.168.1.10:50000", SecondLocal = "192.168.1.10:50002", OtherLocal = "192.168.1.10:50001";
        const string Search = "142.250.1.1:443";
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            // The process connected to one address, sent it 9 sends of 100 B and then 20, each answered with 1,000 B, and
            // over a second connection there one send whose size it did not record, answered too.
            Timed(Transfer(1, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, (1 * 16) + 8).Between(Local, Search)),
            .. Outbound(10, 100, 10, Local, Search, slot: 8),
            .. Outbound(100, 200, 5, Local, Search, slot: 8),
            Timed(Transfer(150, ObservationKind.Send, AccountingSide.SendSide, null, 100, (150 * 16) + 12).Between(SecondLocal, Search)),
            Timed(Transfer(151, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 100, (151 * 16) + 12).Between(SecondLocal, Search)),
            // It reached another address in the baseline alone.
            .. Outbound(20, 50, 10, OtherLocal, "142.250.1.2:443", slot: 10),
        ]), coverage: Covered());

        IntervalComparison records = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200));
        Assert.Equal(["142.250.1.1", "142.250.1.2"], records.Relationships.Select(row => row.Host!.Address.Address));
        ComparedRelationship busier = records.Relationships[0], gone = records.Relationships[1];
        Assert.Equal((ComparisonChange.Increased, 19L, 42L), (busier.Change, busier.Baseline.Records, busier.Compared.Records));
        Assert.Equal((ComparisonChange.Disappeared, 6L, 0L), (gone.Change, gone.Baseline.Records, gone.Compared.Records));
        Assert.Null(busier.Relationship);
        Assert.Equal((Mechanism.Tcp, busier.Host!.Link.Key + "@142.250.1.1", 1), (busier.Mechanism, busier.Key, busier.Host.Address.Connected));
        Assert.Equal(100, records.Processes[busier.Host.Link.Process].ProcessId);

        // By bytes, what the process sent there: its sends, never the answers it received, whose sender is not observed;
        // the send whose size it did not record makes the compared interval's a lower bound, which still exceeds the
        // baseline's.
        IntervalComparison bytes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200), ComparisonMetric.BytesSent);
        ComparedRelationship sent = bytes.Relationships.Single(row => row.Host!.Address.Address == "142.250.1.1");
        Assert.Equal((900L, 9L, 0L), (sent.Baseline.Value!.Value, sent.Baseline.Measured, sent.Baseline.Unmeasured));
        Assert.Equal((2_000L, 20L, 1L), (sent.Compared.Value!.Value, sent.Compared.Measured, sent.Compared.Unmeasured));
        Assert.Equal((ComparisonChange.Increased, true), (sent.Change, sent.FromLowerBound));

        // Intervals in which neither address was reached list neither, counting both as quiet.
        IntervalComparison later = SessionComparison.Compare(session.Store, new(200, 300), new(300, 400));
        Assert.Equal((0, 2), (later.Relationships.Count, later.Quiet));
    }

    [Fact(DisplayName = "R22: by executable, two processes run from one path share a row and two paths of one name do not")]
    public void ByExecutableAProcessThatRestartedIsInItsExecutablesRow()
    {
        // client.exe's process exchanged with server.exe until it exited at 99 and its next process from 100; another
        // executable of the same name, run from another folder, exchanged with the server in the compared interval alone.
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            Timed(Lifecycle(99, ObservationKind.Exit, 100, (99 * 16) + 14)),
            Started(100, 150, ClientPath, ObservationKind.Create), Started(100, 300, OtherClientPath, ObservationKind.Create, slot: 15),
            .. Connects(5, pair: 0), .. Every(10, 99, 10, pair: 0), .. Connects(105, pair: 5), .. Every(110, 200, 10, pair: 5),
            .. Connects(145, pair: 6), .. Every(150, 200, 10, pair: 6),
        ]), coverage: Covered());

        // By process, the restart reads as one relationship that disappeared and another that appeared.
        IntervalComparison processes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200));
        Assert.Equal(ComparisonGrouping.Process, processes.Grouping);
        Assert.Equal(
            [ComparisonChange.Appeared, ComparisonChange.Appeared, ComparisonChange.Disappeared],
            processes.Relationships.Select(row => row.Change).Order());

        // By executable, client.exe's two processes are one row, their rate unchanged; the other client.exe is a row of its
        // own, named apart; and no process is taken for another, each still its own instance.
        IntervalComparison executables = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200),
            grouping: ComparisonGrouping.Executable);
        Assert.Equal(ComparisonGrouping.Executable, executables.Grouping);
        Assert.Equal(4, executables.Processes.Count);
        Assert.All(executables.Relationships, row => Assert.Null(row.Relationship));
        ComparedRelationship[] rows = [.. executables.Relationships];
        Assert.Equal(2, rows.Length);
        ComparedRelationship other = rows[0], restarted = rows[1];
        Assert.Equal((ComparisonChange.Appeared, ComparisonChange.Unchanged), (other.Change, restarted.Change));
        Assert.Equal((20L, 20L), (restarted.Baseline.Records, restarted.Compared.Records));
        Assert.Equal((2, 0), (restarted.Executables!.SourceConnected, restarted.Executables.TargetConnected));
        Assert.Equal([(ushort)8080], restarted.Executables.AcceptedPorts);
        Assert.Equal(("executable:" + ClientPath.ToUpperInvariant(), "executable:" + ServerPath.ToUpperInvariant()),
            (restarted.Executables!.Source, restarted.Executables.Target));
        Assert.Equal(("executable:" + ServerPath.ToUpperInvariant(), "executable:" + OtherClientPath.ToUpperInvariant()),
            (other.Executables!.Source, other.Executables.Target));
        Assert.Equal((0, 1), (other.Executables.SourceConnected, other.Executables.TargetConnected));
        Assert.Equal("executables:tcp:" + restarted.Executables.Source + "|" + restarted.Executables.Target, restarted.Key);
        string[] names = [executables.Groups[other.Executables.Target].Name, executables.Groups[restarted.Executables.Source].Name];
        Assert.Equal(["client.exe (Other)", "client.exe (Tools)"], names);
        Assert.Equal(new UnmatchedRows(0, 0, 0), executables.Unmatched);

        // By bytes, what both executables' processes sent across their relationships, together.
        IntervalComparison bytes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200), ComparisonMetric.BytesSent,
            grouping: ComparisonGrouping.Executable);
        ComparedRelationship sent = bytes.Relationships.Single(row => row.Executables!.Source == restarted.Executables.Source);
        Assert.Equal((ComparisonChange.Unchanged, 72L, 72L), (sent.Change, sent.Baseline.Value, sent.Compared.Value));
    }

    [Fact(DisplayName = "R21: by executable, what a process of no witnessed executable is at an end of is counted apart, never grouped")]
    public void ByExecutableWhatNoPathGroupsIsCountedApart()
    {
        const string Local = "192.168.1.10:50000", NextLocal = "192.168.1.10:50001", Search = "142.250.1.1:443";
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 150, ClientPath, ObservationKind.Inventory, slot: 15),
            // client.exe's two processes each connected to one other host and sent to it in both intervals, the second
            // twice as often in the compared one; two processes no record names the executable of exchanged throughout,
            // once more in the baseline, and two others only after both intervals.
            Timed(Transfer(2, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, (2 * 16) + 8).Between(Local, Search)),
            Timed(Transfer(3, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 150, (3 * 16) + 10).Between(NextLocal, Search)),
            .. Outbound(10, 200, 10, Local, Search, slot: 8),
            .. Outbound(15, 100, 20, NextLocal, Search, slot: 10, owner: 150),
            .. Outbound(105, 200, 10, NextLocal, Search, slot: 10, owner: 150),
            .. Every(0, 200, 20, pair: 2), .. Exchange(30, pair: 2), .. Exchange(250, pair: 3),
        ]), coverage: Covered());

        IntervalComparison comparison = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200),
            grouping: ComparisonGrouping.Executable);
        ComparedRelationship reach = Assert.Single(comparison.Relationships);
        Assert.Equal(("executable:" + ClientPath.ToUpperInvariant(), "142.250.1.1", 2, 0),
            (reach.ExecutableHost!.Executable, reach.ExecutableHost.Address, reach.ExecutableHost.Connected, reach.ExecutableHost.Accepted));
        Assert.Equal([(ushort)443], reach.ExecutableHost.Ports);
        Assert.Equal((30L, 40L, ComparisonChange.Increased), (reach.Baseline.Records, reach.Compared.Records, reach.Change));
        Assert.Equal("executable-hosts:tcp:" + reach.ExecutableHost.Executable + "@142.250.1.1", reach.Key);

        // By bytes, what both processes sent there, together: every send measured its size.
        ComparedRelationship sent = Assert.Single(SessionComparison.Compare(session.Store, new(0, 100), new(100, 200),
            ComparisonMetric.BytesSent, grouping: ComparisonGrouping.Executable).Relationships);
        Assert.Equal((1_400L, 14L, 2_000L, 20L, ComparisonChange.Increased),
            (sent.Baseline.Value, sent.Baseline.Measured, sent.Compared.Value, sent.Compared.Measured, sent.Change));

        // The processes of no witnessed executable are in no row: their relationship with a record in either interval, and
        // its records, are counted apart, and the one with none is not counted at all, no executable's row holding it.
        Assert.Equal(new UnmatchedRows(1, 12, 10), comparison.Unmatched);
        Assert.Equal(0, comparison.Quiet);

        // By process, every row is there, the unwitnessed processes' relationships among them, and nothing is counted apart.
        IntervalComparison processes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200));
        Assert.Equal((3, 1), (processes.Relationships.Count, processes.Quiet));
        Assert.Equal(new UnmatchedRows(0, 0, 0), processes.Unmatched);
    }

    [Fact(DisplayName = "I14: two captures of the same records compare as equal, row by row")]
    public void TwoCapturesOfTheSameRecordsCompareAsEqual()
    {
        using var first = new TemporarySession();
        using var second = new TemporarySession(OtherCapture);
        ObservationRowV1[] rows = Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            .. Connects(5, pair: 0), .. Every(10, 200, 10, pair: 0),
            Timed(Transfer(3, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, (3 * 16) + 8)
                .Between("192.168.1.10:50000", "142.250.1.1:443")),
            .. Outbound(10, 200, 20, "192.168.1.10:50000", "142.250.1.1:443", slot: 8),
        ]);

        // The first capture published its records in two generations, the second in one.
        Publish(first.Store, rows[..20], coverage: Covered());
        Publish(first.Store, rows[20..]);
        Publish(second.Store, rows, coverage: Covered());

        foreach (ComparisonMetric metric in Enum.GetValues<ComparisonMetric>())
        {
            IntervalComparison same = SessionComparison.CompareCaptures(first.Store, new(0, 200), second.Store, new(0, 200), metric);
            Assert.Equal(2, same.Relationships.Count);
            Assert.All(same.Relationships, row =>
            {
                Assert.Equal((ComparisonChange.Unchanged, (string?)null), (row.Change, row.Reason));
                Assert.Equal(row.Baseline, row.Compared);
            });
            Assert.Equal((ComparisonGrouping.Executable, (Guid?)TestSessions.Session, OtherCapture, true),
                (same.Grouping, same.BaselineSessionId, same.SessionId, same.SameHost));
            Assert.Equal((2L, 1L), (same.BaselineGeneration, same.Generation));

            // Each pair's and each host's connections are both captures' together: a connect in each.
            Assert.Equal(2, same.Relationships.Single(row => row.ExecutableHost is not null).ExecutableHost!.Connected);
            Assert.Equal(2, same.Relationships.Single(row => row.Executables is not null).Executables!.SourceConnected);
        }

        // Of one session twice, there are no two captures to compare.
        ArgumentException one = Assert.Throws<ArgumentException>(() =>
            SessionComparison.CompareCaptures(first.Store, new(0, 100), first.Store, new(100, 200)));
        Assert.StartsWith(SessionComparison.OneSession, one.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "I12: two captures pair their rows by path alone, never by PID, and keep each capture's processes apart")]
    public void TwoCapturesPairTheirRowsByPath()
    {
        // The baseline's capture: client.exe, PID 100, to server.exe, PID 200; worker.exe, PID 300, to another host; and two
        // processes no record names the executable of. The compared capture, of another host: client.exe as PID 300, its
        // exchanges with server.exe, PID 400, twice as often; another client.exe, from another folder, as PID 100.
        using var first = new TemporarySession();
        using var second = new TemporarySession(OtherCapture);
        Publish(first.Store, Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            Started(2, 300, WorkerPath, ObservationKind.Inventory),
            .. Connects(5, pair: 0), .. Every(10, 200, 10, pair: 0),
            .. Outbound(10, 100, 10, "192.168.1.10:50000", "142.250.1.1:443", slot: 8, owner: 300),
            .. Every(0, 200, 20, pair: 2),
        ]), coverage: Covered());
        Publish(second.Store, Sorted(
        [
            Started(1, 300, ClientPath, ObservationKind.Inventory), Started(1, 400, ServerPath, ObservationKind.Inventory, slot: 15),
            Started(2, 100, OtherClientPath, ObservationKind.Inventory),
            .. Connects(5, pair: 1), .. Every(10, 200, 5, pair: 1), .. Every(100, 200, 10, pair: 7),
        ]), coverage: Covered(), clock: ClockFor(Clock, "another-host"));

        IntervalComparison comparison = SessionComparison.CompareCaptures(first.Store, new(0, 200), second.Store, new(0, 200));
        Assert.Equal(
            [
                ("executables:tcp:executable:" + ClientPath.ToUpperInvariant() + "|executable:" + ServerPath.ToUpperInvariant(),
                    ComparisonChange.Increased, 40L, 78L),
                ("executables:tcp:executable:" + ServerPath.ToUpperInvariant() + "|executable:" + OtherClientPath.ToUpperInvariant(),
                    ComparisonChange.Appeared, 0L, 20L),
                ("executable-hosts:tcp:executable:" + WorkerPath.ToUpperInvariant() + "@142.250.1.1", ComparisonChange.Disappeared, 18L, 0L),
            ],
            comparison.Relationships.Select(row => (row.Key, row.Change, row.Baseline.Records, row.Compared.Records)));

        // The pair's connections are both captures' together, its client connecting in each, to 8080 in one and 9090 in the
        // other.
        ExecutableRelationship pair = comparison.Relationships[0].Executables!;
        Assert.Equal((2, 0), (pair.SourceConnected, pair.TargetConnected));
        Assert.Equal([(ushort)8080, (ushort)9090], pair.AcceptedPorts);

        // Executables are named over both captures: the baseline's one client.exe is told apart from the other's.
        Assert.Equal(["client.exe (Other)", "client.exe (Tools)", "server.exe", "worker.exe"], new[]
        {
            OtherClientPath, ClientPath, ServerPath, WorkerPath,
        }.Select(path => comparison.Groups["executable:" + path.ToUpperInvariant()].Name));

        // Each capture's processes stay its own, PID 100 and PID 300 in each a process of its own; the processes no path
        // groups are counted apart; and the two captures name two host identities.
        Assert.Equal(8, comparison.Processes.Count);
        Assert.Equal(2, comparison.Processes.Values.Count(process => process.ProcessId == 100));
        Assert.Equal(new UnmatchedRows(1, 20, 0), comparison.Unmatched);
        Assert.Equal(false, comparison.SameHost);
    }

    [Fact(DisplayName = "R21: a capture that collected a mechanism otherwise than the other states no change of it")]
    public void CapturesCollectedOtherwiseAreNotComparable()
    {
        using var first = new TemporarySession();
        using var second = new TemporarySession(OtherCapture);
        ObservationRowV1[] rows = Sorted(
        [
            Started(1, 100, ClientPath, ObservationKind.Inventory), Started(1, 200, ServerPath, ObservationKind.Inventory, slot: 15),
            .. Every(10, 200, 10, pair: 0),
        ]);
        Publish(first.Store, rows, coverage: Ledger(Epoch(1, 0, 1_000_000, 10)));
        Publish(second.Store, rows, coverage: Ledger(Epoch(1, 0, 1_000_000, 10, 12)));

        // The second capture collected TCP through one more descriptor, so its counts are of more than the first's.
        ComparedRelationship across = Assert.Single(SessionComparison.CompareCaptures(first.Store, new(0, 200), second.Store,
            new(0, 200)).Relationships);
        Assert.Equal((ComparisonChange.NotComparable, "The two captures collected TCP through other descriptors, so their counts "
            + "were not made alike."), (across.Change, across.Reason));

        // A capture whose own epochs collected it otherwise says which, before the two are weighed.
        using var changed = new TemporarySession(Guid.Parse("0d0d0d0d-0000-4000-8000-000000000756"));
        Publish(changed.Store, rows, coverage: Ledger(Epoch(1, 0, 99, 10), Epoch(2, 100, 1_000_000, 12)));
        ComparedRelationship within = Assert.Single(SessionComparison.CompareCaptures(changed.Store, new(0, 200), first.Store,
            new(0, 200)).Relationships);
        Assert.Equal("The baseline's capture collected TCP through other descriptors in its epoch 2 than in its epoch 1, so its "
            + "counts there were not made alike.", within.Reason);
        ComparedRelationship compared = Assert.Single(SessionComparison.CompareCaptures(first.Store, new(0, 200), changed.Store,
            new(0, 200)).Relationships);
        Assert.StartsWith("The compared interval's capture collected TCP through other descriptors", compared.Reason, StringComparison.Ordinal);

        // A capture that did not collect TCP at all says so of its own interval, whatever the other covered.
        using var silent = new TemporarySession(Guid.Parse("0f0f0f0f-0000-4000-8000-000000000756"));
        Publish(silent.Store, rows, coverage: Ledger(Epoch(1, 0, 1_000_000)));
        Assert.Equal("TCP was not collected over the compared interval: no admitted descriptor records it.",
            Assert.Single(SessionComparison.CompareCaptures(first.Store, new(0, 200), silent.Store, new(0, 200)).Relationships).Reason);

        // Two captures that collected it alike state its change.
        using var alike = new TemporarySession(Guid.Parse("0e0e0e0e-0000-4000-8000-000000000756"));
        Publish(alike.Store, rows, coverage: Ledger(Epoch(1, 0, 1_000_000, 10)));
        Assert.Equal(ComparisonChange.Unchanged, Assert.Single(SessionComparison.CompareCaptures(first.Store, new(0, 200), alike.Store,
            new(0, 200)).Relationships).Change);
    }

    [Fact(DisplayName = "R21: a mechanism the capture stopped collecting makes a relationship not comparable, never disappeared")]
    public void AnUncollectedMechanismIsNotComparableRatherThanDisappeared()
    {
        ObservationRowV1[] rows = Sorted([.. Every(0, 100, 10, pair: 0)]);

        // Collected throughout, a relationship with no record in the compared interval disappeared there.
        using (var collected = new TemporarySession())
        {
            Publish(collected.Store, rows, coverage: Covered());
            ComparedRelationship row = Assert.Single(SessionComparison.Compare(collected.Store, new(0, 100), new(100, 200)).Relationships);
            Assert.Equal((ComparisonChange.Disappeared, (string?)null), (row.Change, row.Reason));
        }

        // TCP collected only until tick 100: the same records state no change, both values with why.
        using var stopped = new TemporarySession();
        Publish(stopped.Store, rows, coverage: Ledger(Epoch(1, 0, 99, 10), Epoch(2, 100, 1_000_000)));
        IntervalComparison comparison = SessionComparison.Compare(stopped.Store, new(0, 100), new(100, 200));
        ComparedRelationship stoppedRow = Assert.Single(comparison.Relationships);
        Assert.Equal(ComparisonChange.NotComparable, stoppedRow.Change);
        Assert.Equal("TCP was not collected over the compared interval: no admitted descriptor records it.", stoppedRow.Reason);
        Assert.Equal((20L, 0L), (stoppedRow.Baseline.Value!.Value, stoppedRow.Compared.Value!.Value));
        Assert.Equal((CoverageState.Covered, CoverageState.NotCollected),
            (stoppedRow.Baseline.Coverage.State, stoppedRow.Compared.Coverage.State));
        Assert.Equal(Int128.Zero, stoppedRow.ScaledDifference);

        // Reversed, the baseline is the interval that collected none: the relationship never appeared there.
        ComparedRelationship reversed = Assert.Single(SessionComparison.Compare(stopped.Store, new(100, 200), new(0, 100)).Relationships);
        Assert.Equal("TCP was not collected over the baseline: no admitted descriptor records it.", reversed.Reason);
    }

    [Fact(DisplayName = "R21: a capture whose sources collected a mechanism otherwise in one interval states no change of it")]
    public void IntervalsCollectedThroughOtherSourcesAreNotComparable()
    {
        using var session = new TemporarySession();
        CoverageLedgerV1 ledger = Ledger(Epoch(1, 0, 99, 10, 12), Epoch(2, 100, 199, 10), Epoch(3, 200, 1_000_000, 12));
        Publish(session.Store, Sorted([.. Every(0, 300, 10, pair: 0)]), coverage: ledger);

        // Equal rates, but its first epoch collected TCP through a descriptor its second did not.
        ComparedRelationship across = Assert.Single(SessionComparison.Compare(session.Store, new(0, 100), new(100, 200)).Relationships);
        Assert.Equal((CoverageState.Covered, CoverageState.Covered), (across.Baseline.Coverage.State, across.Compared.Coverage.State));
        Assert.Equal(ComparisonChange.NotComparable, across.Change);
        Assert.Equal("The capture collected TCP through other descriptors in its epoch 2 than in its epoch 1, so its counts there "
            + "were not made alike.", across.Reason);

        // As many descriptors, but not the same ones.
        Assert.Equal(ComparisonChange.NotComparable,
            Assert.Single(SessionComparison.Compare(session.Store, new(100, 200), new(200, 300)).Relationships).Change);

        // Two intervals of the second epoch were collected alike.
        ComparedRelationship within = Assert.Single(SessionComparison.Compare(session.Store, new(100, 150), new(150, 200)).Relationships);
        Assert.Equal((ComparisonChange.Unchanged, (string?)null), (within.Change, within.Reason));
        Assert.Null(SessionCoverage.CollectedOtherwise(null, Mechanism.Tcp, [new(0, 1)]));

        // Only the mechanism's own descriptors decide: UDP's sources changed where TCP's did not.
        CoverageLedgerV1 udpChanged = Ledger(Epoch(1, 0, 99, [10], udpEvent: 11), Epoch(2, 100, 199, [10], udpEvent: 13));
        Assert.Null(SessionCoverage.CollectedOtherwise(udpChanged, Mechanism.Tcp, [new(0, 200)]));
        Assert.NotNull(SessionCoverage.CollectedOtherwise(udpChanged, Mechanism.Udp, [new(0, 200)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionCoverage.CollectedOtherwise(udpChanged, (Mechanism)0, [new(0, 200)]));
    }

    [Fact]
    public void CoverageShortOfCoveredStatesBothValuesAndNoChange()
    {
        ObservationRowV1[] rows = Sorted([.. Every(0, 100, 10, pair: 0), .. Every(100, 300, 5, pair: 0)]);
        using (var lossy = new TemporarySession())
        {
            Publish(lossy.Store, rows, coverage: TransportLedger(tcp: true, udp: false, lost: 5));
            ComparedRelationship row = Assert.Single(SessionComparison.Compare(lossy.Store, new(0, 100), new(100, 200)).Relationships);
            Assert.Equal(ComparisonChange.NotComparable, row.Change);
            Assert.StartsWith("TCP has a partial gap over the baseline, not extrapolated: ", row.Reason, StringComparison.Ordinal);
            Assert.Equal((20L, 40L), (row.Baseline.Value!.Value, row.Compared.Value!.Value));
        }

        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows);
        ComparedRelationship unknown = Assert.Single(SessionComparison.Compare(legacy.Store, new(0, 100), new(100, 200)).Relationships);
        Assert.Equal("TCP's coverage over the baseline is unknown: this generation publishes no coverage ledger.", unknown.Reason);
    }

    [Fact(DisplayName = "R3: a byte comparison's lower bound decides a change only where the bound alone exceeds the other rate")]
    public void ALowerBoundDecidesOnlyTheChangeItAlreadyExceeds()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            // 200 B, then at least 300 B: more, whatever the unmeasured send carried.
            .. Exchange(10, 0, 100), .. Exchange(20, 0, 100),
            .. Exchange(110, 0, 100), .. Exchange(120, 0, 100), .. Exchange(130, 0, 100), .. Exchange(140, 0, null),
            // 500 B, then at least 100 B: the unmeasured send could make it either.
            .. Every(10, 60, 10, pair: 1, bytes: 100), .. Exchange(110, 1, 100), .. Exchange(120, 1, null),
            // At least 200 B, then 50 B: less, whatever the unmeasured send carried.
            .. Exchange(10, 2, 100), .. Exchange(20, 2, 100), .. Exchange(30, 2, null), .. Exchange(110, 2, 50),
            // Both ends send: 100 B one way and 50 B back, in each interval.
            .. Exchange(10, 3, 100), .. Reply(12, 3, 50), .. Exchange(110, 3, 100), .. Reply(112, 3, 50),
        ]), coverage: Covered());

        IntervalComparison bytes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200), ComparisonMetric.BytesSent);
        ComparedRelationship more = Row(bytes, 0), either = Row(bytes, 1), less = Row(bytes, 2);
        Assert.Equal((ComparisonChange.Increased, true, (string?)null), (more.Change, more.FromLowerBound, more.Reason));
        Assert.Equal((200L, 2L, 0L, true), (more.Baseline.Value!.Value, more.Baseline.Measured, more.Baseline.Unmeasured, more.Baseline.Exact));
        Assert.Equal((300L, 3L, 1L, false), (more.Compared.Value!.Value, more.Compared.Measured, more.Compared.Unmeasured, more.Compared.Exact));
        Assert.Equal((ComparisonChange.Decreased, true), (less.Change, less.FromLowerBound));
        Assert.Equal(ComparisonChange.NotComparable, either.Change);
        Assert.Equal("1 send in the compared interval recorded no size, so what was sent across there is a lower bound, which "
            + "cannot decide whether it rose or fell.", either.Reason);
        Assert.Equal([2, 0, 3, 1], bytes.Relationships.Select(row => PairOf(bytes, row)));

        // What both ends sent across a relationship is each one's sends, each transfer counted once at its sender.
        ComparedRelationship both = Row(bytes, 3);
        Assert.Equal((150L, 2L, 150L, ComparisonChange.Unchanged), (both.Baseline.Value!.Value, both.Baseline.Measured,
            both.Compared.Value!.Value, both.Change));

        // Counted by records, every row is exact, and none rests on a bound.
        IntervalComparison records = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200));
        Assert.Equal(
            (ComparisonChange.Increased, ComparisonChange.Decreased, ComparisonChange.Decreased, ComparisonChange.Unchanged),
            (Row(records, 0).Change, Row(records, 1).Change, Row(records, 2).Change, Row(records, 3).Change));
        Assert.DoesNotContain(records.Relationships, row => row.FromLowerBound);
    }

    [Fact]
    public void SendsWithoutASizeInBothIntervalsOrOnlyThereDecideNothing()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted(
        [
            // At least 100 B in each interval.
            .. Exchange(10, 0, 100), .. Exchange(20, 0, null), .. Exchange(110, 0, 100), .. Exchange(120, 0, null),
            // Only unmeasured sends in the baseline, then 300 B.
            .. Exchange(10, 1, null), .. Exchange(110, 1, 300),
            // Nothing sent in either interval: only receives, which send nothing across.
            Timed(Transfer(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 600, 30 * 16 + 5).Between(Pairs[2].ServerEnd, Pairs[2].ClientEnd)),
            Timed(Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 500, 31 * 16 + 4).Between(Pairs[2].ClientEnd, Pairs[2].ServerEnd)),
            Timed(Transfer(130, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 600, 130 * 16 + 5).Between(Pairs[2].ServerEnd, Pairs[2].ClientEnd)),
            Timed(Transfer(131, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 500, 131 * 16 + 4).Between(Pairs[2].ClientEnd, Pairs[2].ServerEnd)),
            // Only unmeasured sends in the compared interval, after none at all: they appeared, whatever they carried.
            .. Exchange(140, 3, null),
            // At least 100 B, then 300 B: the unmeasured send could make the baseline either.
            .. Exchange(10, 4, 100), .. Exchange(20, 4, null), .. Exchange(110, 4, 300),
        ]), coverage: Covered());

        IntervalComparison bytes = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200), ComparisonMetric.BytesSent);
        Assert.Equal("1 send in the baseline and 1 in the compared interval recorded no size, so what was sent across in each is "
            + "a lower bound, and the two bounds cannot decide whether it rose or fell.", Row(bytes, 0).Reason);
        ComparedRelationship unsized = Row(bytes, 1);
        Assert.Equal((ComparisonChange.NotComparable, (long?)null), (unsized.Change, unsized.Baseline.Value));
        Assert.Equal("1 send in the baseline recorded no size, so what was sent across there is a lower bound, which cannot "
            + "decide whether it rose or fell.", unsized.Reason);
        ComparedRelationship silent = Row(bytes, 2);
        Assert.Equal((ComparisonChange.Unchanged, true, true), (silent.Change, silent.Baseline.None, silent.Compared.None));
        ComparedRelationship appeared = Row(bytes, 3);
        Assert.Equal((ComparisonChange.Appeared, (long?)null, false), (appeared.Change, appeared.Compared.Value, appeared.FromLowerBound));
        Assert.Null(appeared.Compared.PerSecond(bytes.Compared));

        ComparedRelationship below = Row(bytes, 4);
        Assert.Equal((ComparisonChange.NotComparable, 100L, 300L), (below.Change, below.Baseline.Value!.Value, below.Compared.Value!.Value));
        Assert.Equal("1 send in the baseline recorded no size, so what was sent across there is a lower bound, which cannot "
            + "decide whether it rose or fell.", below.Reason);

        // The rows that cannot be compared follow every comparable one, the busier first: by the larger of their two rates
        // of records, scaled by both intervals' lengths.
        Assert.Equal([2, 3], bytes.Relationships.Take(2).Select(row => PairOf(bytes, row)).Order());
        Assert.Equal([0, 4, 1], bytes.Relationships.Skip(2).Select(row => PairOf(bytes, row)));
        Assert.Equal(((Int128)400, (Int128)400, (Int128)200), (Row(bytes, 0).ScaledBusier, Row(bytes, 4).ScaledBusier, Row(bytes, 1).ScaledBusier));
    }

    [Fact]
    public void RpcCallsAreComparedByTheirRecordsAloneJudgedByRpcAndAlpc()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));

        // A byte comparison asks of RPC calls a size they never record, whatever their coverage.
        ComparedRelationship bytes = Assert.Single(SessionComparison.Compare(session.Store, new(0, 150), new(250, 400),
            ComparisonMetric.BytesSent).Relationships);
        Assert.Equal((ComparisonChange.NotComparable, "RPC calls record no size, so no bytes are measured of them in either interval."),
            (bytes.Change, bytes.Reason));

        // By records, an import that cannot show its session recorded the calls states their coverage unknown.
        ComparedRelationship records = Assert.Single(SessionComparison.Compare(session.Store, new(0, 150), new(250, 400)).Relationships);
        Assert.Equal((4L, 4L), (records.Baseline.Records, records.Compared.Records));
        Assert.Equal((Mechanism.Rpc, CoverageState.UnknownCoverage), (records.Baseline.Coverage.Mechanism, records.Baseline.Coverage.State));
        Assert.StartsWith("RPC's coverage over the baseline is unknown: ", records.Reason, StringComparison.Ordinal);

        // A live capture that collected RPC throughout and ALPC only until tick 200: the later call's link was not observed
        // as the earlier's was, so the two are not compared, whatever RPC's own coverage says.
        using var linked = new TemporarySession();
        Publish(linked.Store, rows, fields: fields, coverage: Ledger(RpcEpoch(1, 0, 199, alpc: true), RpcEpoch(2, 200, 1_000_000, alpc: false)));
        ComparedRelationship unlinked = Assert.Single(SessionComparison.Compare(linked.Store, new(0, 150), new(250, 400)).Relationships);
        Assert.Equal((Mechanism.Alpc, CoverageState.NotCollected), (unlinked.Compared.Coverage.Mechanism, unlinked.Compared.Coverage.State));
        Assert.Equal("ALPC was not collected over the compared interval: no admitted descriptor records it.", unlinked.Reason);

        // Both intervals within the first epoch, RPC's and ALPC's coverage alike, the calls compare.
        ComparedRelationship alike = Assert.Single(SessionComparison.Compare(linked.Store, new(0, 150), new(0, 150)).Relationships);
        Assert.Equal((ComparisonChange.Unchanged, Mechanism.Rpc), (alike.Change, alike.Baseline.Coverage.Mechanism));
    }

    /// <summary>
    /// A live epoch over native readings [<paramref name="first"/>, <paramref name="last"/>] that collected RPC's call start,
    /// and ALPC's send when <paramref name="alpc"/> says so, each delivering 3 records and losing none.
    /// </summary>
    private static CoverageEpochV1 RpcEpoch(int number, long first, long last, bool alpc)
    {
        (Guid Provider, string Name, int EventId, int Version, int? Opcode, Mechanism Mechanism)[] collected =
        [
            (RpcProvider, "Microsoft-Windows-RPC", 5, 1, null, Mechanism.Rpc),
            .. alpc ? new[] { (AlpcClass, "Kernel ALPC", 0, 2, (int?)33, Mechanism.Alpc) } : [],
        ];
        return new()
        {
            Epoch = number,
            Acquisition = CoverageAcquisition.LiveCapture,
            FirstDeliveredNativeTicks = first,
            LastDeliveredNativeTicks = last,
            Collected = [.. collected.Select(entry => new CoverageCollectedV1
            {
                ProviderId = entry.Provider, ProviderName = entry.Name, EventId = entry.EventId, Version = entry.Version,
                Opcode = entry.Opcode, Mechanism = entry.Mechanism,
            })],
            Deliveries = [.. collected.Select(entry => new CoverageDeliveryV1
            {
                ProviderId = entry.Provider, EventId = entry.EventId, Version = entry.Version, Opcode = entry.Opcode, Delivered = 3,
                Admitted = 3, Omitted = 0,
            })],
            Losses = [.. Enum.GetValues<LossLayer>().Select(layer => new CoverageLossV1 { Layer = layer, Lost = 0 })],
        };
    }

    [Fact(DisplayName = "I16: a comparison names the generation it answers, reading a session that publishes again until one does")]
    public void AComparisonReadsOneGeneration()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted([.. Every(0, 200, 10, pair: 0)]), coverage: Covered());
        long first = SessionOverviewProjector.Project(session.Store).Generation;

        // A generation published between the first attempt's reads, adding another relationship's records to the first
        // generation's, is read whole by the next attempt.
        int published = 0;
        IntervalComparison comparison = SessionComparison.Compare(session.Store, new(0, 100), new(100, 200), ComparisonMetric.Records,
            EvidencePolicy.IncludeCorrelated, ComparisonGrouping.Process, () =>
            {
                if (published++ == 0)
                {
                    Publish(session.Store, Sorted([.. Every(100, 200, 10, pair: 1)]));
                }
            }, CancellationToken.None);
        Assert.Equal((first + 1, 2), (comparison.Generation, published));
        Assert.Equal([ComparisonChange.Appeared, ComparisonChange.Unchanged], comparison.Relationships.Select(row => row.Change));

        // A session that publishes between every attempt's reads is refused in words.
        long next = 1_000;
        InvalidOperationException unsettled = Assert.Throws<InvalidOperationException>(() => SessionComparison.Compare(session.Store,
            new(0, 100), new(100, 200), ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated, ComparisonGrouping.Process,
            () => Publish(session.Store, Exchange(next++, pair: 2)), CancellationToken.None));
        Assert.Equal(1_000 + SessionComparison.Attempts, next);
        Assert.Equal(SessionComparison.Unsettled, unsettled.Message);
    }

    [Fact]
    public void AComparisonRefusesWhatItCannotCompare()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Sorted([.. Every(0, 100, 10, pair: 0)]), coverage: Covered());
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Compare(session.Store, default, new(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Compare(session.Store, new(0, 1), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Compare(session.Store, new(0, 1), new(0, 1), (ComparisonMetric)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Compare(session.Store, new(0, 1), new(0, 1),
            policy: (EvidencePolicy)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionComparison.Compare(session.Store, new(0, 1), new(0, 1),
            grouping: (ComparisonGrouping)0));
        Assert.Throws<ArgumentNullException>(() => SessionComparison.Compare(null!, new(0, 1), new(0, 1)));
    }

    /// <summary>A ledger whose one live epoch collected TCP and UDP throughout, delivering and losing nothing else.</summary>
    private static CoverageLedgerV1 Covered() => Ledger(Epoch(1, 0, 1_000_000, 10));

    private static CoverageLedgerV1 Ledger(params CoverageEpochV1[] epochs) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = epochs,
    };

    /// <summary>
    /// A live epoch over native readings [<paramref name="first"/>, <paramref name="last"/>] that collected UDP and TCP
    /// through the network provider's <paramref name="tcpEvents"/>, none if none is named, each delivering 3 records.
    /// </summary>
    private static CoverageEpochV1 Epoch(int number, long first, long last, params int[] tcpEvents) =>
        Epoch(number, first, last, tcpEvents, udpEvent: 11);

    /// <summary>The same epoch, collecting UDP through the network provider's <paramref name="udpEvent"/>.</summary>
    private static CoverageEpochV1 Epoch(int number, long first, long last, int[] tcpEvents, int udpEvent)
    {
        (int EventId, Mechanism Mechanism)[] collected = [.. tcpEvents.Select(id => (id, Mechanism.Tcp)), (udpEvent, Mechanism.Udp)];
        return new()
        {
            Epoch = number,
            Acquisition = CoverageAcquisition.LiveCapture,
            FirstDeliveredNativeTicks = first,
            LastDeliveredNativeTicks = last,
            Collected = [.. collected.Select(entry => new CoverageCollectedV1
            {
                ProviderId = NetworkProvider, ProviderName = "network", EventId = entry.EventId, Version = 0, Mechanism = entry.Mechanism,
            })],
            Deliveries = [.. collected.Select(entry => new CoverageDeliveryV1
            {
                ProviderId = NetworkProvider, EventId = entry.EventId, Version = 0, Delivered = 3, Admitted = 3, Omitted = 0,
            })],
            Losses =
            [
                new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
                new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
            ],
        };
    }

    /// <summary>One exchange of a pair at a tick: its client's send and its server's receive, each carrying the bytes given.</summary>
    private static ObservationRowV1[] Exchange(long ticks, int pair, long? bytes = 8)
    {
        (int client, int server, string clientEnd, string serverEnd) = Pairs[pair];
        ulong ordinal = (ulong)((ticks * 16) + (pair * 2));
        return
        [
            Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, bytes, client, ordinal).Between(clientEnd, serverEnd)),
            Timed(Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, bytes, server, ordinal + 1).Between(serverEnd, clientEnd)),
        ];
    }

    /// <summary>A server's answer at a tick: its send of the bytes given and its client's receive of them.</summary>
    private static ObservationRowV1[] Reply(long ticks, int pair, long? bytes)
    {
        (int client, int server, string clientEnd, string serverEnd) = Pairs[pair];
        ulong ordinal = (ulong)((ticks * 16) + (pair * 2));
        return
        [
            Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, bytes, server, ordinal).Between(serverEnd, clientEnd)),
            Timed(Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, bytes, client, ordinal + 1).Between(clientEnd, serverEnd)),
        ];
    }

    /// <summary>A pair's client connecting to its server at a tick: its connect and the server's accept.</summary>
    private static ObservationRowV1[] Connects(long ticks, int pair)
    {
        (int client, int server, string clientEnd, string serverEnd) = Pairs[pair];
        ulong ordinal = (ulong)((ticks * 16) + (pair * 2));
        return
        [
            Timed(Transfer(ticks, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, client, ordinal).Between(clientEnd, serverEnd)),
            Timed(Transfer(ticks, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, server, ordinal + 1).Between(serverEnd, clientEnd)),
        ];
    }

    /// <summary>A pair's exchanges from <paramref name="from"/> up to <paramref name="to"/>, one each <paramref name="step"/> ticks.</summary>
    private static IEnumerable<ObservationRowV1> Every(long from, long to, long step, int pair, long? bytes = 8)
    {
        for (long ticks = from; ticks < to; ticks += step)
        {
            foreach (ObservationRowV1 row in Exchange(ticks, pair, bytes))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// A process's sends of 100 B to another host from <paramref name="from"/> up to <paramref name="to"/>, one each
    /// <paramref name="step"/> ticks, each answered by a receive of 1,000 B, their ordinals in <paramref name="slot"/>; the
    /// process is PID 100 unless <paramref name="owner"/> names another.
    /// </summary>
    private static IEnumerable<ObservationRowV1> Outbound(long from, long to, long step, string local, string remote, int slot,
        int owner = 100)
    {
        for (long ticks = from; ticks < to; ticks += step)
        {
            ulong ordinal = (ulong)((ticks * 16) + slot);
            yield return Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 100, owner, ordinal).Between(local, remote));
            yield return Timed(Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 1_000, owner, ordinal + 1).Between(local, remote));
        }
    }

    /// <summary>
    /// A process's start or rundown at a tick naming the image it ran, as a creation or a rundown carries it; its ordinal in
    /// <paramref name="slot"/>, 14 or 15, which no exchange of the pairs takes.
    /// </summary>
    private static ObservationRowV1 Started(long ticks, int processId, string path, ObservationKind kind, int slot = 14) =>
        Timed(Lifecycle(ticks, kind, processId, (ulong)((ticks * 16) + slot)) with { ResourceName = path });

    /// <summary>Records in the order the source read them.</summary>
    private static ObservationRowV1[] Sorted(IEnumerable<ObservationRowV1> rows) => [.. rows.OrderBy(row => row.RawRecordOrdinal)];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>The pair whose client is at one end of a row's relationship.</summary>
    private static int PairOf(IntervalComparison comparison, ComparedRelationship row) => Array.FindIndex(Pairs, pair =>
        comparison.Processes[row.Relationship!.SourceId].ProcessId == pair.Client
        || comparison.Processes[row.Relationship.TargetId].ProcessId == pair.Client);

    private static ComparedRelationship Row(IntervalComparison comparison, int pair) =>
        comparison.Relationships.Single(row => PairOf(comparison, row) == pair);
}
