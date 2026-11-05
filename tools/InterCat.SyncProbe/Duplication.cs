using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SyncProbe;

/// <summary>
/// FX-SYNC-002: which process the object manager raises a handle's duplication in, and the records of the handle a
/// duplication made. FX-SYNC-001's two processes made each of their handles by name, and each record was raised in the
/// process whose handle it was; a duplication's record names the process it made a handle in and that handle, but not the
/// process it duplicated from. So this runs FX-SYNC-001 with the opener also given a handle of each object three ways - the
/// creator pushing its event's into it, the opener pulling the semaphore's from the creator, and the coordinator, a process
/// at neither end, duplicating the mutex's from the one into the other - and reads, machine-wide over the same session,
/// where the kernel's own handles' records are raised: a kernel handle is held in the system process's table whichever
/// process the kernel was running for when it made it.
/// </summary>
internal static class Duplication
{
    // The object manager's handle records, by opcode: a handle's creation, its close, its duplication, and the stop's rundown
    // of a handle still open.
    private const int Create = 32;
    private const int Close = 33;
    private const int Duplicate = 34;
    private const int Rundown = 39;

    // A kernel handle's value, as the records carry it in 32 bits, has its top bit set.
    private const uint KernelHandleBit = 0x8000_0000;
    private const int SystemProcessId = 4;

