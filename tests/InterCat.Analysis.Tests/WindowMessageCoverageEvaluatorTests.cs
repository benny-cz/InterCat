using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The window message coverage evaluator (ADR-088): each truth message is the number of its rank among the capture's
/// messages of its kind from the sender to the receiver, observed with every end where that number has each end in its own
/// process, its other end the receiver; a message names no window and carries no size, so the mechanism is experimental
/// evidence at most.
/// </summary>
public sealed class WindowMessageCoverageEvaluatorTests
{
    private const int Sender = 400;
    private const int Receiver = 700;
    private const int Stranger = 900;

    [Fact(DisplayName = "P27: a whole window message run - every send at both ends, every post and the close queued and retrieved, each end in its own process - is experimental evidence, each message observed with every end and its other end the receiver, bound to no window and no byte criterion measured")]
    public void AWholeRunIsExperimentalEvidence()
    {
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2), Capture(sends: 2, posts: 2), Settings());

        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Equal(["sent", "sent", "posted", "posted", "close"], result.Messages.Select(static message => message.Kind));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], result.Messages.Select(static message => message.MessageNumber!.Value));
        Assert.All(result.Messages, message => Assert.Equal((true, (int?)Receiver, (string?)null), (message.EveryEnd, message.OtherEndProcessId, message.Gap)));
        Assert.Equal((5, 0, 0), (result.MessagesWithEveryEnd, result.MessagesBeyondTruth, result.ObservationsOutsideScope));

        // Every message observed and attributed to the receiver, none falsely; bound to no window, as none is named, and no
        // byte criterion measured, as no record carries a size; the window has no record, so no lifetime.
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal((5L, 5L, 0L, 5L, 0L), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.PeerAttributions, measured.FalsePeerAttributions));
        Assert.Equal((0L, 0L, 1L, 0L, 2L, 2L, true), (measured.ByteEligibleOperations, measured.ByteMeasuredOperations, measured.TruthResources,
            measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships, measured.ResolvedMemberships, measured.ClaimsBytesOrOperations));
        Dictionary<string, TierCriterion> criteria = result.Assessment.Criteria.ToDictionary(static criterion => criterion.Name, StringComparer.Ordinal);
        Assert.Equal((true, true, false, false), (criteria["AdmittedObservations"].Satisfied, criteria["FalsePeerAttribution"].Satisfied,
            criteria["ResourceInstanceBinding"].Satisfied, criteria["ByteMeasurement"].Satisfied));
        Assert.Null(criteria["ByteMeasurement"].Measured);

        // A run not reproduced reaches no tier.
        Assert.Equal(CapabilityTier.Unsupported,
            WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2), Capture(sends: 2, posts: 2), Settings(reproduced: false)).Assessment.Tier);
    }

    [Fact(DisplayName = "R22: a message whose number lacks an end is observed but not with every end, and one whose retrieval binds to a process the truth says did not receive it is a false attribution")]
    public void AMissingEndOrAStrangerIsSaid()
    {
        // The second send's retrieval stop is missing; the first post's retrieval binds to the receiver and to a stranger too,
        // as a number joining two messages would.
        List<WindowMessageObservation> capture = Capture(sends: 2, posts: 2);
        capture.RemoveAll(static record => record is { MessageNumber: 2, Kind: ObservationKind.RequestEnd, Direction: Direction.Inbound });
        capture.Add(Record(100, ObservationKind.Receive, Direction.Inbound, Stranger, 3));
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2), capture, Settings());

        WindowMessageTruthCoverage incomplete = result.Messages[1];
        Assert.Equal((2L, false, (int?)Receiver), (incomplete.MessageNumber, incomplete.EveryEnd, incomplete.OtherEndProcessId));
        Assert.Contains("lacks a send's start or stop", incomplete.Gap, StringComparison.Ordinal);
        WindowMessageTruthCoverage shared = result.Messages[2];
        Assert.Equal((3L, false, (int?)null), (shared.MessageNumber, shared.EveryEnd, shared.OtherEndProcessId));
        Assert.Contains("the truth says did not receive it", shared.Gap, StringComparison.Ordinal);
        Assert.Equal((5L, 5L, 1L), (result.Measurement.TruthOperationsWithAdmittedObservation, result.Measurement.PeerAttributions,
            result.Measurement.FalsePeerAttributions));
        Assert.Equal(3, result.MessagesWithEveryEnd);

        // One false attribution in five is past the 1% ceiling.
        Assert.False(result.Assessment.Criteria.Single(static criterion => criterion.Name == "FalsePeerAttribution").Satisfied);
    }

    [Fact(DisplayName = "R22: where the capture holds more or fewer messages of a kind than the truth logged, no rank says which is which, and none of that kind is matched")]
    public void ACountThatDiffersMatchesNone()
    {
        // The capture began after the first send, and holds a post the truth does not log.
        List<WindowMessageObservation> capture = Capture(sends: 2, posts: 3);
        capture.RemoveAll(static record => record.MessageNumber == 1);
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2), capture, Settings());

        Assert.All(result.Messages, message => Assert.Null(message.MessageNumber));
        Assert.Contains("holds 1 sent messages from the sender to the receiver where the truth logged 2", result.Messages[0].Gap, StringComparison.Ordinal);
        Assert.Contains("holds 4 posted messages from the sender to the receiver where the truth logged 3", result.Messages[2].Gap, StringComparison.Ordinal);
        Assert.Equal((0L, 1), (result.Measurement.TruthOperationsWithAdmittedObservation, result.MessagesBeyondTruth));
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R22: a message the sender sent or posted another process's window, and every record of a process the truth does not name, are neither the run's messages nor error - they are scope")]
    public void OtherWindowsAndProcessesAreScope()
    {
        // The sender also sends a stranger's window one message and posts it another, and the stranger messages a third.
        List<WindowMessageObservation> capture = Capture(sends: 2, posts: 2);
        long ordinal = 1_000;
        capture.AddRange(
        [
            Record(++ordinal, ObservationKind.RequestStart, Direction.Outbound, Sender, 50, peer: Stranger),
            Record(++ordinal, ObservationKind.RequestStart, Direction.Inbound, Stranger, 50),
            Record(++ordinal, ObservationKind.RequestEnd, Direction.Inbound, Stranger, 50),
            Record(++ordinal, ObservationKind.RequestEnd, Direction.Outbound, Sender, 50),
            Record(++ordinal, ObservationKind.Send, Direction.Outbound, Sender, 51),
            Record(++ordinal, ObservationKind.Receive, Direction.Inbound, Stranger, 51),
            Record(++ordinal, ObservationKind.Send, Direction.Outbound, Stranger, 52),
        ]);
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2), capture, Settings());

        Assert.Equal([1L, 2L, 3L, 4L, 5L], result.Messages.Select(static message => message.MessageNumber!.Value));
        Assert.Equal((5, 0, 4), (result.MessagesWithEveryEnd, result.MessagesBeyondTruth, result.ObservationsOutsideScope));
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
    }

    [Fact(DisplayName = "R22: messages to windows made one after another are each the number of its rank, every window's close ranked among the posts where the sender posted it, and each window's two ends resolved by its own messages though no window is discovered")]
    public void WindowsOneAfterAnotherRankEachClose()
    {
        // Three windows, each sent one message and posted two before its close: the gate numbers them 1 to 12 in that order.
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 1, posts: 2, windows: 3),
            Capture(sends: 1, posts: 2, windows: 3), Settings());

        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Equal((12, 0), (result.MessagesWithEveryEnd, result.MessagesBeyondTruth));
        long[] Numbers(string kind) => [.. result.Messages.Where(message => message.Kind == kind).Select(static message => message.MessageNumber!.Value)];
        Assert.Equal([1L, 5L, 9L], Numbers("sent"));
        Assert.Equal([2L, 3L, 6L, 7L, 10L, 11L], Numbers(WindowMessageCoverageEvaluator.PostedStatus));
        Assert.Equal([4L, 8L, 12L], Numbers("close"));
        Assert.Equal([Window(1), Window(2), Window(3)], result.Messages.Where(static message => message.Kind == "close").Select(static message => message.Window));
        Assert.All(result.Messages, message => Assert.Equal(Window((int)((message.MessageNumber!.Value + 3) / 4)), message.Window));

        // Three windows, none discovered, each one's two ends resolved by its own messages.
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal((3L, 0L, 6L, 6L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships));

        // Where every message to the second window is also retrieved by a stranger, as numbers joining two messages would be,
        // that window's receiving end is resolved by none of its messages, and each of them is a false attribution.
        List<WindowMessageObservation> joined = Capture(sends: 1, posts: 2, windows: 3);
        joined.AddRange([.. Enumerable.Range(5, 4).Select(number => Record(100 + number, ObservationKind.Receive, Direction.Inbound, Stranger, number))]);
        WindowMessageCoverageResult shared = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 1, posts: 2, windows: 3), joined, Settings());
        Assert.Equal((5L, 4L), (shared.Measurement.ResolvedMemberships, shared.Measurement.FalsePeerAttributions));
        Assert.All(shared.Messages.Where(static message => message.Window == Window(2)), message => Assert.Null(message.OtherEndProcessId));
    }

    [Fact(DisplayName = "R3: a capture of none of the truth's messages observes none and reaches no tier, its memberships unresolved")]
    public void ACaptureOfNoneObservesNone()
    {
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(Truth(sends: 2, posts: 2),
            [Record(1, ObservationKind.Send, Direction.Outbound, Stranger, 9)], Settings());
        Assert.Equal((0L, 0L, 0L), (result.Measurement.TruthOperationsWithAdmittedObservation, result.Measurement.PeerAttributions,
            result.Measurement.ResolvedMemberships));
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
        Assert.Equal(1, result.ObservationsOutsideScope);
    }

    [Fact(DisplayName = "P27: FX-WMSG-001's committed ledger through the product reproduces its tier - each of its 32 sends and 33 posts the number of its rank, with every end in its own process and its other end the receiver - experimental evidence, in three runs with nothing lost")]
    public void CommittedEvidenceReproducesItsTier()
    {
        (TruthRecord[] truth, WindowMessageObservation[] ledger, Dictionary<string, int> processes) = Committed("FX-WMSG-001", "evidence", "product");
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(truth, ledger, Settings());

        // The first run's ledger: the workload's 194 records, every one of its messages the number of its rank with every end
        // in its own process, none of another process's, so the tier is the evaluator's, not the ledger's word.
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Equal(194, ledger.Length);
        Assert.Equal((32, 32, 1), (result.Messages.Count(static message => message.Kind == "sent"),
            result.Messages.Count(static message => message.Kind == WindowMessageCoverageEvaluator.PostedStatus),
            result.Messages.Count(static message => message.Kind == "close")));
        Assert.Equal((65L, 65, 0L, 0), (result.Measurement.TruthOperationsWithAdmittedObservation, result.MessagesWithEveryEnd,
            result.Measurement.FalsePeerAttributions, result.ObservationsOutsideScope));
        Assert.All(result.Messages, message => Assert.Equal(processes["receiver"], message.OtherEndProcessId));

        // The three runs' report, as the probe wrote it from each capture's own health: each experimental evidence, every
        // message observed with every end, nothing lost, dropped or delivered out of order.
        System.Text.Json.JsonElement[] runs = Runs("window-message-product-*");
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal(("ExperimentalEvidence", 65, 65, 0), (run.GetProperty("tier").GetString(), run.GetProperty("observed").GetInt32(),
                run.GetProperty("withEveryEnd").GetInt32(), run.GetProperty("falsePeerAttributions").GetInt32()));
            AssertNothingLost(run);
        });
    }

    [Fact(DisplayName = "R22: FX-WMSG-002's committed ledger through the product keeps each of its 144 messages to 16 windows made one after another the number of its rank - every window's messages numbers of their own before the next window's, its close the last of them - with every end in its own process and its other end the receiver, experimental evidence in three runs with nothing lost")]
    public void WindowsOneAfterAnotherReproduceTheirTier()
    {
        (TruthRecord[] truth, WindowMessageObservation[] ledger, Dictionary<string, int> processes) = Committed("FX-WMSG-002", "evidence");
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(truth, ledger, Settings(fixture: "FX-WMSG-002"));

        // The first run's ledger: the workload's 416 records, every one of its messages the number of its rank with every end
        // in its own process, none of another process's.
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Equal(416, ledger.Length);
        Assert.Equal((64, 64, 16), (result.Messages.Count(static message => message.Kind == "sent"),
            result.Messages.Count(static message => message.Kind == WindowMessageCoverageEvaluator.PostedStatus),
            result.Messages.Count(static message => message.Kind == "close")));
        Assert.Equal((144L, 144, 0L, 0), (result.Measurement.TruthOperationsWithAdmittedObservation, result.MessagesWithEveryEnd,
            result.Measurement.FalsePeerAttributions, result.ObservationsOutsideScope));
        Assert.All(result.Messages, message => Assert.Equal(processes["receiver"], message.OtherEndProcessId));

        // Sixteen windows, none discovered, each one's two ends resolved by its own messages; in the order the sender opened
        // them, each window's nine messages are numbers of their own, all after the last window's, its close the last of them.
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal((16L, 0L, 32L, 32L), (measured.TruthResources, measured.DiscoveredResourcesWithLifetime, measured.ExpectedMemberships,
            measured.ResolvedMemberships));
        string[] opened = [.. truth.Where(static record => record is { Role: "sender", Kind: TruthEventKind.ObjectOpened })
            .OrderBy(static record => record.Sequence).Select(static record => record.ResourceName!)];
        Assert.Equal(16, opened.Distinct().Count());
        long last = long.MinValue;
        foreach (string window in opened)
        {
            WindowMessageTruthCoverage[] its = [.. result.Messages.Where(message => message.Window == window)];
            long[] numbers = [.. its.Select(static message => message.MessageNumber!.Value).Order()];
            Assert.Equal(9, numbers.Length);
            Assert.True(numbers[0] > last, $"{window}'s messages are not all after the last window's.");
            Assert.Equal(numbers[^1], Assert.Single(its, static message => message.Kind == "close").MessageNumber);
            last = numbers[^1];
        }

        // The three runs' report, as the probe wrote it from each capture's own health: each experimental evidence, every
        // message observed with every end and every window's two ends resolved, nothing lost, dropped or delivered out of order.
        System.Text.Json.JsonElement[] runs = Runs("window-message-generations-product-*");
        Assert.Equal(3, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal(("ExperimentalEvidence", 144, 144, 0), (run.GetProperty("tier").GetString(), run.GetProperty("observed").GetInt32(),
                run.GetProperty("withEveryEnd").GetInt32(), run.GetProperty("falsePeerAttributions").GetInt32()));
            Assert.Equal((16, 32), (run.GetProperty("truthWindows").GetInt32(), run.GetProperty("resolvedMemberships").GetInt32()));
            AssertNothingLost(run);
        });
    }

    // A run's capture lost nothing at its source or consumer, dropped nothing and omitted no record delivered out of order.
    private static void AssertNothingLost(System.Text.Json.JsonElement run)
    {
        System.Text.Json.JsonElement health = run.GetProperty("health");
        Assert.Equal((0L, 0L, 0L, 0L), (health.GetProperty("providerReportedEventLoss").GetInt64(), health.GetProperty("consumerReportedBufferLoss").GetInt64(),
            health.GetProperty("applicationDrops").GetInt64(), health.GetProperty("gateOmissionsOutOfOrder").GetInt64()));
    }

    // A fixture's committed run through the product: its two processes' truth, its ledger as the evaluator reads a session's,
    // and the truth's processes by role.
    private static (TruthRecord[] Truth, WindowMessageObservation[] Ledger, Dictionary<string, int> Processes) Committed(string fixture, params string[] folders)
    {
        string evidence = Path.Combine([RepositoryRoot(), "fixtures", fixture, .. folders]);
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(evidence, "truth-sender.jsonl")), .. ReadTruth(Path.Combine(evidence, "truth-receiver.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(static record => record.Role).ToDictionary(static group => group.Key, static group => group.First().ProcessId);
        return (truth, [.. ReadLedger(Path.Combine(evidence, "ledger.csv"), processes)], processes);
    }

    // The runs of the one report the probe wrote under the bench results folder the pattern names.
    private static System.Text.Json.JsonElement[] Runs(string pattern)
    {
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.GetDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), pattern)), "report.json")));
        return [.. report.RootElement.GetProperty("runs").EnumerateArray().Select(static run => run.Clone())];
    }

    private static IEnumerable<TruthRecord> ReadTruth(string path)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return File.ReadLines(path).Where(line => line.Length > 0).Select(line => System.Text.Json.JsonSerializer.Deserialize<TruthRecord>(line, options)!);
    }

    // A ledger's records as the evaluator reads a session's: each role of the truth its process, each other process one of its
    // own the truth never names, and a send's peer by its role likewise.
    private static IEnumerable<WindowMessageObservation> ReadLedger(string path, Dictionary<string, int> processes)
    {
        int? Process(string role) => role.Length == 0 ? null
            : processes.TryGetValue(role, out int process) ? process
            : -int.Parse(role["other-".Length..], System.Globalization.CultureInfo.InvariantCulture);
        return File.ReadLines(path).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new WindowMessageObservation
        {
            Ordinal = long.Parse(field[0], System.Globalization.CultureInfo.InvariantCulture),
            Kind = Enum.Parse<ObservationKind>(field[3]),
            Direction = Enum.Parse<Direction>(field[4]),
            ProcessId = Process(field[2]),
            MessageNumber = field[5].Length == 0 ? null : long.Parse(field[5], System.Globalization.CultureInfo.InvariantCulture),
            PeerProcessId = Process(field[6]),
        });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    private static WindowMessageCoverageSettings Settings(bool reproduced = true, string fixture = "FX-WMSG-001") => new()
    {
        FixtureId = fixture,
        BuildId = "10.0.26220.0-x64",
        BuildIsSupported = true,
        Reproduced = reproduced,
    };

    // A window's name in a synthetic truth: FX-WMSG-001's one, or the generation's of FX-WMSG-002's several.
    private static string Window(int generation, int windows = 3) =>
        windows == 1 ? "InterCat.FX-WMSG-001.window" : $"InterCat.FX-WMSG-002.window/{generation:D2}";

    /// <summary>
    /// FX-WMSG-001's truth - the sender's sends and posts, numbered, and the close it posts last; the receiver's receipts - or
    /// FX-WMSG-002's, the same to each of several windows one after another, numbered on from the last window's.
    /// </summary>
    private static TruthRecord[] Truth(int sends, int posts, int windows = 1)
    {
        var records = new List<TruthRecord>();
        long sequence = 0;
        TruthRecord Logged(string role, int process, TruthEventKind kind, long? call, string status, string window) => new()
        {
            ScenarioId = windows == 1 ? "FX-WMSG-001" : "FX-WMSG-002",
            Role = role,
            Sequence = ++sequence,
            Kind = kind,
            ProcessId = process,
            ProcessStartFileTime = process,
            MonotonicTicks = sequence,
            RecordedUtc = new DateTimeOffset(2026, 11, 5, 17, 0, 0, TimeSpan.Zero).AddTicks(sequence),
            CallId = call,
            ResourceName = window,
            Status = status,
        };

        for (int generation = 1; generation <= windows; generation++)
        {
            string window = Window(generation, windows);
            for (int call = (generation - 1) * sends + 1; call <= generation * sends; call++)
            {
                records.Add(Logged("receiver", Receiver, TruthEventKind.MessageReceived, call, "WM_COPYDATA", window));
                records.Add(Logged("sender", Sender, TruthEventKind.MessageSent, call, "WM_COPYDATA", window));
            }

            for (int call = (generation - 1) * posts + 1; call <= generation * posts; call++)
            {
                records.Add(Logged("sender", Sender, TruthEventKind.MessageSent, call, WindowMessageCoverageEvaluator.PostedStatus, window));
                records.Add(Logged("receiver", Receiver, TruthEventKind.MessageReceived, call, WindowMessageCoverageEvaluator.PostedStatus, window));
            }

            records.Add(Logged("sender", Sender, TruthEventKind.HandleClosed, null, WindowMessageCoverageEvaluator.WindowStatus, window));
            records.Add(Logged("receiver", Receiver, TruthEventKind.HandleClosed, null, WindowMessageCoverageEvaluator.WindowStatus, window));
        }

        return [.. records];
    }

    /// <summary>
    /// A capture of the run, each message numbered from 1 as the gate numbers them: each window's sends, its posts, its close.
    /// </summary>
    private static List<WindowMessageObservation> Capture(int sends, int posts, int windows = 1)
    {
        var records = new List<WindowMessageObservation>();
        long number = 0;
        for (int generation = 0; generation < windows; generation++)
        {
            for (int send = 0; send < sends; send++)
            {
                number++;
                records.Add(Record(records.Count, ObservationKind.RequestStart, Direction.Outbound, Sender, number, peer: Receiver));
                records.Add(Record(records.Count, ObservationKind.RequestStart, Direction.Inbound, Receiver, number));
                records.Add(Record(records.Count, ObservationKind.RequestEnd, Direction.Inbound, Receiver, number));
                records.Add(Record(records.Count, ObservationKind.RequestEnd, Direction.Outbound, Sender, number));
            }

            for (int post = 0; post <= posts; post++)
            {
                number++;
                records.Add(Record(records.Count, ObservationKind.Send, Direction.Outbound, Sender, number));
                records.Add(Record(records.Count, ObservationKind.Receive, Direction.Inbound, Receiver, number));
            }
        }

        return records;
    }

    private static WindowMessageObservation Record(long ordinal, ObservationKind kind, Direction direction, int process, long number, int? peer = null) => new()
    {
        Ordinal = ordinal,
        Kind = kind,
        Direction = direction,
        ProcessId = process,
        MessageNumber = number,
        PeerProcessId = peer,
    };
}
