using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.ComProbe;

/// <summary>
/// Reads a session icat record recorded with the COM calls profile, derives its COM calls as the product does - its process
/// instances, its RPC calls, their other ends under `rpc-call-peer-v2` and its COM calls under `com-call-link-v1` - and writes
/// the ledger of the workload's own: each call's ordinal, end, its process's role and the role its start names at its other
/// end, its state, interface, method, start and stop on the wall clock the capture's calibration reads, HRESULT, the RPC call
/// carrying it and the call at its other end, and what that RPC call found at its own. It writes no PID, no thread, no activity
/// id and no other process's call (P16), so a test can read the ledger as the session's calls were. It then evaluates the ledger
/// against the truth as the product evaluates COM call coverage, and writes the result beside it.
/// </summary>
internal static class SessionLedger
{
    public const string Header =
        "ordinal,side,role,state,peerRole,interface,method,startUtcTicks,stopUtcTicks,hresult,carrier,link,otherEnd,otherEndRole,carrierPeer,carrierThroughCom,carrierServedBy";

    /// <summary>IDispatch, the interface FX-COM-001's client calls its object through, and Invoke, its method.</summary>
    public static readonly Guid Dispatch = new("00020400-0000-0000-c000-000000000046");

    public const long Invoke = 6;

    /// <summary>The role a ledger names a process the truth does not, read back as no process the truth names.</summary>
    private const string Other = "other";

    public static int Write(string session, string? truth, string? output)
    {
        if (truth is null || output is null)
        {
            Console.Error.WriteLine("InterCat.ComProbe --session <recorded session> --truth <its workload's truth> --out <calls.csv>");
            return 2;
        }

        // The evaluation's words are the invariant culture's, as a fixture's committed measurement states them.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Dictionary<int, string> roles;
        string fixture;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            // FX-COM-001's one server, or FX-COM-003's servers by their place, each a role of its own as its truth log names it.
            JsonElement root = scenario.RootElement;
            fixture = root.GetProperty("scenarioId").GetString()!;
            roles = new() { [root.GetProperty("clientProcessId").GetInt32()] = "client" };
            if (root.TryGetProperty("servers", out JsonElement servers))
            {
                foreach (JsonElement server in servers.EnumerateArray())
                {
                    roles[server.GetProperty("processId").GetInt32()] = string.Create(CultureInfo.InvariantCulture,
                        $"server-{server.GetProperty("index").GetInt32():D2}");
                }
            }
            else
            {
                roles[root.GetProperty("serverProcessId").GetInt32()] = "server";
            }

            // FX-COM-004's second server, which the server calls while serving.
            if (root.TryGetProperty("forwardedProcessId", out JsonElement forwarded))
            {
                roles[forwarded.GetInt32()] = "forwarded";
            }
        }

        string Role(int? process) => process is not { } id ? string.Empty : roles.GetValueOrDefault(id, Other);

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        var text = new StringBuilder(Header).Append('\n');
        int written = 0;
        int others = 0;
        long calibratedFrom;
        long calibratedTo;
        long rpcCalls;
        long otherComRecords;
        int comCalls;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            ClockCalibrationV1 calibration = ClockCalibrationV1.Read(store.Root, manifest)
                ?? throw new InvalidDataException("The session carries no clock calibration, so its calls cannot be read on the wall clock.");
            ClockCalibrationSampleV1 first = calibration.Samples[0];
            ClockCalibrationSampleV1 last = calibration.Samples[^1];
            if (last.NativeTicks <= first.NativeTicks)
            {
                throw new InvalidDataException("The session's calibration holds no two readings apart to read its calls between.");
            }

