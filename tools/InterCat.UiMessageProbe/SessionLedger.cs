using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.UiMessageProbe;

/// <summary>
/// Reads a session icat record recorded with the window messages profile and writes the ledger of the workload's window
/// message records as the session holds them: each record's place in the session's order, its time on the wall clock the
/// capture's calibration reads, its process's role under `process-binding-v7`, its kind and direction, its message's number
/// in the capture, and the role of the process a send's start names at its other end. A record of a process the truth does
/// not name is kept where its message is one the workload's processes raised an end of, under a role of its own - the first
/// such process other-01, the next other-02 - so a stranger at a message's other end is in the ledger. It writes no PID, no
/// window, no pointer and no message (P16), so a test can read the ledger as the session's records were. It then evaluates
/// the ledger against the truth as the product evaluates window messages (ADR-088), and writes the result beside it.
/// </summary>
internal static class SessionLedger
{
    public const string Header = "ordinal,utcTicks,role,kind,direction,number,peer";

    /// <summary>The role prefix of a process the truth does not name, numbered in the order its first kept record came.</summary>
    public const string OtherRole = "other-";

    private static readonly string[] TruthFiles = ["truth-sender.jsonl", "truth-receiver.jsonl"];

    /// <summary>
    /// Writes the ledger and its evaluation, and returns the evaluation with the capture's window: its first window message
    /// record to its last, on the wall clock its calibration reads.
    /// </summary>
    public static (WindowMessageCoverageResult Result, long CaptureFromUtcTicks, long CaptureToUtcTicks) Write(string session, string truth, string output)
    {
        Dictionary<int, string> roles;
        string fixture;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            JsonElement root = scenario.RootElement;
            fixture = root.GetProperty("scenarioId").GetString()!;
            roles = new()
            {
                [root.GetProperty("senderProcessId").GetInt32()] = "sender",
                [root.GetProperty("receiverProcessId").GetInt32()] = "receiver",
            };
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        var records = new List<Row>();
        long calibratedFrom;
        long calibratedTo;
        long boundToNone = 0;
        long withoutNumber = 0;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            ClockCalibrationV1 calibration = ClockCalibrationV1.Read(store.Root, manifest)
                ?? throw new InvalidDataException("The session carries no clock calibration, so its records cannot be read on the wall clock.");
            ClockCalibrationSampleV1 first = calibration.Samples[0];
            ClockCalibrationSampleV1 last = calibration.Samples[^1];
            if (last.NativeTicks <= first.NativeTicks)
            {
                throw new InvalidDataException("The session's calibration holds no two readings apart to read its records between.");
            }

            (calibratedFrom, calibratedTo) = (first.Utc.UtcTicks, last.Utc.UtcTicks);

            // Each record's message number and the process a send names are source fields of its own record.
            var numbers = new Dictionary<(uint Stream, uint Epoch, ulong Ordinal, FactKey Fact), long>();
            var peers = new Dictionary<(uint Stream, uint Epoch, ulong Ordinal, FactKey Fact), int>();
            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                SegmentReaderV1 fields = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < fields.RowCount; index++)
                {
                    SourceFieldRowV1 field = fields.FieldRow(index);
                    var key = (field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal, field.FactKey);
                    if (field is { Field: SourceField.WindowMessageNumber, Value: { } number })
                    {
                        numbers[key] = number;
                    }
                    else if (field is { Field: SourceField.PeerProcessId, Value: { } peer })
                    {
                        peers[key] = (int)peer;
                    }
                }
            }

            foreach (string name in SessionSegments.Names(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    ObservationRowV1 row = segment.Row(index);
                    if (row.Mechanism != Mechanism.WindowMessage)
                    {
                        continue;
                    }

                    // The process the record belongs to as the product binds it: the process that raised it.
                    int? owner = RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId);
                    var key = (row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey);
                    long? number = numbers.TryGetValue(key, out long found) ? found : null;
                    int? peer = peers.TryGetValue(key, out int named) ? named : null;
                    boundToNone += owner is null ? 1 : 0;
                    withoutNumber += number is null ? 1 : 0;
                    long utc = first.Utc.UtcTicks + (long)((Int128)(row.NativeTicks - first.NativeTicks)
                        * (last.Utc.UtcTicks - first.Utc.UtcTicks) / (last.NativeTicks - first.NativeTicks));
                    records.Add(new(row.NativeTicks, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey, utc, owner,
                        row.Kind, row.Direction, number, peer));
                }
            }
        }

        store.ReleaseSegmentReaders();
        records.Sort(static (left, right) => left.CompareTo(right));

        // The capture's window: its first window message record to its last, whichever process raised them - every window on
        // the machine pumps messages, so the capture's records run from its start to its stop. A truth message begun outside
        // it is no message of the capture's (ADR-088's third addendum).
        if (records.Count == 0)
        {
            throw new InvalidDataException("The session holds no window message record, so it has no window to hold a truth to.");
        }

        (long captureFrom, long captureTo) = (records[0].Utc, records[^1].Utc);

        // The messages the workload's processes raised an end of, by number: the records of any other process are kept on
        // these alone.
        HashSet<long> theirs = [.. records.Where(record => record.Owner is { } owner && roles.ContainsKey(owner) && record.Number is not null)
            .Select(static record => record.Number!.Value)];
        var others = new Dictionary<int, string>();
        string Role(int? process) => process is not { } id ? string.Empty
            : roles.TryGetValue(id, out string? known) ? known
            : others.TryGetValue(id, out string? other) ? other
            : (others[id] = OtherRole + (others.Count + 1).ToString("D2", CultureInfo.InvariantCulture));
        var text = new StringBuilder(Header).Append('\n');
        int written = 0;
        int ofTheWorkload = 0;
        int ofOthersKept = 0;
        int unboundKept = 0;
        long ofOthers = 0;
        foreach (Row record in records)
        {
            bool workload = record.Owner is { } process && roles.ContainsKey(process);
            ofOthers += !workload && record.Owner is not null ? 1 : 0;
            if (!workload && (record.Number is not { } number || !theirs.Contains(number)))
            {
                continue;
            }

            text.Append(string.Join(',',
                written.ToString(CultureInfo.InvariantCulture),
                record.Utc.ToString(CultureInfo.InvariantCulture),
                Role(record.Owner),
                record.Kind.ToString(),
                record.Direction.ToString(),
                record.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Role(record.Peer))).Append('\n');
            written++;
            ofTheWorkload += workload ? 1 : 0;
            ofOthersKept += !workload && record.Owner is not null ? 1 : 0;
            unboundKept += record.Owner is null ? 1 : 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text.ToString());

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        json.Converters.Add(new JsonStringEnumConverter());
        TruthRecord[] truthRecords =
        [
            .. TruthFiles
                .SelectMany(file => File.ReadAllLines(Path.Combine(truth, file)))
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<TruthRecord>(line, json)!),
        ];
        Dictionary<string, int> processes = truthRecords.GroupBy(record => record.Role).ToDictionary(group => group.Key, group => group.First().ProcessId);
        WindowMessageCoverageResult result = WindowMessageCoverageEvaluator.Evaluate(truthRecords, Read(output, processes), new()
        {
            FixtureId = fixture,
            BuildId = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            BuildIsSupported = true,
            Reproduced = true,
            CaptureFromUtcTicks = captureFrom,
            CaptureToUtcTicks = captureTo,
        });
        File.WriteAllText(Path.ChangeExtension(output, ".evaluation.json"), JsonSerializer.Serialize(new
        {
            result.Measurement,
            result.Assessment.Tier,
            result.Assessment.Gaps,
            result.Messages,
            result.MessagesWithEveryEnd,
            result.MessagesBeyondTruth,
            result.ObservationsOutsideScope,
            result.MessagesOutsideCapture,
            result.MessagesBegunBeforeCapture,
            result.Notes,
            CaptureFromUtcTicks = captureFrom,
            CaptureToUtcTicks = captureTo,
            SessionWindowMessageRecords = records.Count,
            WorkloadRecords = ofTheWorkload,
            OtherProcessesRecords = ofOthers,
            OtherProcessesRecordsOnTheWorkloadsMessages = ofOthersKept,
            OtherProcessesOnTheWorkloadsMessages = others.Count,
            RecordsBoundToNoProcess = boundToNone,
            RecordsBoundToNoProcessOnTheWorkloadsMessages = unboundKept,
            RecordsWithNoMessageNumber = withoutNumber,
            CalibratedFromUtcTicks = calibratedFrom,
            CalibratedToUtcTicks = calibratedTo,
        }, json));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ofTheWorkload} window message records of the workload's two processes and {ofOthersKept} of {others.Count} other processes on its "
            + $"messages written to {output}; {result.Measurement.TruthOperationsWithAdmittedObservation} of {result.Measurement.TruthOperations} "
            + $"truth messages observed, {result.MessagesWithEveryEnd} with every end ({result.Measurement.FalsePeerAttributions} false peers); "
            + $"{result.MessagesOutsideCapture} begun outside the capture and {result.MessagesBegunBeforeCapture} of its messages begun before it; "
            + $"tier {result.Assessment.Tier}."));
        return (result, captureFrom, captureTo);
    }

    /// <summary>
    /// A ledger's records, as the evaluator reads a session's: each of the truth's processes by its role, and each other
    /// process a process of its own the truth never names.
    /// </summary>
    public static IReadOnlyList<WindowMessageObservation> Read(string ledger, IReadOnlyDictionary<string, int> processes)
    {
        int? Process(string role) => role.Length == 0 ? null
            : processes.TryGetValue(role, out int id) ? id
            : -int.Parse(role[OtherRole.Length..], CultureInfo.InvariantCulture);
        return
        [
            .. File.ReadLines(ledger).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new WindowMessageObservation
            {
                Ordinal = long.Parse(field[0], CultureInfo.InvariantCulture),
                Kind = Enum.Parse<ObservationKind>(field[3]),
                Direction = Enum.Parse<Direction>(field[4]),
                ProcessId = Process(field[2]),
                MessageNumber = field[5].Length == 0 ? null : long.Parse(field[5], CultureInfo.InvariantCulture),
                PeerProcessId = Process(field[6]),
            }),
        ];
    }

    /// <summary>One window message record of the session, in canonical order by its reading and then its place.</summary>
    private readonly record struct Row(
        long NativeTicks,
        uint Stream,
        uint Epoch,
        ulong Ordinal,
        FactKey Fact,
        long Utc,
        int? Owner,
        ObservationKind Kind,
        Direction Direction,
        long? Number,
        int? Peer) : IComparable<Row>
    {
        public int CompareTo(Row other) =>
            NativeTicks != other.NativeTicks ? NativeTicks.CompareTo(other.NativeTicks)
            : Stream != other.Stream ? Stream.CompareTo(other.Stream)
            : Epoch != other.Epoch ? Epoch.CompareTo(other.Epoch)
            : Ordinal != other.Ordinal ? Ordinal.CompareTo(other.Ordinal)
            : Fact.High != other.Fact.High ? Fact.High.CompareTo(other.Fact.High)
            : Fact.Low.CompareTo(other.Fact.Low);
    }
}
