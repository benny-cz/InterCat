using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The COM/DCOM/WinRT work package's capability result (plan M6, ADR-077): Microsoft-Windows-COM-Perf, read by a lab session
/// against FX-COM-001's out-of-process calls, records each call at both ends - its interface and method, the other end's
/// process - its start joined to its stop by its trace number; a call's two ends pair by causality and rank, the server's
/// inside the client's call; and its activations carry no field, so none names what was activated or counts as a call.
/// </summary>
public sealed class ComPerfLeadTests
{
    private const string Dispatch = "{00020400-0000-0000-c000-000000000046}";
    private const int Invoke = 6;

    private sealed record Row(long Utc, string RaisedIn, int Event, string CallTrace, string OtherEnd, string Interface, string Method, string Causality, string Hresult);

    [Fact(DisplayName = "P8: FX-COM-001's COM-Perf records pair as measured - each truth call one invoke at the client and one at the server, of its causality and rank, the server's inside the client's call, each start joined to its stop by its trace number")]
    public void TheCallsPairAsMeasured()
    {
        (string Kind, long Call, long Utc)[] client = Truth("truth-client.jsonl");
        (string Kind, long Call, long Utc)[] server = Truth("truth-server.jsonl");
        Row[] rows = Ledger();
        Row[] made = [.. rows.Where(row => row is { RaisedIn: "client", Event: 39, Interface: Dispatch } && row.Method == Invoke.ToString(CultureInfo.InvariantCulture))];
        Row[] served = [.. rows.Where(row => row is { RaisedIn: "server", Event: 43, Interface: Dispatch } && row.Method == Invoke.ToString(CultureInfo.InvariantCulture))];
        (string Kind, long Call, long Utc)[] issued = [.. client.Where(record => record.Kind == "CallIssued")];
        (string Kind, long Call, long Utc)[] completed = [.. client.Where(record => record.Kind == "CallCompleted")];
        Assert.Equal((64, 64, 64, 64), (issued.Length, server.Count(record => record.Kind == "CallServed"), made.Length, served.Length));

        // Each start is joined to its stop by its trace number in the process that raised it, the call answered S_OK.
        foreach ((string side, int start, int stop) in new[] { ("client", 39, 40), ("server", 43, 44) })
        {
            Dictionary<string, Row> stops = rows.Where(row => row.RaisedIn == side && row.Event == stop).ToDictionary(row => row.CallTrace);
            Assert.All(rows.Where(row => row.RaisedIn == side && row.Event == start), row =>
            {
                Assert.True(stops.TryGetValue(row.CallTrace, out Row? ended), $"{side} call {row.CallTrace} has no stop");
                Assert.True(ended!.Utc >= row.Utc);
            });
            Assert.All(made.Concat(served).Where(row => row.RaisedIn == side), row => Assert.Equal("0x00000000", stops[row.CallTrace].Hresult));
        }

        // Each truth call holds exactly one invoke of the client between its issue and its completion.
        Assert.All(issued.Zip(completed), call => Assert.Single(made, row => row.Utc >= call.First.Utc && row.Utc <= call.Second.Utc));

        // A causality names a chain, not a call: the client's invokes are paired with the server's by causality and rank,
        // and each server invoke starts inside the client's call it pairs with.
        Dictionary<string, long> clientStops = rows.Where(row => row is { RaisedIn: "client", Event: 40 }).ToDictionary(row => row.CallTrace, row => row.Utc);
        int pairs = 0;
        foreach (IGrouping<string, Row> chain in made.GroupBy(row => row.Causality))
        {
            Row[] calls = [.. chain.OrderBy(row => row.Utc)];
            Row[] taken = [.. served.Where(row => row.Causality == chain.Key).OrderBy(row => row.Utc)];
            Assert.Equal(calls.Length, taken.Length);
            Assert.All(calls.Zip(taken), pair => Assert.InRange(pair.Second.Utc, pair.First.Utc, clientStops[pair.First.CallTrace]));
            pairs += calls.Length;
        }

        Assert.Equal(64, pairs);
        Assert.InRange(made.Select(row => row.Causality).Distinct().Count(), 1, 2);
    }