            (calibratedFrom, calibratedTo) = (first.Utc.UtcTicks, last.Utc.UtcTicks);
            long? Utc(long? native) => native is not { } ticks ? null : first.Utc.UtcTicks + (long)((Int128)(ticks - first.NativeTicks)
                * (last.Utc.UtcTicks - first.Utc.UtcTicks) / (last.NativeTicks - first.NativeTicks));

            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidDataException("The generation names no source clock, so no call has a process.");
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields);
            RpcCallIndex index = RpcCallIndex.Derive(segments, fields, processes, clock);
            RpcPeerIndex peers = RpcPeerIndex.Derive(index, segments, fields);
            ComCallIndex calls = ComCallIndex.Derive(index, segments, fields);
            rpcCalls = index.Totals.Calls;
            otherComRecords = calls.OtherComRecords;
            comCalls = calls.Calls.Count;

            // Where each client RPC call of the workload's processes is in its group, to read the other end of the one
            // carrying a client COM call.
            var placed = new Dictionary<ObservationId, (RpcCallGroup Group, int Position)>();
            foreach (RpcCallGroup group in index.Groups.Where(group => group.Side == RpcCallSide.Client && roles.ContainsKey(group.ProcessId)))
            {
                IReadOnlyList<RpcCall> described = index.CallsOf(group, segments, 0, (int)group.Counts.Calls);
                for (int position = 0; position < described.Count; position++)
                {
                    placed[described[position].Identity] = (group, position);
                }
            }

            for (int ordinal = 0; ordinal < calls.Calls.Count; ordinal++)
            {
                ComCall call = calls.Calls[ordinal];
                if (!roles.TryGetValue(call.ProcessId, out string? role))
                {
                    others++;
                    continue;
                }

                RpcPeerState? carrierPeer = null;
                bool throughCom = false;
                int? servedBy = null;
                if (call.Side == ComCallSide.Client && calls.CarrierOf(ordinal, segments) is { } carrier
                    && placed.TryGetValue(carrier.Identity, out (RpcCallGroup Group, int Position) at))
                {
                    (RpcPeerState state, RpcCall? other) = peers.PeerOf(at.Group, at.Position, segments);
                    carrierPeer = state == default ? null : state;
                    throughCom = peers.LinkedThroughCom(at.Group, at.Position);
                    servedBy = other?.ProcessId;
                }

                int? otherEnd = call.OtherEnd < 0 ? null : calls.Calls[call.OtherEnd].ProcessId;
                text.Append(string.Join(',',
                    ordinal.ToString(CultureInfo.InvariantCulture),
                    call.Side.ToString(),
                    role,
                    call.State.ToString(),
                    Role(call.PeerProcessId),
                    call.Interface?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
                    call.Method?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Utc(call.Start)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Utc(call.Stop)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    call.Hresult?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    call.Carrier.ToString(),
                    call.Link.ToString(),
                    call.OtherEnd < 0 ? string.Empty : call.OtherEnd.ToString(CultureInfo.InvariantCulture),
                    Role(otherEnd),
                    carrierPeer?.ToString() ?? string.Empty,
                    throughCom ? "true" : "false",
                    Role(servedBy))).Append('\n');
                written++;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{written} COM calls of the workload's {roles.Count} processes written to {output}; {others} of other processes left out."));

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        json.Converters.Add(new JsonStringEnumConverter());
        TruthRecord[] records =
        [
            .. Directory.EnumerateFiles(truth, "truth-*.jsonl").Order(StringComparer.Ordinal)
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<TruthRecord>(line, json)!),
        ];
        Dictionary<string, int> processIds = records.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        ComCallCoverageResult result = ComCallCoverageEvaluator.Evaluate(records, Read(output, processIds), new()
        {
            ExpectedInterface = Dispatch,
            ExpectedMethod = Invoke,
            FixtureId = fixture,
            BuildId = "10.0.26220.0-x64",
            BuildIsSupported = true,
            Reproduced = true,
        });
        File.WriteAllText(Path.ChangeExtension(output, ".evaluation.json"), JsonSerializer.Serialize(new
        {
            result.Measurement,
            result.Assessment.Tier,
            result.Assessment.Gaps,
            result.TruthCallsServed,
            result.ServedCallsObserved,
            result.CallsCompleted,
            result.HresultsAgreeing,
            result.CallsCarried,
            result.LinkedToTheirServedCall,
            result.CarriersServedThroughCom,
            result.CallsBeyondTruth,
            result.ObservationsOutsideScope,
            result.CallsPairedByRank,
            Unmatched = result.Calls.Count(call => call.Gap is not null),
            ClockOffsetMilliseconds = result.ClockOffset.TotalMilliseconds,
            OtherProcessesCalls = others,
            SessionComCalls = comCalls,
            SessionOtherComRecords = otherComRecords,
            SessionRpcCalls = rpcCalls,
            CalibratedFromUtcTicks = calibratedFrom,
            CalibratedToUtcTicks = calibratedTo,
            result.Notes,
        }, json));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{result.Measurement.TruthOperationsWithAdmittedObservation} of {result.Measurement.TruthOperations} truth calls observed, "
            + $"{result.Measurement.PeerAttributions} linked ({result.Measurement.FalsePeerAttributions} falsely), "
            + $"{result.ServedCallsObserved} of {result.TruthCallsServed} served calls observed, {result.CarriersServedThroughCom} carriers "
            + $"served through COM; tier {result.Assessment.Tier}."));
        return 0;
    }

    /// <summary>A ledger's calls, as the evaluator reads a session's: each of the truth's process of its role.</summary>
    public static IReadOnlyList<ComCallObservation> Read(string ledger, IReadOnlyDictionary<string, int> processes)
    {
        int? Process(string role) => role.Length == 0 ? null : processes.TryGetValue(role, out int id) ? id : -1;
        long? Number(string field) => field.Length == 0 ? null : long.Parse(field, CultureInfo.InvariantCulture);
        return
        [
            .. File.ReadLines(ledger).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new ComCallObservation
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
            }),
        ];
    }
}
