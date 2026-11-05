using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>
/// COM call coverage against a truth log (ADR-079, ADR-080): each truth call is matched to its client's COM call on the
/// interface and method the workload called, its peer the process of the server call its link reached; a call of another
/// process is scope, a name's lookup or COM's own call is beyond the truth, and since a COM call carries no size and names no
/// object, the byte criterion is not measured, the interface has no lifetime, and the mechanism stays below traffic
/// visualization.
/// </summary>
public sealed class ComCallCoverageEvaluatorTests
{
    private const int Client = 20, Server = 10, Other = 99;
    private const long GetIdsOfNames = 5, Invoke = 6;
    private static readonly Guid Dispatch = new("00020400-0000-0000-c000-000000000046");
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "P27: a COM call evaluation matches each truth call to its client's call on the interface and method the workload called, its peer the process its link reached, and measures no byte")]
    public void EachCallIsItsClientsCall()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("server", Server, 1, TruthEventKind.CallServed, 11, callId: 0),
            Truth("client", Client, 2, TruthEventKind.CallCompleted, 13, callId: 0, status: "S_OK"),
            Truth("client", Client, 3, TruthEventKind.CallIssued, 20, callId: 1),
            Truth("server", Server, 2, TruthEventKind.CallServed, 21, callId: 1),

            // A method's answer the client logged in hexadecimal, as the capture keeps it: its bits unsigned.
            Truth("client", Client, 4, TruthEventKind.CallCompleted, 23, callId: 1, status: "0x80004002"),
        ];
        ComCallObservation[] calls =
        [
            // Each call's name is looked up first, at both ends: calls the truth does not log.
            Call(0, ComCallSide.Client, Client, 9, Server, GetIdsOfNames, otherEnd: 1, otherEndProcess: Server),
            Call(1, ComCallSide.Server, Server, 9.2, Client, GetIdsOfNames, otherEnd: 0, otherEndProcess: Client),
            Call(2, ComCallSide.Client, Client, 10.1, Server, Invoke, otherEnd: 3, otherEndProcess: Server),
            Call(3, ComCallSide.Server, Server, 11, Client, Invoke, otherEnd: 2, otherEndProcess: Client),
            Call(4, ComCallSide.Client, Client, 19.5, Server, GetIdsOfNames, otherEnd: 5, otherEndProcess: Server),
            Call(5, ComCallSide.Server, Server, 19.6, Client, GetIdsOfNames, otherEnd: 4, otherEndProcess: Client),
            Call(6, ComCallSide.Client, Client, 20.2, Server, Invoke, otherEnd: 7, otherEndProcess: Server, hresult: 0x80004002),
            Call(7, ComCallSide.Server, Server, 21, Client, Invoke, otherEnd: 6, otherEndProcess: Client, hresult: 0x80004002),
            Call(8, ComCallSide.Client, Other, 20.1, 77, Invoke),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        // Each truth call is its client's invoke, linked to the server's invoke of the same truth call, its RPC call served
        // through it by the truth's server, with the answer the client logged.
        Assert.Equal([(0L, 2L, 3L), (1L, 6L, 7L)], result.Calls.Select(call => (call.CallId!.Value, call.ClientCall!.Value, call.ServedCall!.Value)));
        Assert.All(result.Calls, call =>
        {
            Assert.Equal((Server, true, true, true), (call.LinkedProcessId!.Value, call.LinkedToItsServedCall, call.CarrierServedThroughCom, call.PairedByRank));
            Assert.Equal(call.TruthHresult, call.Hresult);
            Assert.Null(call.Gap);
        });
        Assert.Equal((2, 2, 2, 2, 2, 2, 2, 4), (result.TruthCallsServed, result.ServedCallsObserved, result.CallsCompleted, result.HresultsAgreeing,
            result.CallsCarried, result.LinkedToTheirServedCall, result.CarriersServedThroughCom, result.CallsPairedByRank));
        Assert.Equal((0L, 2147500034L, 2147500034L, 2147500034L), (ComCallCoverageEvaluator.HresultOf("S_OK")!.Value,
            ComCallCoverageEvaluator.HresultOf("0x80004002")!.Value, ComCallCoverageEvaluator.HresultOf("-2147467262")!.Value,
            ComCallCoverageEvaluator.HresultOf("2147500034")!.Value));
        Assert.Null(ComCallCoverageEvaluator.HresultOf("E_FAIL"));

        // The lookups are beyond the truth, another process's call is scope.
        Assert.Equal((4, 1), (result.CallsBeyondTruth, result.ObservationsOutsideScope));

        // Every call observed, bound to the interface it names, its peer the truth's server; no byte, and the interface has no
        // lifetime: experimental evidence.
        MechanismMeasurement measurement = result.Measurement;
        Assert.Equal((2L, 2L, 2L, 2L, 0L), (measurement.TruthOperations, measurement.TruthOperationsWithAdmittedObservation,
            measurement.TruthOperationsBoundToResourceInstance, measurement.PeerAttributions, measurement.FalsePeerAttributions));
        Assert.Equal((0L, 0L, 1L, 0L, 2L, 2L), (measurement.ByteEligibleOperations, measurement.ByteMeasuredOperations, measurement.TruthResources,
            measurement.DiscoveredResourcesWithLifetime, measurement.ExpectedMemberships, measurement.ResolvedMemberships));
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
        Assert.Equal(
            ["Byte measurement: not measured; the truth workload declared no eligible item for this criterion.",
                "Resource discovery: measured 0.0 % of 1, below the required 95 %."],
            result.Assessment.Gaps);
    }

    [Fact(DisplayName = "P7: a COM call linked to a server call of a process other than the truth's server is a false peer attribution, and one not linked attributes none")]
    public void APeerIsTheProcessTheLinkReached()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("client", Client, 2, TruthEventKind.CallIssued, 20, callId: 1),
            Truth("client", Client, 3, TruthEventKind.CallIssued, 30, callId: 2),
            Truth("server", Server, 1, TruthEventKind.CallServed, 11, callId: 0),
            Truth("server", Server, 2, TruthEventKind.CallServed, 21, callId: 1),
            Truth("server", Server, 3, TruthEventKind.CallServed, 31, callId: 2),
        ];
        ComCallObservation[] calls =
        [
            // Its link reached a server call of another process, while its start names the server: the link decides.
            Call(0, ComCallSide.Client, Client, 10, Server, Invoke, otherEnd: 9, otherEndProcess: Other),
            Call(1, ComCallSide.Server, Server, 11, Client, Invoke),
            Call(2, ComCallSide.Client, Client, 20, Server, Invoke, link: ComLinkState.NoServedCall),
            Call(3, ComCallSide.Server, Server, 21, Client, Invoke),
            Call(4, ComCallSide.Client, Client, 30, Server, Invoke, otherEnd: 5, otherEndProcess: Server),
            Call(5, ComCallSide.Server, Server, 31, Client, Invoke, otherEnd: 4, otherEndProcess: Client),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        Assert.Equal([Other, null, Server], result.Calls.Select(call => call.LinkedProcessId));
        Assert.Equal([false, false, true], result.Calls.Select(call => call.LinkedToItsServedCall));

        // Its RPC call was served through it by the process its link reached, which is not the truth's server.
        Assert.Equal([false, false, true], result.Calls.Select(call => call.CarrierServedThroughCom));
        Assert.Equal([null, "Observed, but its other end is not linked (NoServedCall).", null], result.Calls.Select(call => call.Gap));
        Assert.Equal((2L, 1L), (result.Measurement.PeerAttributions, result.Measurement.FalsePeerAttributions));
        Assert.Contains("False peer attribution: measured 50.0 % of 2, above the permitted 1 %.", result.Assessment.Gaps);
        Assert.Equal(CapabilityTier.ExperimentalEvidence, result.Assessment.Tier);
    }

    [Fact(DisplayName = "P7: with servers one after another, each truth call's peer is the server whose log served it - a link to another of the truth's servers is a false peer attribution")]
    public void EachCallsPeerIsItsOwnServer()
    {
        const int First = 10, Second = 11;
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("client", Client, 2, TruthEventKind.CallIssued, 20, callId: 1),
            Truth("client", Client, 3, TruthEventKind.CallIssued, 100, callId: 2),
            Truth("client", Client, 4, TruthEventKind.CallIssued, 110, callId: 3),
            Truth("server-00", First, 1, TruthEventKind.CallServed, 11, callId: 0),
            Truth("server-00", First, 2, TruthEventKind.CallServed, 21, callId: 1),
            Truth("server-01", Second, 1, TruthEventKind.CallServed, 101, callId: 2),
            Truth("server-01", Second, 2, TruthEventKind.CallServed, 111, callId: 3),
        ];
        ComCallObservation[] calls =
        [
            Call(0, ComCallSide.Client, Client, 10, First, Invoke, otherEnd: 1, otherEndProcess: First),
            Call(1, ComCallSide.Server, First, 11, Client, Invoke, otherEnd: 0, otherEndProcess: Client),
            Call(2, ComCallSide.Client, Client, 20, First, Invoke, otherEnd: 3, otherEndProcess: First),
            Call(3, ComCallSide.Server, First, 21, Client, Invoke, otherEnd: 2, otherEndProcess: Client),
            Call(4, ComCallSide.Client, Client, 100, Second, Invoke, otherEnd: 5, otherEndProcess: Second),
            Call(5, ComCallSide.Server, Second, 101, Client, Invoke, otherEnd: 4, otherEndProcess: Client),

            // A link reaching the first server's call, while the truth's second server served it.
            Call(6, ComCallSide.Client, Client, 110, Second, Invoke, otherEnd: 3, otherEndProcess: First),
            Call(7, ComCallSide.Server, Second, 111, Client, Invoke),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        // Each server's records pair with its own calls by rank, each call answered by its own server.
        Assert.Equal([(First, 1L), (First, 3L), (Second, 5L), (Second, 7L)], result.Calls.Select(call => (call.ServerProcessId!.Value, call.ServedCall!.Value)));
        Assert.Equal([First, First, Second, First], result.Calls.Select(call => call.LinkedProcessId!.Value));
        Assert.Equal([true, true, true, false], result.Calls.Select(call => call.LinkedToItsServedCall));
        Assert.Equal([true, true, true, false], result.Calls.Select(call => call.CarrierServedThroughCom));
        Assert.Equal((4, 8), (result.ServedCallsObserved, result.CallsPairedByRank));
        Assert.Equal((4L, 1L), (result.Measurement.PeerAttributions, result.Measurement.FalsePeerAttributions));
    }

    [Fact(DisplayName = "P7: with several clients, a truth call is matched to a call of its own client's process alone, never to another client's, though nearer and unclaimed")]
    public void ACallIsItsOwnClientsAlone()
    {
        const int Second = 21;
        TruthRecord[] truth =
        [
            // Two clients each log a call at the same moment; the capture holds the second client's alone, and an invoke of
            // it the truth does not log, so neither client's records pair by rank.
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("second", Second, 1, TruthEventKind.CallIssued, 12, callId: 0),
        ];
        ComCallObservation[] calls =
        [
            Call(0, ComCallSide.Client, Second, 10, Server, Invoke),
            Call(1, ComCallSide.Client, Second, 12, Server, Invoke),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        // The first client's call is a gap; the second's is its own nearest call.
        Assert.Equal([(Client, (long?)null), (Second, 1L)], result.Calls.Select(call => (call.ClientProcessId, call.ClientCall)));
        Assert.Equal((2L, 1L, 0), (result.Measurement.TruthOperations, result.Measurement.TruthOperationsWithAdmittedObservation, result.CallsPairedByRank));
    }

    [Fact(DisplayName = "P7: a call number two servers' logs both hold names no server for it - the call is linked to no served call of the truth's, and its attribution is never counted true")]
    public void ACallNumberTwoServersHoldNamesNoServer()
    {
        const int Second = 21, OtherServer = 11;
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("second", Second, 1, TruthEventKind.CallIssued, 20, callId: 0),
            Truth("server", Server, 1, TruthEventKind.CallServed, 11, callId: 0),
            Truth("other-server", OtherServer, 1, TruthEventKind.CallServed, 21, callId: 0),
        ];
        ComCallObservation[] calls =
        [
            Call(0, ComCallSide.Client, Client, 10, Server, Invoke, otherEnd: 1, otherEndProcess: Server),
            Call(1, ComCallSide.Server, Server, 11, Client, Invoke, otherEnd: 0, otherEndProcess: Client),
            Call(2, ComCallSide.Client, Second, 20, OtherServer, Invoke, otherEnd: 3, otherEndProcess: OtherServer),
            Call(3, ComCallSide.Server, OtherServer, 21, Second, Invoke, otherEnd: 2, otherEndProcess: Second),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        // Each call is observed and linked, but the truth does not say which server served which, so neither is checked true.
        Assert.All(result.Calls, call => Assert.Equal(((int?)null, (long?)null, false, false),
            (call.ServerProcessId, call.ServedCall, call.LinkedToItsServedCall, call.CarrierServedThroughCom)));
        Assert.Equal((2L, 2L, 2L), (result.Measurement.TruthOperationsWithAdmittedObservation, result.Measurement.PeerAttributions,
            result.Measurement.FalsePeerAttributions));
    }

    [Fact(DisplayName = "P27: a truth call with no call of its client's process, interface and method inside the window is a gap, never matched by another process's call, another method or the server's end")]
    public void AnUnmatchedCallIsAGap()
    {
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 10, callId: 0),
            Truth("client", Client, 2, TruthEventKind.CallIssued, 2_000, callId: 1),
        ];
        ComCallObservation[] calls =
        [
            Call(0, ComCallSide.Client, Client, 10, Server, GetIdsOfNames),
            Call(1, ComCallSide.Server, Client, 10, Server, Invoke),
            Call(2, ComCallSide.Client, Other, 10, Server, Invoke),
            Call(3, ComCallSide.Client, Client, 520, Server, Invoke),
            Call(4, ComCallSide.Client, Client, 2_000, Server, Invoke) with { Interface = Guid.Empty },
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        Assert.All(result.Calls, call => Assert.Equal(
            ("No COM call of the client's process on the interface and method the workload called was found inside the match window.", null),
            (call.Gap, call.ClientCall)));
        Assert.Equal((2L, 0L, 0L), (result.Measurement.TruthOperations, result.Measurement.TruthOperationsWithAdmittedObservation,
            result.Measurement.PeerAttributions));
        Assert.Equal(CapabilityTier.Unsupported, result.Assessment.Tier);
        Assert.Contains("No admitted observation and no discovered resource was measured for this mechanism.", result.Assessment.Gaps);

        // A call whose stop the capture did not hold is observed, but its HRESULT stays unknown.
        ComCallCoverageResult open = ComCallCoverageEvaluator.Evaluate(truth[..1],
            [Call(0, ComCallSide.Client, Client, 10, Server, Invoke, link: ComLinkState.NotCompleted) with { State = ComCallState.OpenAtCaptureEnd, Hresult = null }],
            Settings());
        Assert.Equal("Observed, but its start and stop did not pair (OpenAtCaptureEnd), so its HRESULT stays unknown.", Assert.Single(open.Calls).Gap);
        Assert.Equal((1L, 0), (open.Measurement.TruthOperationsWithAdmittedObservation, open.CallsCompleted));
    }

    [Fact(DisplayName = "P8: an end's truth records are its calls by rank only when they are as many and every pair lies inside the window; otherwise each is its nearest call on the truth's time moved by the ranked pairs' offset")]
    public void RankDecidesWhereItCan()
    {
        // The capture's clock reads 300 ms ahead of the truth's, and the client made one invoke the truth does not log.
        TruthRecord[] truth =
        [
            Truth("client", Client, 1, TruthEventKind.CallIssued, 0, callId: 0),
            Truth("client", Client, 2, TruthEventKind.CallIssued, 100, callId: 1),
            Truth("client", Client, 3, TruthEventKind.CallIssued, 200, callId: 2),
            Truth("server", Server, 1, TruthEventKind.CallServed, 1, callId: 0),
            Truth("server", Server, 2, TruthEventKind.CallServed, 101, callId: 1),
            Truth("server", Server, 3, TruthEventKind.CallServed, 201, callId: 2),
        ];
        ComCallObservation[] calls =
        [
            Call(0, ComCallSide.Client, Client, 150, Server, Invoke),
            Call(1, ComCallSide.Client, Client, 300, Server, Invoke, otherEnd: 2, otherEndProcess: Server),
            Call(2, ComCallSide.Server, Server, 301, Client, Invoke, otherEnd: 1, otherEndProcess: Client),
            Call(3, ComCallSide.Client, Client, 400, Server, Invoke, otherEnd: 4, otherEndProcess: Server),
            Call(4, ComCallSide.Server, Server, 401, Client, Invoke, otherEnd: 3, otherEndProcess: Client),
            Call(5, ComCallSide.Client, Client, 500, Server, Invoke, otherEnd: 6, otherEndProcess: Server),
            Call(6, ComCallSide.Server, Server, 501, Client, Invoke, otherEnd: 5, otherEndProcess: Client),
        ];

        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        // The server's records pair by rank and measure the offset; the client's, one call more, are each matched on the
        // truth's time moved by it, never to the extra invoke the truth's own clock would have reached first.
        Assert.Equal((3, TimeSpan.FromMilliseconds(300)), (result.CallsPairedByRank, result.ClockOffset));
        Assert.Equal([(1L, false), (3L, false), (5L, false)], result.Calls.Select(call => (call.ClientCall!.Value, call.PairedByRank)));
        Assert.All(result.Calls, call => Assert.True(call.LinkedToItsServedCall));
        Assert.Equal(1, result.CallsBeyondTruth);

        // As many calls, one pair past the window: nothing pairs by rank, and each record is its nearest call.
        ComCallCoverageResult unranked = ComCallCoverageEvaluator.Evaluate(truth[..3],
            [Call(0, ComCallSide.Client, Client, 0, Server, Invoke), Call(1, ComCallSide.Client, Client, 100, Server, Invoke),
                Call(2, ComCallSide.Client, Client, 900, Server, Invoke)],
            Settings());
        Assert.Equal((0, TimeSpan.Zero), (unranked.CallsPairedByRank, unranked.ClockOffset));
        Assert.Equal([0L, 1L, null], unranked.Calls.Select(call => call.ClientCall));
    }

    [Fact(DisplayName = "P27: FX-COM-001's calls through the product reproduce its measured tier - every truth call its client's call linked to the server call of its own, carried by one RPC call served through it, and no byte")]
    public void TheProductsCallsReproduceTheTier()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-COM-001", "evidence");
        string product = Path.Combine(evidence, "product");
        TruthRecord[] truth = [.. ReadTruth(Path.Combine(product, "truth-server.jsonl")), .. ReadTruth(Path.Combine(product, "truth-client.jsonl"))];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(product, "scenario.json"))))
        {
            Assert.Equal((processes["client"], processes["server"]), (scenario.RootElement.GetProperty("clientProcessId").GetInt32(),
                scenario.RootElement.GetProperty("serverProcessId").GetInt32()));
        }

        ComCallObservation[] calls = [.. ReadLedger(Path.Combine(product, "calls.csv"), processes)];
        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings());

        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs").EnumerateArray()
            .Single(run => run.GetProperty("run").GetString() == "paced-1").GetProperty("evaluation");
        MechanismMeasurement measured = result.Measurement;
        Assert.Equal(
            (stated.GetProperty("truthCalls").GetInt64(), stated.GetProperty("observed").GetInt64(), stated.GetProperty("boundToTheirInterface").GetInt64(),
                stated.GetProperty("peerAttributions").GetInt64(), stated.GetProperty("falsePeerAttributions").GetInt64()),
            (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation, measured.TruthOperationsBoundToResourceInstance,
                measured.PeerAttributions, measured.FalsePeerAttributions));
        Assert.Equal(
            (stated.GetProperty("truthCallsServed").GetInt32(), stated.GetProperty("servedCallsObserved").GetInt32(), stated.GetProperty("completed").GetInt32(),
                stated.GetProperty("hresultsAgreeing").GetInt32(), stated.GetProperty("carried").GetInt32(), stated.GetProperty("linkedToTheirServedCall").GetInt32(),
                stated.GetProperty("carriersServedThroughCom").GetInt32(), stated.GetProperty("callsBeyondTruth").GetInt32()),
            (result.TruthCallsServed, result.ServedCallsObserved, result.CallsCompleted, result.HresultsAgreeing, result.CallsCarried,
                result.LinkedToTheirServedCall, result.CarriersServedThroughCom, result.CallsBeyondTruth));
        Assert.Equal(stated.GetProperty("gaps").EnumerateArray().Select(gap => gap.GetString()), result.Assessment.Gaps);
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Every one of them, exactly: each truth call its client's invoke, answered as the client logged, linked to the
        // server's invoke of the same call, and carried by one RPC call the server's served through it; no call attributed
        // to another process. A COM call carries no byte and names no object, so the tier is experimental evidence.
        Assert.Equal((64L, 64L, 64L, 64L, 0L), (measured.TruthOperations, measured.TruthOperationsWithAdmittedObservation,
            measured.TruthOperationsBoundToResourceInstance, measured.PeerAttributions, measured.FalsePeerAttributions));
        Assert.Equal((64, 64, 64, 64, 64, 64), (result.ServedCallsObserved, result.CallsCompleted, result.HresultsAgreeing, result.CallsCarried,
            result.LinkedToTheirServedCall, result.CarriersServedThroughCom));
        Assert.Equal((0L, 0L, CapabilityTier.ExperimentalEvidence), (measured.ByteEligibleOperations, measured.DiscoveredResourcesWithLifetime, result.Assessment.Tier));

        // The ledger holds the two workload processes' calls alone, by role, every one paired and carried.
        Assert.All(calls, call => Assert.Contains(call.ProcessId, processes.Values));
        Assert.All(calls, call => Assert.Equal((ComCallState.Completed, ComCarrierState.Carried, ComLinkState.Linked), (call.State, call.Carrier, call.Link)));
        Assert.Equal(0, result.ObservationsOutsideScope);
    }

    [Fact(DisplayName = "P7: FX-COM-003's calls through the product, a client following eight servers started one after another, have every call linked to the server instance whose log served it, inside that instance's lifetime, and carried by an RPC call it served through it - none attributed to another instance")]
    public void EachCallReachesTheInstanceThatServedIt()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-COM-003", "evidence");
        TruthRecord[] truth = [.. Directory.EnumerateFiles(evidence, "truth-*.jsonl").Order(StringComparer.Ordinal).SelectMany(ReadTruth)];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        int[] servers;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json"))))
        {
            servers = [.. scenario.RootElement.GetProperty("servers").EnumerateArray().Select(server => server.GetProperty("processId").GetInt32())];
        }

        // Eight server instances, each logging under its place, none the client.
        Assert.Equal(8, servers.Distinct().Count());
        Assert.Equal(servers.Order(), processes.Where(role => role.Key.StartsWith("server-", StringComparison.Ordinal)).Select(role => role.Value).Order());

        ComCallObservation[] calls = [.. ReadLedger(Path.Combine(evidence, "calls.csv"), processes)];
        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings() with { FixtureId = "FX-COM-003" });

        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs").EnumerateArray()
            .Single(run => run.GetProperty("run").GetString() == "restarts-1").GetProperty("evaluation");
        Assert.Equal(
            (stated.GetProperty("truthCalls").GetInt64(), stated.GetProperty("observed").GetInt64(), stated.GetProperty("peerAttributions").GetInt64(),
                stated.GetProperty("falsePeerAttributions").GetInt64()),
            (result.Measurement.TruthOperations, result.Measurement.TruthOperationsWithAdmittedObservation, result.Measurement.PeerAttributions,
                result.Measurement.FalsePeerAttributions));
        Assert.Equal(
            (stated.GetProperty("linkedToTheirServedCall").GetInt32(), stated.GetProperty("carriersServedThroughCom").GetInt32(),
                stated.GetProperty("servedCallsObserved").GetInt32(), stated.GetProperty("callsPairedByRank").GetInt32()),
            (result.LinkedToTheirServedCall, result.CarriersServedThroughCom, result.ServedCallsObserved, result.CallsPairedByRank));
        Assert.Equal(Enum.Parse<CapabilityTier>(stated.GetProperty("tier").GetString()!), result.Assessment.Tier);

        // Each of the 256 calls is linked to the instance whose log served it, 32 to each, its RPC call served through it by
        // that instance; none is attributed to another.
        Assert.Equal((256L, 256L, 0L), (result.Measurement.TruthOperations, result.Measurement.PeerAttributions, result.Measurement.FalsePeerAttributions));
        Assert.All(result.Calls, call =>
        {
            Assert.Equal(call.ServerProcessId, call.LinkedProcessId);
            Assert.True(call.LinkedToItsServedCall && call.CarrierServedThroughCom, $"call {call.CallId}");
        });
        Assert.Equal(servers.Order().Select(server => (server, 32)),
            result.Calls.GroupBy(call => call.LinkedProcessId!.Value).Select(group => (group.Key, group.Count())).OrderBy(pair => pair.Key));

        // And each served call lies inside its own instance's lifetime, as its log states it: from its object's registration
        // to its exit, a millisecond either side for the two clocks.
        long margin = TimeSpan.FromMilliseconds(1).Ticks;
        Assert.All(result.Calls, call =>
        {
            ComCallObservation served = calls.Single(candidate => candidate.Ordinal == call.ServedCall);
            TruthRecord[] lifetime = [.. truth.Where(record => record.ProcessId == call.ServerProcessId
                && record.Kind is TruthEventKind.ListenerBound or TruthEventKind.ProcessExiting)];
            Assert.InRange(served.StartUtcTicks!.Value, lifetime.Min(record => record.RecordedUtc.UtcTicks) - margin,
                lifetime.Max(record => record.RecordedUtc.UtcTicks) + margin);
        });
    }

    [Fact(DisplayName = "P7: FX-COM-004's nested calls through the product - the server calling a second server while serving each call - are calls of their own, each linked to the call that served it inside the one it is nested in, which is linked to its own, none ambiguous")]
    public void ANestedCallIsACallOfItsOwn()
    {
        const long Offset = 1_000_000;
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-COM-004", "evidence");
        TruthRecord[] truth = [.. Directory.EnumerateFiles(evidence, "truth-*.jsonl").Order(StringComparer.Ordinal).SelectMany(ReadTruth)];
        Dictionary<string, int> processes = truth.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        Assert.Equal(["client", "forwarded", "server"], processes.Keys.Order(StringComparer.Ordinal));
        ComCallObservation[] calls = [.. ReadLedger(Path.Combine(evidence, "calls.csv"), processes)];
        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(truth, calls, Settings() with { FixtureId = "FX-COM-004" });

        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        JsonElement stated = measurement.RootElement.GetProperty("throughTheProduct").GetProperty("runs").EnumerateArray()
            .Single(run => run.GetProperty("run").GetString() == "nested-1").GetProperty("evaluation");
        Assert.Equal(
            (stated.GetProperty("truthCalls").GetInt64(), stated.GetProperty("peerAttributions").GetInt64(), stated.GetProperty("falsePeerAttributions").GetInt64(),
                stated.GetProperty("linkedToTheirServedCall").GetInt32(), stated.GetProperty("carriersServedThroughCom").GetInt32()),
            (result.Measurement.TruthOperations, result.Measurement.PeerAttributions, result.Measurement.FalsePeerAttributions,
                result.LinkedToTheirServedCall, result.CarriersServedThroughCom));

        // The client's 64 calls are linked to the server's, and the 64 the server made while serving them to the forwarded
        // server's, each to the call of its own truth call and carried by an RPC call that call's server served through it.
        ComCallTruthCoverage[] made = [.. result.Calls.Where(call => call.ClientProcessId == processes["client"]).OrderBy(call => call.CallId)];
        ComCallTruthCoverage[] nested = [.. result.Calls.Where(call => call.ClientProcessId == processes["server"]).OrderBy(call => call.CallId)];
        Assert.Equal((64, 64), (made.Length, nested.Length));
        Assert.All(made, call => Assert.Equal(processes["server"], call.LinkedProcessId));
        Assert.All(nested, call => Assert.Equal(processes["forwarded"], call.LinkedProcessId));
        Assert.All(result.Calls, call => Assert.True(call.LinkedToItsServedCall && call.CarrierServedThroughCom, $"call {call.CallId}"));

        // Each nested call lies inside the call the server served it from, both of the server's own records.
        Assert.All(made.Zip(nested), pair =>
        {
            Assert.Equal(pair.First.CallId + Offset, pair.Second.CallId);
            ComCallObservation served = calls.Single(call => call.Ordinal == pair.First.ServedCall);
            ComCallObservation inside = calls.Single(call => call.Ordinal == pair.Second.ClientCall);
            Assert.InRange(inside.StartUtcTicks!.Value, served.StartUtcTicks!.Value, served.StopUtcTicks!.Value);
            Assert.InRange(inside.StopUtcTicks!.Value, inside.StartUtcTicks!.Value, served.StopUtcTicks!.Value);
        });

        // And no COM call of the three processes was left uncarried or unlinked: no served call or activity held two calls.
        Assert.All(calls, call => Assert.Equal((ComCarrierState.Carried, ComLinkState.Linked), (call.Carrier, call.Link)));
    }

    private static IEnumerable<TruthRecord> ReadTruth(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return File.ReadLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<TruthRecord>(line, options)!);
    }

    /// <summary>A ledger InterCat.ComProbe --session wrote: each call by its process's role, never its PID.</summary>
    private static IEnumerable<ComCallObservation> ReadLedger(string path, Dictionary<string, int> processes)
    {
        int? Process(string role) => role.Length == 0 ? null : processes.TryGetValue(role, out int id) ? id : -1;
        long? Number(string field) => field.Length == 0 ? null : long.Parse(field, CultureInfo.InvariantCulture);
        return File.ReadLines(path).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new ComCallObservation
        {
            Ordinal = long.Parse(field[0], CultureInfo.InvariantCulture),
            Side = Enum.Parse<ComCallSide>(field[1]),
            ProcessId = processes[field[2]],
            State = Enum.Parse<ComCallState>(field[3]),
            PeerProcessId = Process(field[4]),
            Interface = field[5].Length == 0 ? null : Guid.Parse(field[5], CultureInfo.InvariantCulture),
            Method = Number(field[6]),
            StartUtcTicks = Number(field[7]),
            StopUtcTicks = Number(field[8]),
            Hresult = Number(field[9]),
            Carrier = Enum.Parse<ComCarrierState>(field[10]),
            Link = Enum.Parse<ComLinkState>(field[11]),
            OtherEnd = Number(field[12]),
            OtherEndProcessId = Process(field[13]),
            CarrierPeer = field[14].Length == 0 ? null : Enum.Parse<RpcPeerState>(field[14]),
            CarrierLinkedThroughCom = bool.Parse(field[15]),
            CarrierServedByProcessId = Process(field[16]),
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

    private static ComCallCoverageSettings Settings() => new()
    {
        ExpectedInterface = Dispatch,
        ExpectedMethod = Invoke,
        FixtureId = "FX-COM-001",
        BuildId = "10.0.26220.0-x64",
        BuildIsSupported = true,
        Reproduced = true,
    };

    private static TruthRecord Truth(string role, int process, long sequence, TruthEventKind kind, int milliseconds,
        long? callId = null, string? status = null) => new()
    {
        ScenarioId = "FX-COM-001",
        Role = role,
        Sequence = sequence,
        Kind = kind,
        ProcessId = process,
        ProcessStartFileTime = 1,
        MonotonicTicks = milliseconds,
        RecordedUtc = Start.AddMilliseconds(milliseconds),
        CallId = callId,
        Status = status,
    };

    // A completed call on IDispatch, carried by one RPC call; a client call linked to its other end has its RPC call served
    // through it by the process of that end.
    private static ComCallObservation Call(long ordinal, ComCallSide side, int process, double milliseconds, int peer, long method,
        long? otherEnd = null, int? otherEndProcess = null, ComLinkState? link = null, long hresult = 0) => new()
    {
        Ordinal = ordinal,
        Side = side,
        State = ComCallState.Completed,
        ProcessId = process,
        PeerProcessId = peer,
        Interface = Dispatch,
        Method = method,
        StartUtcTicks = Start.AddMilliseconds(milliseconds).UtcTicks,
        StopUtcTicks = Start.AddMilliseconds(milliseconds + 0.5).UtcTicks,
        Hresult = hresult,
        Carrier = ComCarrierState.Carried,
        Link = link ?? (otherEnd is null ? ComLinkState.NotReached : ComLinkState.Linked),
        OtherEnd = otherEnd,
        OtherEndProcessId = otherEndProcess,
        CarrierPeer = side == ComCallSide.Client && otherEnd is not null ? RpcPeerState.Served : null,
        CarrierLinkedThroughCom = side == ComCallSide.Client && otherEnd is not null,
        CarrierServedByProcessId = side == ComCallSide.Client ? otherEndProcess : null,
    };
}
