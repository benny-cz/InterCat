using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SectionProbe;

/// <summary>
/// Reads a session icat record recorded with the sections profile and writes the ledger of its section records as the session
/// holds them: each record's place in the session's order, its time on the wall clock the capture's calibration reads, its
/// process's role under `process-binding-v8`, its kind and opcode, a handle's section number, a view's number, its section's
/// number, its size and its extra information's high byte, and a name where it names one of the run's sections. It keeps every
/// record of the workload's two processes - their runtimes' own views and images among them - and of any other process only
/// the records on a number one of the run's sections was found under, under a role of its own, other-01 and on. It writes no
/// PID, no handle value, no address and no name but the run's own sections' (P16), so a test reads the ledger as the session's
/// records were. It evaluates the session's records against the truth as the product evaluates sections, and writes the
/// result beside the ledger, which a test reproduces from it.
/// </summary>
internal static class SessionLedger
{
    public const string Header = "ordinal,utcTicks,role,kind,object,view,section,size,high,name";

    /// <summary>The role prefix of a process the truth does not name, numbered in the order its first kept record came.</summary>
    public const string OtherRole = "other-";

    private static readonly string[] TruthFiles = ["truth-creator.jsonl", "truth-opener.jsonl"];

    // A truth log's lines, and the evaluation written beside a ledger, each enum by its name.
    private static readonly JsonSerializerOptions Lines = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static SectionCoverageResult Write(string session, string truth, string output) => Write(session, truth, output, out _);

