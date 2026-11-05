using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SyncProbe;

/// <summary>
/// Reads a session icat record recorded with the synchronization objects profile and writes the ledger of the workload's
/// synchronization handle records as the session holds them: each record's place in the session's order, its time on the wall
/// clock the capture's calibration reads, its process's role under `process-binding-v6`, its kind and opcode, its object's
/// number in the capture, and its name where it names one of the run's objects. A record of a process the truth does not name
/// is kept where its object is one the workload's processes held, under a role of its own - the first such process other-01,
/// the next other-02 - so a stranger bound to a truth object is in the ledger. It writes no PID, no handle value, no address
/// and no name but the run's own objects' (P16), so a test can read the ledger as the session's records were. It then
/// evaluates the ledger against the truth as the product evaluates named synchronization objects, and writes the result
/// beside it.
/// </summary>
internal static class SessionLedger
{
    public const string Header = "ordinal,utcTicks,role,kind,opcode,object,name";

    /// <summary>What the ledger writes for a name that names none of the run's objects, a name it never writes.</summary>
    public const string OtherName = "named";

    /// <summary>The role prefix of a process the truth does not name, numbered in the order its first kept record came.</summary>
    public const string OtherRole = "other-";

    private static readonly string[] TruthFiles = ["truth-creator.jsonl", "truth-opener.jsonl"];