    [Fact(DisplayName = "P7: FX-COM-001's every COM-Perf call names the other workload process at both ends, its threads pairing nothing, and its activations name nothing to count as a call")]
    public void EveryCallNamesItsOtherEnd()
    {
        Row[] rows = Ledger();

        // Every call start of either process names the other one, never a third; the client's calls beyond the truth's -
        // GetIDsOfNames and IRundown's remote QueryInterface, AddRef and Release - name it alike.
        Assert.All(rows.Where(row => row.Event == 39), row => Assert.Equal(("client", "server"), (row.RaisedIn, row.OtherEnd)));
        Assert.All(rows.Where(row => row.Event == 43), row => Assert.Equal(("server", "client"), (row.RaisedIn, row.OtherEnd)));
        Assert.Contains(rows, row => row is { Event: 39, Interface: Dispatch, Method: "5" });
        Assert.Contains(rows, row => row is { Event: 39, Interface: "{00000134-0000-0000-c000-000000000046}" });

        // An activation carries no field: nothing names what was activated or its other end, and no call is counted of it.
        Row[] activations = [.. rows.Where(row => row.Event is 5 or 6 or 11 or 12 or 35 or 36)];
        Assert.NotEmpty(activations);
        Assert.All(activations, row => Assert.Equal((string.Empty, string.Empty, string.Empty), (row.Interface, row.Method, row.OtherEnd)));

        // Every run the measurement states held every call at both ends, paired, nothing lost; no thread named the other end's.
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("throughALabSession").GetProperty("runs").EnumerateArray()];
        Assert.Equal(4, runs.Length);
        Assert.All(runs, run =>
        {
            int truth = run.GetProperty("truth").GetProperty("issued").GetInt32();
            JsonElement invokes = run.GetProperty("invokes");
            Assert.Equal((truth, truth, truth, 0), (invokes.GetProperty("clientInvokesToTheServer").GetInt32(), invokes.GetProperty("serverInvokesFromTheClient").GetInt32(),
                invokes.GetProperty("pairsServedInsideTheClientsCall").GetInt32(), invokes.GetProperty("clientInvokesNamingAServingThreadOfTheServer").GetInt32()));
            Assert.Equal(0, run.GetProperty("session").GetProperty("eventsLost").GetInt32());
        });
        JsonElement delivery = measurement.RootElement.GetProperty("throughALabSession").GetProperty("delivery");
        Assert.Equal(delivery.GetProperty("realTimeDrainedBeforeDisposal").GetProperty("runs").GetInt32(),
            delivery.GetProperty("realTimeDrainedBeforeDisposal").GetProperty("complete").GetInt32());
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), runs[0].GetProperty("bench").GetString()!, "report.json")));
    }

    [Fact(DisplayName = "R21: a session's process filter holds for COM-Perf - FX-COM-001's filtered pair has its every call recorded at both ends, and a decoy pair making as many calls beside it, and every other process, none: outside the filter is not collected, never quiet")]
    public void TheProcessFilterHolds()
    {
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("processFilter").GetProperty("runs").EnumerateArray()];
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run =>
        {
            JsonElement truth = run.GetProperty("truth");
            int calls = truth.GetProperty("filtered").GetProperty("issued").GetInt32();
            Assert.Equal((calls, calls), (truth.GetProperty("filtered").GetProperty("served").GetInt32(), truth.GetProperty("decoy").GetProperty("issued").GetInt32()));

            // Only the filtered pair's records arrived: its every invoke at both ends, the decoy's none, nobody else's any.
            Assert.Equal(["client", "server"], run.GetProperty("recordsByProcess").EnumerateObject().Select(process => process.Name).Order(StringComparer.Ordinal));
            JsonElement kept = run.GetProperty("filteredInvokes");
            JsonElement decoy = run.GetProperty("decoyInvokes");
            Assert.Equal((calls, calls, 0, 0), (kept.GetProperty("atTheClientNamingTheServer").GetInt32(), kept.GetProperty("atTheServerNamingTheClient").GetInt32(),
                decoy.GetProperty("atTheClientNamingTheServer").GetInt32(), decoy.GetProperty("atTheServerNamingTheClient").GetInt32()));
            Assert.Equal((0, 0), (run.GetProperty("session").GetProperty("eventsLost").GetInt32(), run.GetProperty("session").GetProperty("realTimeBuffersLost").GetInt32()));
            Assert.True(File.Exists(Path.Combine(RepositoryRoot(), run.GetProperty("bench").GetString()!, "report.json")));
        });
    }

    [Fact(DisplayName = "P7: FX-COM-002's calls across an apartment are recorded as an out-of-process call is, each naming its own process at both ends, so a call is between processes only where the process it names is another")]
    public void AnInProcessCallNamesItsOwnProcess()
    {
        string evidence = Evidence("FX-COM-002");
        Row[] rows = Ledger(evidence);
        (string Kind, long Call, long Utc)[] client = Truth(evidence, "truth-client.jsonl");
        (string Kind, long Call, long Utc)[] issued = [.. client.Where(record => record.Kind == "CallIssued")];
        (string Kind, long Call, long Utc)[] completed = [.. client.Where(record => record.Kind == "CallCompleted")];

        // Every call start, at either end, is raised in the workload's process and names that same process as its other end.
        Row[] starts = [.. rows.Where(row => row.Event is 39 or 43)];
        Assert.NotEmpty(starts);
        Assert.All(starts, row => Assert.Equal(("the process", "the process"), (row.RaisedIn, row.OtherEnd)));

        // Each truth call is one invoke at the client end and one at the server end, both in that process, each start joined
        // to its stop by its trace number.
        Row[] made = [.. rows.Where(row => row is { Event: 39, Interface: Dispatch } && row.Method == Invoke.ToString(CultureInfo.InvariantCulture))];
        Row[] served = [.. rows.Where(row => row is { Event: 43, Interface: Dispatch } && row.Method == Invoke.ToString(CultureInfo.InvariantCulture))];
        Assert.Equal((64, 64, 64), (issued.Length, made.Length, served.Length));
        Assert.All(issued.Zip(completed), call => Assert.Single(made, row => row.Utc >= call.First.Utc && row.Utc <= call.Second.Utc));
        foreach ((int start, int stop) in new[] { (39, 40), (43, 44) })
        {
            HashSet<string> stopped = [.. rows.Where(row => row.Event == stop).Select(row => row.CallTrace)];
            Assert.All(rows.Where(row => row.Event == start), row => Assert.Contains(row.CallTrace, stopped));
        }

        // Both runs the measurement states, alike.
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        Assert.All(measurement.RootElement.GetProperty("throughALabSession").GetProperty("runs").EnumerateArray(), run =>
        {
            int calls = run.GetProperty("truth").GetProperty("issued").GetInt32();
            JsonElement invokes = run.GetProperty("invokes");
            Assert.Equal((calls, calls), (invokes.GetProperty("atTheClientNamingItsOwnProcess").GetInt32(), invokes.GetProperty("atTheServerNamingItsOwnProcess").GetInt32()));
            Assert.Equal(run.GetProperty("clientCalls").GetProperty("starts").GetInt32(), run.GetProperty("clientCalls").GetProperty("startsNamingTheirOwnProcess").GetInt32());
        });
    }

    [Fact(DisplayName = "P27: COM calls have an inventoried source, requested by no profile and admitting no event, its process filter and cost measured, and no tier on any build")]
    public void ComCallsHaveALeadAndNoTier()
    {
        WindowsSourceDefinition comPerf = WindowsSourceCatalog.Find(WindowsSourceCatalog.ComPerfSourceId)!;
        Assert.Equal(("Microsoft-Windows-COM-Perf", SourceKind.ManifestProvider), (comPerf.ProviderName, comPerf.Kind));
        Assert.Equal([Mechanism.ComCall], comPerf.Mechanisms);
        Assert.Equal((SourceContractStatus.Experimental, true), (comPerf.ContractStatus, comPerf.SupportsCaptureSideProcessFilter));
        Assert.Empty(comPerf.AdmittedEvents);

        // Its cost is measured, Low in processor time, with the throughput regression its notes state beside the class.
        Assert.Equal(OverheadClass.Low, comPerf.Overhead);
        using (JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), comPerf.OverheadEvidence!))))
        {
            Assert.Equal((comPerf.Overhead.ToString(), false), (impact.RootElement.GetProperty("overhead").GetString(),
                impact.RootElement.GetProperty("throughputTargetMet").GetBoolean()));
        }

        Assert.Contains(comPerf.Notes, note => note.Contains("slower", StringComparison.Ordinal) && note.Contains("never Explore", StringComparison.Ordinal));
        Assert.Contains(comPerf.Notes, note => note.Contains("FX-COM-001", StringComparison.Ordinal));
        Assert.Contains(comPerf.Notes, note => note.Contains("FX-COM-002", StringComparison.Ordinal));
        Assert.Contains("FX-COM-001", comPerf.FilteringNotes, StringComparison.Ordinal);

        // No profile requests it, and no other source claims COM calls.
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures),
            profile => profile.Sources.Any(source => source.SourceId == WindowsSourceCatalog.ComPerfSourceId));
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != WindowsSourceCatalog.ComPerfSourceId),
            source => Assert.DoesNotContain(Mechanism.ComCall, source.Mechanisms));

        // And no build carries a tier for COM calls: no fixture has measured them through the product.
        Assert.DoesNotContain(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.ComCall);
    }

    [Fact(DisplayName = "§12: COM-Perf's collection is measured in two series of pairs - Low in processor time, and a COM-heavy workload slower past §12's throughput target, so only an opt-in profile stating it may admit the source")]
    public void TheCollectionCostIsMeasured()
    {
        string[] series = [.. Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "com-perf-impact-*")
            .Select(directory => Path.Combine(directory, "impact.json")).Where(File.Exists).Order(StringComparer.Ordinal)];
        Assert.Equal(2, series.Length);
        Assert.All(series, file =>
        {
            using JsonDocument impact = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = impact.RootElement;

            // Nine pairs of FX-COM-001's exchange unpaced, nothing lost: processor time is Low, and the workload is slower
            // than §12's 5% target in its median pair.
            Assert.Equal((9, 0L), (root.GetProperty("pairs").GetArrayLength(), root.GetProperty("eventsLost").GetInt64()));
            Assert.Equal(nameof(OverheadClass.Low), root.GetProperty("overhead").GetString());
            Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(root.GetProperty("medianCaptureImpactPercentagePoints").GetDouble()));
            Assert.True(root.GetProperty("medianThroughputRegressionPercent").GetDouble() > 5, file);
            Assert.False(root.GetProperty("throughputTargetMet").GetBoolean());
            Assert.Contains("com-loopback", root.GetProperty("workload").GetString(), StringComparison.Ordinal);
        });
    }

    private static Row[] Ledger(string? evidence = null) =>
    [
        .. File.ReadLines(Path.Combine(evidence ?? Evidence(), "ledger.csv")).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(','))
            .Select(field => new Row(long.Parse(field[0], CultureInfo.InvariantCulture), field[1], int.Parse(field[2], CultureInfo.InvariantCulture),
                field[3], field[4], field[5], field[6], field[7], field[8])),
    ];

    private static (string Kind, long Call, long Utc)[] Truth(string file) => Truth(Evidence(), file);

    private static (string Kind, long Call, long Utc)[] Truth(string evidence, string file) =>
    [
        .. File.ReadLines(Path.Combine(evidence, file)).Where(line => line.Length > 0).Select(line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            JsonElement root = record.RootElement;
            return (root.GetProperty("kind").GetString()!,
                root.GetProperty("callId").ValueKind == JsonValueKind.Number ? root.GetProperty("callId").GetInt64() : -1,
                DateTimeOffset.Parse(root.GetProperty("recordedUtc").GetString()!, CultureInfo.InvariantCulture).UtcTicks);
        }),
    ];

    private static string Evidence(string fixture = "FX-COM-001") => Path.Combine(RepositoryRoot(), "fixtures", fixture, "evidence");

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