    /// <summary>As <see cref="Write(string, string, string)"/>, saying when the session's first section record was raised, on its calibrated clock.</summary>
    public static SectionCoverageResult Write(string session, string truth, string output, out long firstRecordUtcTicks)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string[] sections;
        string fixture;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            fixture = scenario.RootElement.GetProperty("scenarioId").GetString()!;
            sections = [.. scenario.RootElement.GetProperty("sections").EnumerateArray().Select(static name => name.GetString()!)];
        }

        TruthRecord[] truthRecords = Truth(truth);
        Dictionary<int, string> roles = truthRecords.GroupBy(static record => record.ProcessId).ToDictionary(static group => group.Key, static group => group.First().Role);

        // Every section record of the session, each by the process the product binds it to, its numbers its source fields.
        List<SectionObservation> observed = Read(session, out long calibratedFrom, out long calibratedTo);
        firstRecordUtcTicks = observed.Count > 0 ? observed[0].UtcTicks ?? 0 : 0;
        long firstRecord = firstRecordUtcTicks;
        var settings = new SectionCoverageSettings
        {
            FixtureId = fixture,
            BuildId = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            BuildIsSupported = true,
            Reproduced = true,
        };
        SectionCoverageResult result = SectionCoverageEvaluator.Evaluate(truthRecords, observed, settings);

        // The ledger: the workload's two processes' every record, and others' on a number a run's section was found under.
        HashSet<long> handleNumbers = [.. result.Sections.Select(static section => section.SectionObjectNumber).OfType<long>()];
        HashSet<long> sectionNumbers = [.. result.Sections.Select(static section => section.SectionNumber).OfType<long>()];
        var others = new Dictionary<int, string>();
        var text = new StringBuilder(Header).Append('\n');
        int written = 0;
        foreach (SectionObservation record in observed)
        {
            bool workload = record.ProcessId is { } process && roles.ContainsKey(process);
            bool onARunSection = record.SectionObjectNumber is { } handle && handleNumbers.Contains(handle)
                || record.SectionNumber is { } section && sectionNumbers.Contains(section);
            if (!workload && !onARunSection)
            {
                continue;
            }

            string role = record.ProcessId is not { } id ? string.Empty
                : roles.TryGetValue(id, out string? known) ? known
                : others.TryGetValue(id, out string? other) ? other
                : (others[id] = OtherRole + (others.Count + 1).ToString("D2", CultureInfo.InvariantCulture));
            string? name = record.Name is { } kernelName && sections.Any(run => SyncObjectCoverageEvaluator.Names(kernelName, run)) ? kernelName : null;
            text.Append(string.Join(',',
                written.ToString(CultureInfo.InvariantCulture),
                record.UtcTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                role,
                record.Kind.ToString(),
                record.SectionObjectNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.ViewNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.SectionNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.ViewSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.ViewInformation is { } information ? "0x" + ((ulong)information >> 48).ToString("X2", CultureInfo.InvariantCulture) : string.Empty,
                name ?? string.Empty)).Append('\n');
            written++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text.ToString());

        // What is committed is the ledger's own evaluation, which a test reproduces from it: the same records of the workload's
        // processes, and of others only those on the run's numbers.
        Dictionary<string, int> processes = truthRecords.GroupBy(static record => record.Role).ToDictionary(static group => group.Key, static group => group.First().ProcessId);
        result = SectionCoverageEvaluator.Evaluate(truthRecords, ReadLedger(output, processes), settings);
        File.WriteAllText(Path.ChangeExtension(output, ".evaluation.json"), JsonSerializer.Serialize(new
        {
            result.Measurement,
            result.Assessment.Tier,
            result.Assessment.Gaps,
            result.Sections,
            result.ObservationsOutsideScope,
            result.Notes,
            SessionSectionRecords = observed.Count,
            LedgerRecords = written,
            OtherProcessesKept = others.Count,
            CalibratedFromUtcTicks = calibratedFrom,
            CalibratedToUtcTicks = calibratedTo,
            FirstSectionRecordUtcTicks = firstRecord,
        }, Indented));
        return result;
    }

    /// <summary>A run's truth: both processes' logs, every line a truth record.</summary>
    public static TruthRecord[] Truth(string truth) =>
    [
        .. TruthFiles.SelectMany(file => File.ReadAllLines(Path.Combine(truth, file))).Where(static line => line.Length > 0)
            .Select(static line => JsonSerializer.Deserialize<TruthRecord>(line, Lines)!),
    ];

    /// <summary>The ledger read back as the session's records were, each role the process the truth names by it, others apart.</summary>
    public static List<SectionObservation> ReadLedger(string ledger, IReadOnlyDictionary<string, int> processes)
    {
        var observed = new List<SectionObservation>();
        foreach (string[] field in File.ReadLines(ledger).Skip(1).Where(static line => line.Length > 0).Select(static line => line.Split(',')))
        {
            int? process = field[2].Length == 0 ? null
                : processes.TryGetValue(field[2], out int id) ? id
                : 1_000_000 + int.Parse(field[2][OtherRole.Length..], CultureInfo.InvariantCulture);
            static long? Number(string value) => value.Length == 0 ? null : long.Parse(value, CultureInfo.InvariantCulture);
            observed.Add(new()
            {
                Ordinal = long.Parse(field[0], CultureInfo.InvariantCulture),
                UtcTicks = Number(field[1]),
                ProcessId = process,
                Kind = Enum.Parse<ObservationKind>(field[3]),
                SectionObjectNumber = Number(field[4]),
                ViewNumber = Number(field[5]),
                SectionNumber = Number(field[6]),
                ViewSize = Number(field[7]),
                ViewInformation = field[8].Length == 0 ? null : (long)(ulong.Parse(field[8][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 48),
                Name = field[9].Length == 0 ? null : field[9],
            });
        }

        return observed;
    }

    // Every section record of the session, in its order, with the numbers its source fields carry and its calibrated time.
    private static List<SectionObservation> Read(string session, out long calibratedFrom, out long calibratedTo)
    {
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        var rows = new List<(long Native, SectionObservation Record)>();
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
            var fields = new Dictionary<(uint Stream, uint Epoch, ulong Ordinal, FactKey Fact), Dictionary<SourceField, long>>();
            foreach (string name in SessionSegments.FieldNames(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    SourceFieldRowV1 field = segment.FieldRow(index);
                    if (field is { Field: SourceField.SectionObjectNumber or SourceField.SectionViewNumber or SourceField.SectionNumber
                        or SourceField.ViewSize or SourceField.ViewInformation, Value: { } value })
                    {
                        var key = (field.RawStreamId, field.RawSourceEpoch, field.RawRecordOrdinal, field.FactKey);
                        (fields.TryGetValue(key, out Dictionary<SourceField, long>? held) ? held : fields[key] = [])[field.Field] = value;
                    }
                }
            }

            foreach (string name in SessionSegments.Names(manifest))
            {
                SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
                for (int index = 0; index < segment.RowCount; index++)
                {
                    ObservationRowV1 row = segment.Row(index);
                    if (row.Mechanism != Mechanism.SharedSection)
                    {
                        continue;
                    }

                    Dictionary<SourceField, long> carried = fields.GetValueOrDefault((row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey)) ?? [];
                    long? Of(SourceField field) => carried.TryGetValue(field, out long value) ? value : null;
                    long utc = first.Utc.UtcTicks + (long)((Int128)(row.NativeTicks - first.NativeTicks)
                        * (last.Utc.UtcTicks - first.Utc.UtcTicks) / (last.NativeTicks - first.NativeTicks));
                    rows.Add((row.NativeTicks, new SectionObservation
                    {
                        Ordinal = 0,
                        Kind = row.Kind,
                        ProcessId = RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId),
                        SectionObjectNumber = Of(SourceField.SectionObjectNumber),
                        Name = row.ResourceName,
                        ViewNumber = Of(SourceField.SectionViewNumber),
                        SectionNumber = Of(SourceField.SectionNumber),
                        ViewSize = Of(SourceField.ViewSize),
                        ViewInformation = Of(SourceField.ViewInformation),
                        UtcTicks = utc,
                    }));
                }
            }
        }

        store.ReleaseSegmentReaders();
        return [.. rows.OrderBy(static row => row.Native).Select(static (row, index) => row.Record with { Ordinal = index })];
    }
}
