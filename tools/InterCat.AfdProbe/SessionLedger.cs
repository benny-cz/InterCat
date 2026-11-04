using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.AfdProbe;

/// <summary>
/// Reads a session icat record recorded with the Unix sockets profile and writes the ledger of the workload's own
/// Unix-domain socket rows: each row's ordinal, its time on the wall clock the capture's calibration reads, its owner's
/// role, its kind, its socket's number in the capture, its bytes and its status. It writes no PID, no path, no address and
/// no other process's row (P16), so a test can read the ledger as the session's rows were. It then evaluates the ledger
/// against the truth as the product evaluates AF_UNIX coverage, and writes the result beside it.
/// </summary>
internal static class SessionLedger
{
    public const string Header = "ordinal,utcTicks,role,kind,socket,bytes,status";

    private static readonly string[] TruthFiles = ["truth-server.jsonl", "truth-client.jsonl"];

    public static int Write(string session, string? truth, string? output)
    {
        if (truth is null || output is null)
        {
            Console.Error.WriteLine("InterCat.AfdProbe --session <recorded session> --truth <its workload's truth> --out <ledger.csv>");
            return 2;
        }

        // The evaluation's words are the invariant culture's, as a fixture's committed measurement states them.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Dictionary<int, string> roles;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            roles = new()
            {
                [scenario.RootElement.GetProperty("serverProcessId").GetInt32()] = "server",
                [scenario.RootElement.GetProperty("clientProcessId").GetInt32()] = "client",
            };
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        var lines = new List<(long Utc, ulong Ordinal, string Line)>();
        int others = 0;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            ClockCalibrationV1 calibration = ClockCalibrationV1.Read(store.Root, manifest)
                ?? throw new InvalidDataException("The session carries no clock calibration, so its rows cannot be read on the wall clock.");
            ClockCalibrationSampleV1 first = calibration.Samples[0];
            ClockCalibrationSampleV1 last = calibration.Samples[^1];
            if (last.NativeTicks <= first.NativeTicks)
            {
                throw new InvalidDataException("The session's calibration holds no two readings apart to read its rows between.");
            }

            // Each row's socket is its connection id, a source field of its own record.
            var sockets = new Dictionary<(uint, uint, ulong), long>();
            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                SegmentReaderV1 fields = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < fields.RowCount; index++)
                {
                    SourceFieldRowV1 field = fields.FieldRow(index);
                    if (field is { Field: SourceField.ConnectionId, Value: { } value })
                    {
                        sockets[(field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal)] = value;
                    }
                }
            }

            foreach (string name in SessionSegments.Names(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    ObservationRowV1 row = segment.Row(index);
                    if (row.Mechanism != Mechanism.UnixDomainSocket)
                    {
                        continue;
                    }

                    if (row.OwnerProcessId is not { } owner || !roles.TryGetValue(owner, out string? role))
                    {
                        others++;
                        continue;
                    }

                    long utc = first.Utc.UtcTicks + (long)((Int128)(row.NativeTicks - first.NativeTicks)
                        * (last.Utc.UtcTicks - first.Utc.UtcTicks) / (last.NativeTicks - first.NativeTicks));
                    long socket = sockets.GetValueOrDefault((row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal));
                    lines.Add((utc, row.RawRecordOrdinal, string.Join(',',
                        row.RawRecordOrdinal.ToString(CultureInfo.InvariantCulture),
                        utc.ToString(CultureInfo.InvariantCulture),
                        role,
                        row.Kind.ToString(),
                        socket.ToString(CultureInfo.InvariantCulture),
                        row.ByteValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        row.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)));
                }
            }
        }

        var text = new StringBuilder(Header).Append('\n');
        foreach ((long _, ulong _, string line) in lines.OrderBy(static line => line.Utc).ThenBy(static line => line.Ordinal))
        {
            text.Append(line).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{lines.Count} Unix-domain socket rows of the workload's two processes written to {output}; {others} of other processes left out."));

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        json.Converters.Add(new JsonStringEnumConverter());
        TruthRecord[] records =
        [
            .. TruthFiles
                .SelectMany(file => File.ReadAllLines(Path.Combine(truth, file)))
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<TruthRecord>(line, json)!),
        ];
        Dictionary<string, int> processes = records.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        UnixSocketCoverageResult result = UnixSocketCoverageEvaluator.Evaluate(records, Read(output, processes), new()
        {
            FixtureId = "FX-UNIX-001",
            BuildId = "10.0.26220.0-x64",
            BuildIsSupported = true,
            Reproduced = true,
        });
        File.WriteAllText(Path.ChangeExtension(output, ".evaluation.json"), JsonSerializer.Serialize(new
        {
            result.Measurement,
            result.Assessment.Tier,
            result.Assessment.Gaps,
            result.Processes,
            result.TruthBytesSent,
            result.TruthBytesReceived,
            result.ObservedBytesSent,
            result.ObservedBytesReceived,
            result.TransfersBeyondTruth,
            result.ObservationsOutsideScope,
            Unmatched = result.Operations.Count(operation => operation.Gap is not null),
        }, json));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{result.Measurement.TruthOperationsWithAdmittedObservation} of {result.Measurement.TruthOperations} truth operations observed, "
            + $"{result.Measurement.ByteMeasuredOperations} with bytes; sent {result.ObservedBytesSent} of {result.TruthBytesSent}, "
            + $"received {result.ObservedBytesReceived} of {result.TruthBytesReceived}; tier {result.Assessment.Tier}."));
        return 0;
    }

    /// <summary>A ledger's rows, as the evaluator reads a session's: each owned by the truth's process of its role.</summary>
    public static IReadOnlyList<UnixSocketObservation> Read(string ledger, IReadOnlyDictionary<string, int> processes) =>
    [
        .. File.ReadLines(ledger).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new UnixSocketObservation
        {
            RecordOrdinal = long.Parse(field[0], CultureInfo.InvariantCulture),
            TimestampUtcTicks = long.Parse(field[1], CultureInfo.InvariantCulture),
            OwnerProcessId = processes[field[2]],
            Kind = Enum.Parse<ObservationKind>(field[3]),
            Socket = long.Parse(field[4], CultureInfo.InvariantCulture),
            Bytes = field[5].Length == 0 ? null : long.Parse(field[5], CultureInfo.InvariantCulture),
            Status = field[6].Length == 0 ? null : long.Parse(field[6], CultureInfo.InvariantCulture),
        }),
    ];
}
