using System.Globalization;
using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// What carries a COM call (plan M6, ADR-079): FX-COM-001's exchange under a lab session collecting Microsoft-Windows-COM-Perf
/// beside Microsoft-Windows-RPC. Each COM call is carried by one RPC call at each end - at the client the RPC call of its
/// activity id, at the server the one around it on its thread - on its interface and method, so the two are one call, never
/// two; and the server's COM call carries the client's activity id, which links the call's two ends, and so the RPC call that
/// carried it to the RPC call that served it.
/// </summary>
public sealed class ComLayeringTests
{
    private const string Dispatch = "{00020400-0000-0000-c000-000000000046}";
    private const string LocalRpc = "3";

    private sealed record Row(long Utc, string RaisedIn, string Provider, int Event, string Thread, string Activity, string CallTrace,
        string OtherEnd, string Interface, string Method, string Protocol, string Status);

    private sealed record Call(Row Start, Row Stop);

    [Fact(DisplayName = "P4: FX-COM-001's every COM call is carried by one RPC call at each end - at the client the RPC call of its activity on its thread, at the server the one around it on its thread - on its interface and method, so a COM call beside RPC is one call, never two")]
    public void EachComCallIsCarriedByOneRpcCall()
    {
        Row[] rows = Ledger();
        Call[] clientCom = ComCalls(rows, "client", 39, 40);
        Call[] serverCom = ComCalls(rows, "server", 43, 44);
        Call[] clientRpc = RpcCalls(rows, "client", 5, 7);
        Call[] serverRpc = RpcCalls(rows, "server", 6, 8);

        // Each truth call is one invoke at the client, between its issue and its completion, and one at the server.
        (string Kind, long Utc)[] truth = Truth("truth-client.jsonl");
        (string Kind, long Utc)[] issued = [.. truth.Where(record => record.Kind == "CallIssued")];
        (string Kind, long Utc)[] completed = [.. truth.Where(record => record.Kind == "CallCompleted")];
        Call[] invokes = [.. clientCom.Where(call => call.Start is { Interface: Dispatch, Method: "6" })];
        Assert.Equal((64, 64, 64), (issued.Length, invokes.Length, serverCom.Count(call => call.Start is { Interface: Dispatch, Method: "6" })));
        Assert.All(issued.Zip(completed), call => Assert.Single(invokes, made => made.Start.Utc >= call.First.Utc && made.Start.Utc <= call.Second.Utc));

        // At the client a COM call's carrier is the RPC call of its activity, on its thread, its start inside the COM call and
        // its stop after it: containment in time would join none, and the activity joins each.
        var carried = new HashSet<Call>();
        Assert.Equal(195, clientCom.Length);
        Assert.All(clientCom, call =>
        {
            Call carrier = Assert.Single(clientRpc, rpc => rpc.Start.Activity == call.Start.Activity);
            carried.Add(carrier);
            Assert.Equal((call.Start.Thread, call.Stop.Activity), (carrier.Start.Thread, carrier.Stop.Activity));
            Assert.Equal((call.Start.Interface, call.Start.Method, LocalRpc), (carrier.Start.Interface, carrier.Start.Method, carrier.Start.Protocol));
            Assert.InRange(carrier.Start.Utc, call.Start.Utc, call.Stop.Utc);
            Assert.True(carrier.Stop.Utc > call.Stop.Utc, $"client call {call.Start.CallTrace}'s RPC call stopped before it");
        });

        // The client's other RPC calls are its own, made before its first COM call: none is a COM call RPC records alone.
        long firstComCall = clientCom.Min(call => call.Start.Utc);
        Call[] others = [.. clientRpc.Where(rpc => !carried.Contains(rpc))];
        Assert.Equal(16, others.Length);
        Assert.All(others, rpc => Assert.True(rpc.Stop.Utc < firstComCall));

        // At the server the carrier is the RPC call around the COM call on its thread, with an activity of its own, and every
        // RPC call the server served carried one.
        var serving = new HashSet<Call>();
        Assert.Equal(195, serverCom.Length);
        Assert.All(serverCom, call =>
        {
            Call carrier = Assert.Single(serverRpc, rpc => rpc.Start.Thread == call.Start.Thread && rpc.Start.Utc <= call.Start.Utc && rpc.Stop.Utc >= call.Stop.Utc);
            serving.Add(carrier);
            Assert.Equal((call.Start.Interface, call.Start.Method, LocalRpc), (carrier.Start.Interface, carrier.Start.Method, carrier.Start.Protocol));
            Assert.NotEqual(call.Start.Activity, carrier.Start.Activity);
        });
        Assert.Equal(serverRpc.Length, serving.Count);

        // A COM call's HRESULT is the method's answer and its carrier's status the runtime's: every call COM answered
        // E_NOINTERFACE stopped with RPC status 0, so the HRESULT says what the RPC call cannot.
        Call[] refused = [.. clientCom.Where(call => call.Stop.Status == "0x80004002")];
        Assert.Equal(64, refused.Length);
        Assert.All(refused, call => Assert.Equal("0x00000000", Assert.Single(clientRpc, rpc => rpc.Start.Activity == call.Start.Activity).Stop.Status));
    }