    private static readonly string[] Synchronization = ["Event", "Mutant", "Semaphore"];
    private static readonly string[] Roles = ["creator", "opener", "coordinator"];
    private static readonly JsonSerializerOptions TruthJson = CreateTruthJson();

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string output, bool ledger)
    {
        string truth = Path.Combine(output, "truth");
        var records = new List<Record>();
        var types = new Dictionary<int, string>();
        int lost;
        string sessionName = "InterCat-sync-duplication-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 256 })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            if (GroupMask.Add(sessionName, [GroupMask.ObjectHandles]) is { } problem)
            {
                throw new InvalidOperationException("The handle group could not be added to the probe's session. " + problem);
            }

            session.Source.Kernel.ObjectCreateHandle += data => records.Add(Read(data, Create));
            session.Source.Kernel.ObjectCloseHandle += data => records.Add(Read(data, Close));
            session.Source.Kernel.ObjectDuplicateHandle += data => records.Add(Read(data, Duplicate));
            session.Source.Kernel.ObjectHandleDCEnd += data => records.Add(Read(data, Rundown));
            session.Source.Kernel.ObjectTypeDCEnd += data => types[data.ObjectType] = data.ObjectTypeName;
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(1_500).ConfigureAwait(false);
            using (Process started = Process.Start(new ProcessStartInfo(workload,
                ["sync-objects", "--truth", truth, "--rounds", "16", "--hold-ms", "5", "--duplicate"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await started.WaitForExitAsync().ConfigureAwait(false);
                if (started.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {started.ExitCode}.");
                }
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        var run = new Run(
            scenario.RootElement.GetProperty("coordinatorProcessId").GetInt32(),
            scenario.RootElement.GetProperty("creatorProcessId").GetInt32(),
            scenario.RootElement.GetProperty("openerProcessId").GetInt32());
        TruthRecord[] logged = [.. Roles.SelectMany(role => File.ReadLines(Path.Combine(truth, $"truth-{role}.jsonl"))
            .Select(line => JsonSerializer.Deserialize<TruthRecord>(line, TruthJson)!))];
        HashSet<int> synchronization = [.. types.Where(type => Synchronization.Contains(type.Value, StringComparer.Ordinal)).Select(type => type.Key)];

        // Each duplication the truth logged, and its object by the creator's close of the handle it started from, which
        // names the object: every duplication starts from one of the creator's handles.
        var objects = new Dictionary<ulong, string>();
        var duplications = new List<object>();
        foreach (TruthRecord made in logged.Where(record => record.Kind == TruthEventKind.HandleDuplicated))
        {
            HandleDuplication ends = made.Duplication!;
            string resource = made.ResourceName!;
            string part = resource[(resource.IndexOf('\\', StringComparison.Ordinal) + 1)..];
            ulong[] addresses = [.. records.Where(record => record.Opcode == Close && record.RaisedBy == ends.SourceProcessId
                && record.Handle == (uint)ends.SourceHandle && record.Name.EndsWith(part, StringComparison.Ordinal)).Select(record => record.Object).Distinct()];
            foreach (ulong address in addresses)
            {
                objects[address] = made.Status!;
            }

            Record[] duplicated = [.. records.Where(record => record.Opcode == Duplicate && addresses.Contains(record.Object)
                && record.TargetProcess == ends.TargetProcessId && record.TargetHandle == (uint)ends.TargetHandle)];

            // The records of the handle the duplication made, from the duplication on: a creation of its value to the object,
            // raised anywhere, and a close of it raised in its target or in a process holding no handle of its own of that
            // value to the object - two processes' handles to one object can share a value, as fresh processes' often do.
            DateTime from = duplicated.Length == 1 ? duplicated[0].Utc : DateTime.MinValue;
            bool OwnHandle(int process) => records.Any(record => record.Opcode == Create && addresses.Contains(record.Object)
                && record.RaisedBy == process && record.Handle == (uint)ends.TargetHandle && record.Utc < from);
            Record[] creations = [.. records.Where(record => record.Opcode == Create && addresses.Contains(record.Object)
                && record.Handle == (uint)ends.TargetHandle && record.Utc >= from)];
            Record[] closes = [.. records.Where(record => record.Opcode == Close && addresses.Contains(record.Object)
                && record.Handle == (uint)ends.TargetHandle && record.Utc >= from
                && (record.RaisedBy == ends.TargetProcessId || !OwnHandle(record.RaisedBy)))];
            // What a later version carries after version 2's fields: a value naming one of the duplication's processes, and
            // a name, each said by what it is, never by its value.
            string ValueAfterType(Record record) => record.After switch
            {
                null => "none",
                { } value when value == (uint)ends.SourceProcessId => "the source process",
                { } value when value == (uint)ends.TargetProcessId => "the target process",
                { } value when value == (uint)record.RaisedBy => "the process that asked",
                0 => "zero",
                _ => "another value",
            };
            duplications.Add(new Dictionary<string, object?>
            {
                ["object"] = made.Status,
                ["way"] = made.Role switch { "creator" => "push", "opener" => "pull", _ => "between two others" },
                ["askedBy"] = made.Role,
                ["source"] = run.RoleOf(ends.SourceProcessId),
                ["target"] = run.RoleOf(ends.TargetProcessId),
                ["objectsFound"] = addresses.Length,
                ["records"] = duplicated.Length,
                ["headerVersions"] = Counts(duplicated.Select(record => record.Version.ToString(CultureInfo.InvariantCulture))),
                ["payloadBytes"] = Counts(duplicated.Select(record => record.Length.ToString(CultureInfo.InvariantCulture))),
                ["valueAfterType"] = Counts(duplicated.Select(ValueAfterType)),
                ["nameAfterType"] = Counts(duplicated.Select(record => Generalize(record.NameAfterType, part))),
                ["nameAfterValue"] = Counts(duplicated.Select(record => Generalize(record.NameAfterValue, part))),
                ["raisedIn"] = Counts(duplicated.Select(record => run.RoleOf(record.RaisedBy))),
                ["sourceHandleCarried"] = duplicated.Length > 0 && duplicated.All(record => record.Handle == (uint)ends.SourceHandle),
                ["creationsOfTheHandleMade"] = Counts(creations.Select(record => run.RoleOf(record.RaisedBy))),
                ["closesOfTheHandleMade"] = Counts(closes.Select(record => run.RoleOf(record.RaisedBy))),
                ["closesNamed"] = closes.Count(record => record.Name.Length > 0),
            });
        }

        // Machine-wide: the synchronization types' handle records of kernel handles, by where they were raised, and every
        // duplication of theirs by whether it was raised in the process it made its handle in.
        Record[] ofSynchronization = [.. records.Where(record => synchronization.Contains(record.Type))];
        bool Kernel(Record record) => ((record.Opcode == Duplicate ? record.TargetHandle : record.Handle) & KernelHandleBit) != 0;
        int Holder(Record record) => record.Opcode switch
        {
            Rundown => record.Holder,
            Duplicate => record.TargetProcess,
            _ => record.RaisedBy,
        };
        var kernelHandles = new Dictionary<string, object?>();
        foreach ((string label, int opcode) in new[] { ("creations", Create), ("closes", Close), ("duplications", Duplicate), ("rundown", Rundown) })
        {
            Record[] kernel = [.. ofSynchronization.Where(record => record.Opcode == opcode && Kernel(record))];
            Record[] user = [.. ofSynchronization.Where(record => record.Opcode == opcode && !Kernel(record))];
            kernelHandles[label] = new Dictionary<string, object?>
            {
                ["kernelHandles"] = kernel.Length,
                ["kernelRaisedInSystem"] = kernel.Count(record => record.RaisedBy == SystemProcessId),
                ["kernelRaisedElsewhere"] = kernel.Count(record => record.RaisedBy != SystemProcessId),
                ["kernelRaisedInProcesses"] = kernel.Where(record => record.RaisedBy != SystemProcessId).Select(record => record.RaisedBy).Distinct().Count(),
                ["kernelHeldBySystem"] = kernel.Count(record => Holder(record) == SystemProcessId),
                ["userHandles"] = user.Length,
                ["userRaisedInSystem"] = user.Count(record => record.RaisedBy == SystemProcessId),
                ["userHeldBySystem"] = user.Count(record => Holder(record) == SystemProcessId),
            };
        }

        Record[] machineDuplications = [.. ofSynchronization.Where(record => record.Opcode == Duplicate)];
        Dictionary<string, int> duplicationShapes = Counts(records.Where(record => record.Opcode == Duplicate)
            .Select(record => string.Create(CultureInfo.InvariantCulture, $"version {record.Version}, {record.Length} bytes")));
        var report = new Dictionary<string, object?>
        {
            ["question"] = "Which process the object manager raises a handle's duplication in - pushed by its source, pulled by its "
                + "target, or made between two others by a third - and where it raises the records of the handle a duplication "
                + "made; and, machine-wide, where the records of the synchronization types' kernel handles are raised.",
            ["scenario"] = "FX-SYNC-002",
            ["duplications"] = duplications,
            ["kernelHandles"] = kernelHandles,
            ["machineDuplications"] = new Dictionary<string, object?>
            {
                ["records"] = machineDuplications.Length,
                ["raisedInTheirTarget"] = machineDuplications.Count(record => record.RaisedBy == record.TargetProcess),
                ["raisedElsewhere"] = machineDuplications.Count(record => record.RaisedBy != record.TargetProcess),
                ["ofEveryTypeByShape"] = duplicationShapes,
            },
            ["synchronizationTypes"] = Synchronization.ToDictionary(name => name, name => (object?)types.FirstOrDefault(type => type.Value == name).Key),
            ["handleRecords"] = records.Count,
            ["synchronizationRecords"] = ofSynchronization.Length,
            ["eventsLost"] = lost,
        };

        if (ledger)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ledger.csv"), Ledger(records, objects, run, types)).ConfigureAwait(false);
        }

        return report;
    }

    // Each record of the run's three objects by role and object kind, with its time, never a PID, a handle's value or an
    // address: an object by the order its address first appears, and a handle by the order its process, value and object do.
    private static string Ledger(List<Record> records, Dictionary<ulong, string> objects, Run run, Dictionary<int, string> types)
    {
        var objectOrdinals = new Dictionary<ulong, int>();
        var handleOrdinals = new Dictionary<(int Process, uint Handle, ulong Object), int>();
        int Handle(int process, uint handle, ulong address) =>
            handleOrdinals.TryGetValue((process, handle, address), out int known) ? known : handleOrdinals[(process, handle, address)] = handleOrdinals.Count + 1;

        // A duplication's version and the role of the process its fourth field names, which version 3 carries after the type
        // and no layout the machine registers describes.
        var text = new StringBuilder("utcTicks,record,role,kind,object,handle,targetRole,targetHandle,type,typeName,nameCarried,version,sourceRole\n");
        foreach (Record record in records.Where(record => objects.ContainsKey(record.Object)).OrderBy(static record => record.Utc))
        {
            int objectOrdinal = objectOrdinals.TryGetValue(record.Object, out int seen) ? seen : objectOrdinals[record.Object] = objectOrdinals.Count + 1;
            string row = record.Opcode switch
            {
                // A duplication's source handle is named by the ordinal of the handle of that value to the object a process
                // was seen to hold, as the record names no process for it; its target handle by the target's.
                Duplicate => string.Create(CultureInfo.InvariantCulture,
                    $"Duplicate,{run.RoleOf(record.RaisedBy)},{objects[record.Object]},{objectOrdinal},{SourceOrdinal(record)},{run.RoleOf(record.TargetProcess)},{Handle(record.TargetProcess, record.TargetHandle, record.Object)}"),
                Rundown => string.Create(CultureInfo.InvariantCulture,
                    $"Rundown,{run.RoleOf(record.Holder)},{objects[record.Object]},{objectOrdinal},{Handle(record.Holder, record.Handle, record.Object)},,"),
                _ => string.Create(CultureInfo.InvariantCulture,
                    $"{(record.Opcode == Create ? "Create" : "Close")},{run.RoleOf(record.RaisedBy)},{objects[record.Object]},{objectOrdinal},{Handle(record.RaisedBy, record.Handle, record.Object)},,"),
            };
            text.Append(CultureInfo.InvariantCulture,
                $"{record.Utc.Ticks},{row},{record.Type},{types.GetValueOrDefault(record.Type) ?? string.Empty},{record.Name.Length > 0},{record.Version},{(record.After is { } after ? run.RoleOf((int)after) : string.Empty)}\n");
        }

        return text.ToString();

        string SourceOrdinal(Record duplicate)
        {
            int[] held = [.. handleOrdinals.Where(pair => pair.Key.Handle == duplicate.Handle && pair.Key.Object == duplicate.Object).Select(pair => pair.Value)];
            return held.Length == 1 ? held[0].ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    // A kernel name with the run's own part and the session's number taken out, which says nothing of the machine; an empty
    // one is said to be none.
    private static string Generalize(string? kernelName, string runPart) => string.IsNullOrEmpty(kernelName)
        ? "none"
        : System.Text.RegularExpressions.Regex.Replace(kernelName.Replace(runPart, "<run>", StringComparison.Ordinal),
            @"\\Sessions\\\d+\\", @"\Sessions\<n>\");

    private static Dictionary<string, int> Counts(IEnumerable<string> roles) =>
        roles.GroupBy(role => role, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count());

    // One handle record by the layout the machine registers (§4.2), read from its payload as the product reads it: the
    // object's address, the handle, the type, a duplication's target process and handle, a rundown's holding process and
    // a close's or a rundown's name, with the process its header names.
    private static Record Read(TraceEvent data, int opcode)
    {
        byte[] payload = data.EventData();
        int pointer = data.PointerSize;
        DateTime utc = data.TimeStamp.ToUniversalTime();
        int needed = opcode switch { Duplicate => pointer + 14, Rundown => pointer + 10, _ => pointer + 6 };
        if (payload.Length < needed)
        {
            return new(opcode, utc, data.ProcessID, 0, 0, -1, 0, 0, 0, string.Empty) { Version = data.Version, Length = payload.Length };
        }

        ulong address = pointer == 8 ? BitConverter.ToUInt64(payload, 0) : BitConverter.ToUInt32(payload, 0);
        return opcode switch
        {
            // Object, SourceHandle, TargetHandle, TargetProcessId, ObjectType - the layout the machine registers at version 2 -
            // and whatever a later version carries after them: a 32-bit value and a name, where its length holds them.
            Duplicate => new(opcode, utc, data.ProcessID, address, BitConverter.ToUInt32(payload, pointer), BitConverter.ToUInt16(payload, pointer + 12),
                (int)BitConverter.ToUInt32(payload, pointer + 8), BitConverter.ToUInt32(payload, pointer + 4), 0, string.Empty)
            {
                Version = data.Version,
                Length = payload.Length,
                After = payload.Length >= pointer + 18 ? BitConverter.ToUInt32(payload, pointer + 14) : null,
                NameAfterType = NameAt(payload, pointer + 14),
                NameAfterValue = NameAt(payload, pointer + 18),
            },

            // Object, ProcessId, Handle, ObjectType, ObjectName.
            Rundown => new(opcode, utc, data.ProcessID, address, BitConverter.ToUInt32(payload, pointer + 4), BitConverter.ToUInt16(payload, pointer + 8),
                0, 0, (int)BitConverter.ToUInt32(payload, pointer), NameAt(payload, pointer + 10))
            {
                Version = data.Version,
                Length = payload.Length,
            },

            // Object, Handle, ObjectType, ObjectName.
            _ => new(opcode, utc, data.ProcessID, address, BitConverter.ToUInt32(payload, pointer), BitConverter.ToUInt16(payload, pointer + 4),
                0, 0, 0, NameAt(payload, pointer + 6))
            {
                Version = data.Version,
                Length = payload.Length,
            },
        };
    }

    // The name a record carries after its fixed fields; empty when it names none.
    private static string NameAt(byte[] payload, int offset)
    {
        if (payload.Length <= offset + sizeof(char))
        {
            return string.Empty;
        }

        string text = Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    private static JsonSerializerOptions CreateTruthJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// One handle record: its opcode, time, the process its header names, the object's address, the handle - a duplication's
    /// source handle - the type, a duplication's target process and handle, a rundown's holding process, and its name.
    /// </summary>
    private readonly record struct Record(int Opcode, DateTime Utc, int RaisedBy, ulong Object, uint Handle, int Type, int TargetProcess,
        uint TargetHandle, int Holder, string Name)
    {
        /// <summary>The version its header names, and its payload's length.</summary>
        public int Version { get; init; }

        public int Length { get; init; }

        /// <summary>A duplication's 32-bit value after its type, where its payload holds one; null where it ends there.</summary>
        public uint? After { get; init; }

        /// <summary>A duplication's name read right after its type, and after that 32-bit value: empty where none is.</summary>
        public string? NameAfterType { get; init; }

        public string? NameAfterValue { get; init; }
    }

    /// <summary>The run's three processes, by which a record's process is named by its role, never its PID.</summary>
    private sealed record Run(int Coordinator, int Creator, int Opener)
    {
        public string RoleOf(int processId) => processId == Creator ? "creator"
            : processId == Opener ? "opener"
            : processId == Coordinator ? "coordinator"
            : processId == SystemProcessId ? "system"
            : "other";
    }
}