    public static int Write(string session, string? truth, string? output)
    {
        if (truth is null || output is null)
        {
            Console.Error.WriteLine("InterCat.SyncProbe --session <recorded session> --truth <its workload's truth> --out <records.csv>");
            return 2;
        }

        // The evaluation's words are the invariant culture's, as a fixture's committed measurement states them.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Dictionary<int, string> roles;
        string fixture;
        string[] objects;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            JsonElement root = scenario.RootElement;
            fixture = root.GetProperty("scenarioId").GetString()!;
            roles = new()
            {
                [root.GetProperty("creatorProcessId").GetInt32()] = "creator",
                [root.GetProperty("openerProcessId").GetInt32()] = "opener",
            };
            objects = [.. root.GetProperty("objects").EnumerateArray().Select(name => name.GetString()!)];
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

            // Each record's object number is a source field of its own record, the gate's number over the address.
            var numbers = new Dictionary<(uint Stream, uint Epoch, ulong Ordinal, FactKey Fact), long>();
            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                SegmentReaderV1 fields = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < fields.RowCount; index++)
                {
                    SourceFieldRowV1 field = fields.FieldRow(index);
                    if (field is { Field: SourceField.SynchronizationObjectNumber, Value: { } value })
                    {
                        numbers[(field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal, field.FactKey)] = value;
                    }
                }
            }

            foreach (string name in SessionSegments.Names(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    ObservationRowV1 row = segment.Row(index);
                    if (row.Mechanism != Mechanism.Synchronization)
                    {
                        continue;
                    }

                    // The process the record belongs to as the product binds it: its payload's owner, or the process that
                    // raised it where its kind binds by its header.
                    int? owner = RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId);
                    long? number = numbers.TryGetValue((row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey), out long found)
                        ? found : null;
                    boundToNone += owner is null ? 1 : 0;
                    withoutNumber += number is null ? 1 : 0;
                    long utc = first.Utc.UtcTicks + (long)((Int128)(row.NativeTicks - first.NativeTicks)
                        * (last.Utc.UtcTicks - first.Utc.UtcTicks) / (last.NativeTicks - first.NativeTicks));
                    records.Add(new(row.NativeTicks, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey, utc, owner,
                        row.Kind, row.Opcode, number, row.ResourceName));
                }
            }
        }

        store.ReleaseSegmentReaders();
        records.Sort(static (left, right) => left.CompareTo(right));

        // The objects the workload's processes held, by number: the records of any other process are kept on these alone.
        HashSet<long> held = [.. records.Where(record => record.Owner is { } owner && roles.ContainsKey(owner) && record.Number is not null)
            .Select(static record => record.Number!.Value)];
        var others = new Dictionary<int, string>();
        var text = new StringBuilder(Header).Append('\n');
        int written = 0;
        int ofTheWorkload = 0;
        int ofOthersKept = 0;
        int unboundKept = 0;
        long ofOthers = 0;
        int named = 0;
        foreach (Row record in records)
        {
            bool workload = record.Owner is { } process && roles.ContainsKey(process);
            ofOthers += !workload && record.Owner is not null ? 1 : 0;
            if (!workload && (record.Number is not { } number || !held.Contains(number)))
            {
                continue;
            }

            string role = record.Owner is not { } id ? string.Empty
                : roles.TryGetValue(id, out string? known) ? known
                : others.TryGetValue(id, out string? other) ? other
                : (others[id] = OtherRole + (others.Count + 1).ToString("D2", CultureInfo.InvariantCulture));

            // A name is written only where it names one of the run's objects, by the evaluator's own reading of a name.
            string? kernelName = record.Name;
            bool ofTheRun = kernelName is not null && objects.Any(name => SyncObjectCoverageEvaluator.Names(kernelName, name));
            named += ofTheRun ? 1 : 0;
            text.Append(string.Join(',',
                written.ToString(CultureInfo.InvariantCulture),
                record.Utc.ToString(CultureInfo.InvariantCulture),
                role,
                record.Kind.ToString(),
                record.Opcode.ToString(CultureInfo.InvariantCulture),
                record.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                kernelName is null ? string.Empty : ofTheRun ? kernelName : OtherName)).Append('\n');
            written++;
            ofTheWorkload += workload ? 1 : 0;
            ofOthersKept += !workload && record.Owner is not null ? 1 : 0;
            unboundKept += record.Owner is null ? 1 : 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ofTheWorkload} synchronization records of the workload's two processes, {ofOthersKept} of {others.Count} other processes and {unboundKept} bound to none on their objects written to {output}; {ofOthers - ofOthersKept} of other processes and {boundToNone - unboundKept} bound to none left out."));

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
        SyncObjectCoverageResult result = SyncObjectCoverageEvaluator.Evaluate(truthRecords, Read(output, processes), new()
        {
            FixtureId = fixture,
            BuildId = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            BuildIsSupported = true,
            Reproduced = true,
        });
        File.WriteAllText(Path.ChangeExtension(output, ".evaluation.json"), JsonSerializer.Serialize(new
        {
            result.Measurement,
            result.Assessment.Tier,
            result.Assessment.Gaps,
            result.Objects,
            result.ObservationsOutsideScope,
            result.Notes,
            SessionSynchronizationRecords = records.Count,
            WorkloadRecords = ofTheWorkload,
            WorkloadRecordsNamingARunObject = named,
            OtherProcessesRecords = ofOthers,
            OtherProcessesRecordsOnTheWorkloadsObjects = ofOthersKept,
            OtherProcessesOnTheWorkloadsObjects = others.Count,
            RecordsBoundToNoProcess = boundToNone,
            RecordsBoundToNoProcessOnTheWorkloadsObjects = unboundKept,
            RecordsWithNoObjectNumber = withoutNumber,
            FirstSynchronizationRecordUtcTicks = records.Count == 0 ? (long?)null : records.Min(record => record.Utc),
            CalibratedFromUtcTicks = calibratedFrom,
            CalibratedToUtcTicks = calibratedTo,
        }, json));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{result.Measurement.DiscoveredResourcesWithLifetime} of {result.Measurement.TruthResources} truth objects discovered with their lifetimes, "
            + $"{result.Measurement.ResolvedMemberships} of {result.Measurement.ExpectedMemberships} memberships resolved "
            + $"({result.Measurement.FalsePeerAttributions} false), {result.Measurement.TruthOperationsWithAdmittedObservation} of "
            + $"{result.Measurement.TruthOperations} handle opens and closes seen; tier {result.Assessment.Tier}."));
        return 0;
    }

    /// <summary>
    /// A ledger's records, as the evaluator reads a session's: each of the truth's process of its role, each other process a
    /// process of its own the truth never names, and a name that names none of the run's objects one that names no object.
    /// </summary>
    public static IReadOnlyList<SyncObjectObservation> Read(string ledger, IReadOnlyDictionary<string, int> processes)
    {
        int? Process(string role) => role.Length == 0 ? null
            : processes.TryGetValue(role, out int id) ? id
            : -int.Parse(role[OtherRole.Length..], CultureInfo.InvariantCulture);
        return
        [
            .. File.ReadLines(ledger).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).Select(field => new SyncObjectObservation
            {
                Ordinal = long.Parse(field[0], CultureInfo.InvariantCulture),
                Kind = Enum.Parse<ObservationKind>(field[3]),
                ProcessId = Process(field[2]),
                ObjectNumber = field[5].Length == 0 ? null : long.Parse(field[5], CultureInfo.InvariantCulture),
                Name = field[6].Length == 0 ? null : field[6],
            }),
        ];
    }

    /// <summary>One synchronization record of the session, in canonical order by its reading and then its place.</summary>
    private readonly record struct Row(
        long NativeTicks,
        uint Stream,
        uint Epoch,
        ulong Ordinal,
        FactKey Fact,
        long Utc,
        int? Owner,
        ObservationKind Kind,
        byte Opcode,
        long? Number,
        string? Name) : IComparable<Row>
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