    [Fact(DisplayName = "P7: FX-COM-001's every client COM call has its activity carried by one server COM call naming the client, on its interface and method inside the client's call, which links the RPC call that carried it to the RPC call that served it")]
    public void TheClientsActivityLinksACallsTwoEnds()
    {
        Row[] rows = Ledger();
        Call[] clientCom = ComCalls(rows, "client", 39, 40);
        Call[] serverCom = ComCalls(rows, "server", 43, 44);
        Call[] clientRpc = RpcCalls(rows, "client", 5, 7);
        Call[] serverRpc = RpcCalls(rows, "server", 6, 8);
        Assert.NotEmpty(clientCom);
        Assert.All(clientCom, call =>
        {
            // Exactly one server call carries the client call's activity, each names the other's process, they name one
            // interface and method, and the served call lies inside the client's.
            Call served = Assert.Single(serverCom, other => other.Start.Activity == call.Start.Activity);
            Assert.Equal(("server", "client"), (call.Start.OtherEnd, served.Start.OtherEnd));
            Assert.Equal((call.Start.Interface, call.Start.Method), (served.Start.Interface, served.Start.Method));
            Assert.InRange(served.Start.Utc, call.Start.Utc, call.Stop.Utc);
            Assert.InRange(served.Stop.Utc, served.Start.Utc, call.Stop.Utc);
            Assert.Equal(call.Start.Activity, served.Stop.Activity);

            // So the RPC call carrying the client's call reaches the RPC call that carried the served one, whose own activity
            // no client record carries: RPC's two sides share no id, and COM-Perf's server record is what carries the client's.
            Call carrier = Assert.Single(clientRpc, rpc => rpc.Start.Activity == call.Start.Activity);
            Call serving = Assert.Single(serverRpc, rpc => rpc.Start.Thread == served.Start.Thread && rpc.Start.Utc <= served.Start.Utc && rpc.Stop.Utc >= served.Stop.Utc);
            Assert.Equal((carrier.Start.Interface, carrier.Start.Method), (serving.Start.Interface, serving.Start.Method));
            Assert.DoesNotContain(rows, row => row.RaisedIn == "client" && row.Activity == serving.Start.Activity);
        });

        // Every server call carries a client call's activity, and no two carry one.
        HashSet<string> clientActivities = [.. clientCom.Select(call => call.Start.Activity)];
        Assert.All(serverCom, call => Assert.Contains(call.Start.Activity, clientActivities));
        Assert.Equal(serverCom.Length, serverCom.Select(call => call.Start.Activity).Distinct(StringComparer.Ordinal).Count());

        // Every run the measurement states linked every call so, as its report says: the pairs of ADR-077's causality and
        // rank were the activity's, every client RPC call carrying a COM call reached the one that served it, nothing was
        // linked twice and nothing was lost.
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("layering").GetProperty("runs").EnumerateArray()];
        Assert.Equal(["bursts", "long", "paced", "unpaced"], runs.Select(run => run.GetProperty("run").GetString()).Order(StringComparer.Ordinal));
        Assert.All(runs, run =>
        {
            int calls = run.GetProperty("truth").GetProperty("issued").GetInt32();
            JsonElement client = run.GetProperty("client");
            JsonElement server = run.GetProperty("server");
            JsonElement across = run.GetProperty("across");
            int com = client.GetProperty("comCalls").GetInt32();
            Assert.Equal((com, com, com, com, com), (server.GetProperty("comCalls").GetInt32(), client.GetProperty("comCallsWithOneRpcCallOfTheirActivity").GetInt32(),
                server.GetProperty("comCallsWithOneRpcCallAroundThemOnTheirThread").GetInt32(), across.GetProperty("clientCallsWhoseActivityOneServerCallCarries").GetInt32(),
                across.GetProperty("clientRpcCallsLinkedToTheServerRpcCallThatServedIt").GetInt32()));
            Assert.Equal((calls, calls), (across.GetProperty("invokePairsByCausalityAndRank").GetInt32(), across.GetProperty("invokePairsTheActivityLinksAlike").GetInt32()));
            Assert.Equal((0, 0, 0, 0), (across.GetProperty("serverCallsOfAnActivityNoClientCallCarries").GetInt32(), across.GetProperty("serverCallsLinkedMoreThanOnce").GetInt32(),
                across.GetProperty("serverRpcCallsLinkedMoreThanOnce").GetInt32(), server.GetProperty("rpcCallsCarryingNoComCall").GetInt32()));
            Assert.Equal((0, 0), (run.GetProperty("session").GetProperty("eventsLost").GetInt32(), run.GetProperty("session").GetProperty("realTimeBuffersLost").GetInt32()));
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), run.GetProperty("bench").GetString()!, "report.json")));
            Assert.True(JsonElement.DeepEquals(across, report.RootElement.GetProperty("across")), run.GetProperty("bench").GetString());
            Assert.True(JsonElement.DeepEquals(client, report.RootElement.GetProperty("client")), run.GetProperty("bench").GetString());
            Assert.True(JsonElement.DeepEquals(server, report.RootElement.GetProperty("server")), run.GetProperty("bench").GetString());
        });
    }

    // A COM call: a start joined to its side's next stop of its trace number.
    private static Call[] ComCalls(Row[] rows, string side, int start, int stop)
    {
        var open = new Dictionary<string, Row>(StringComparer.Ordinal);
        var calls = new List<Call>();
        foreach (Row row in rows.Where(row => row.RaisedIn == side && row.Provider == "com-perf" && (row.Event == start || row.Event == stop)))
        {
            if (row.Event == start)
            {
                open[row.CallTrace] = row;
            }
            else
            {
                Assert.True(open.Remove(row.CallTrace, out Row? begun), $"{side} COM stop {row.CallTrace} has no start");
                calls.Add(new(begun!, row));
            }
        }

        Assert.Empty(open);
        return [.. calls];
    }

    // An RPC call: a start joined to its side's next stop of its activity (ADR-031); a start with no activity is its own.
    private static Call[] RpcCalls(Row[] rows, string side, int start, int stop)
    {
        var open = new Dictionary<string, Row>(StringComparer.Ordinal);
        var calls = new List<Call>();
        foreach (Row row in rows.Where(row => row.RaisedIn == side && row.Provider == "rpc" && (row.Event == start || row.Event == stop) && row.Activity.Length > 0))
        {
            if (row.Event == start)
            {
                Assert.True(open.TryAdd(row.Activity, row), $"{side} RPC activity {row.Activity} started twice");
            }
            else
            {
                Assert.True(open.Remove(row.Activity, out Row? begun), $"{side} RPC stop of activity {row.Activity} has no start");
                calls.Add(new(begun!, row));
            }
        }

        Assert.Empty(open);
        return [.. calls];
    }

    private static Row[] Ledger() =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "layering", "ledger.csv")).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(','))
            .Select(field => new Row(long.Parse(field[0], CultureInfo.InvariantCulture), field[1], field[2], int.Parse(field[3], CultureInfo.InvariantCulture),
                field[4], field[5], field[6], field[7], field[8], field[9], field[10], field[11])),
    ];

    private static (string Kind, long Utc)[] Truth(string file) =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "layering", file)).Where(line => line.Length > 0).Select(line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            JsonElement root = record.RootElement;
            return (root.GetProperty("kind").GetString()!, DateTimeOffset.Parse(root.GetProperty("recordedUtc").GetString()!, CultureInfo.InvariantCulture).UtcTicks);
        }),
    ];

    private static string Evidence() => Path.Combine(RepositoryRoot(), "fixtures", "FX-COM-001", "evidence");

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
