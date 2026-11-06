using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SectionProbe;

/// <summary>
/// FX-SECT-001 under a private system logger of the probe's own (ADR-035), machine-wide: process lifecycle and the kernel's
/// file view flag (VAMAP), which raises each view's mapping and unmapping, with the object manager's handle group added to the
/// session's masks (<see cref="GroupMask"/>). What the kernel records of two named sections two processes share - each
/// process's handles to them, with each section's name, address and type, and each view it mapped and unmapped, with its base,
/// size and file object - set against each process's truth. It keeps the records of the run's sections and of the workload's
/// processes' views, and counts the rest; the ledger it writes names each by role and ordinal, never a PID, a handle value or
/// an address.
/// </summary>
internal static class Measure
{
    private const string Schema = "intercat.section-measurement.v1";

    // Every FX-SECT-001 section's name begins so after its namespace; the run's own name follows.
    private const string Prefix = "InterCat-FX-SECT-001-";

    private static readonly Guid ObjectManagerClass = Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7");
    private static readonly Guid FileIoClass = Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // How far from its truth record a view's record may lie: the record is raised as the call maps or unmaps the view, and the
    // truth written once it returned, on the workload's clock.
    private static readonly TimeSpan MatchWindow = TimeSpan.FromMilliseconds(5);

    public static async Task<int> RunAsync(string output, string[] args)
    {
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
        int rounds = int.Parse(Option(args, "--rounds") ?? "4", CultureInfo.InvariantCulture);
        int bytes = int.Parse(Option(args, "--view-bytes") ?? "65536", CultureInfo.InvariantCulture);
        string truth = Path.Combine(output, "truth");

        var capture = new Capture();
        int lost;
        int coordinatorProcessId;
        string[]? masks;
        string sessionName = "InterCat-section-probe-" + Guid.NewGuid().ToString("N");

        // A session of its own name: a kernel provider in a session not named NT Kernel Logger is a private system logger, which
        // this probe owns and stops, and which touches no other session. The kernel flags start it in that mode; the object
        // manager's handle group, which no flag names, is then added to its masks.
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 256 })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.VAMap);
            if (GroupMask.Add(sessionName, [GroupMask.ObjectHandles]) is { } problem)
            {
                Console.Error.WriteLine("The object manager's handle group could not be added to the probe's session. " + problem);
                return 5;
            }

            masks = GroupMask.Read(sessionName);
            capture.Subscribe(session.Source);
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(1_500).ConfigureAwait(false);
            capture.WorkloadStartedUtc = DateTime.UtcNow;
            using (Process coordinator = Process.Start(new ProcessStartInfo(workload,
                ["section-share", "--truth", truth, "--rounds", rounds.ToString(CultureInfo.InvariantCulture),
                    "--view-bytes", bytes.ToString(CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                coordinatorProcessId = coordinator.Id;
                await coordinator.WaitForExitAsync().ConfigureAwait(false);
                if (coordinator.ExitCode != 0)
                {
                    Console.Error.WriteLine($"The truth workload exited with {coordinator.ExitCode}.");
                    return 4;
                }
            }

            capture.WorkloadEndedUtc = DateTime.UtcNow;
            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        JsonElement root = scenario.RootElement;
        var run = new Run(
            [.. root.GetProperty("sections").EnumerateArray().Select(static section => section.GetString()!)],
            coordinatorProcessId,
            root.GetProperty("creatorProcessId").GetInt32(),
            root.GetProperty("openerProcessId").GetInt32(),
            [.. TruthViews(Path.Combine(truth, "truth-creator.jsonl")), .. TruthViews(Path.Combine(truth, "truth-opener.jsonl"))]);
        Dictionary<string, object?> report = Analyze(capture, run, bytes, rounds);
        report["schema"] = Schema;
        report["measuredUtc"] = DateTimeOffset.UtcNow;
        report["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
        report["session"] = new Dictionary<string, object?>
        {
            ["mode"] = "private system logger, uniquely named, owned and stopped by the probe",
            ["kernelFlags"] = new[] { "Process", "VAMap" },
            ["groupMasks"] = new[] { "PERF_OB_HANDLE" },
            ["masksRead"] = masks,
            ["eventsLost"] = lost,
        };
        report["workload"] = new Dictionary<string, object>
        {
            ["scenario"] = "FX-SECT-001",
            ["viewBytes"] = bytes,
            ["rounds"] = rounds,
            ["seconds"] = Math.Round((capture.WorkloadEndedUtc - capture.WorkloadStartedUtc).TotalSeconds, 1),
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "ledger.csv"), Ledger(capture, run)).ConfigureAwait(false);
        if (args.Contains("--diagnose", StringComparer.Ordinal))
        {
            // The probe's own diagnosis, never committed: the maps and unmaps raised in or naming the workload's processes, by
            // their raw payload, and the first of all others.
            int[] workloadProcesses = [run.CreatorProcessId, run.OpenerProcessId, run.CoordinatorProcessId];
            await File.WriteAllLinesAsync(Path.Combine(output, "diagnosis.txt"),
            [
                $"creator {run.CreatorProcessId}, opener {run.OpenerProcessId}, coordinator {run.CoordinatorProcessId}",
                .. capture.Handles.Select(record => $"handle {record.Kind} pid {record.ProcessId} object 0x{record.Object:X} type {capture.TypeName(record.ObjectType)} name {record.Name ?? record.ClosedAs}"),
                .. capture.Views.Where(view => view.Opcode is 37 or 38).GroupBy(static view => (view.Version, view.PayloadBytes, view.Between))
                    .Select(group => $"layout v{group.Key.Version} {group.Key.PayloadBytes} bytes between [{group.Key.Between}]: {group.Count()} records, payload pid = header pid in {group.Count(static view => view.ProcessId == view.HeaderProcessId)}"),
                .. capture.Views.Where(view => view.Opcode is 37 or 38 && (workloadProcesses.Contains(view.ProcessId) || workloadProcesses.Contains(view.HeaderProcessId)))
                    .Select(view => $"workload op{view.Opcode} {view.Utc:HH:mm:ss.ffffff} header {view.HeaderProcessId} payload-pid {view.ProcessId} base 0x{view.Base:X} file 0x{view.FileObject:X} misc 0x{view.MiscInfo:X} size {view.Size} raw {view.Payload}"),
                .. capture.Views.Where(view => view.Opcode is 37 or 38).Take(10)
                    .Select(view => $"any op{view.Opcode} {view.Utc:HH:mm:ss.ffffff} header {view.HeaderProcessId} payload-pid {view.ProcessId} base 0x{view.Base:X} file 0x{view.FileObject:X} misc 0x{view.MiscInfo:X} size {view.Size} raw {view.Payload}"),
            ]).ConfigureAwait(false);
        }

        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    private static Dictionary<string, object?> Analyze(Capture capture, Run run, int bytes, int rounds)
    {
        // The run's sections, each by its handles - its kernel name ends with the section's own - and the object addresses
        // those handles name.
        var sections = new List<Dictionary<string, object?>>();
        foreach (string section in run.Sections)
        {
            string tail = section[(section.IndexOf('\\', StringComparison.Ordinal) + 1)..];
            ulong[] objects = [.. capture.Handles.Where(record => record.ClosedAs?.EndsWith(tail, StringComparison.Ordinal) == true
                || record.Name?.EndsWith(tail, StringComparison.Ordinal) == true).Select(static record => record.Object).Distinct()];
            HandleRecord[] records = [.. capture.Handles.Where(record => objects.Contains(record.Object))];
            sections.Add(new()
            {
                ["section"] = tail.EndsWith("-memory", StringComparison.Ordinal) ? "memory" : "file",
                ["objects"] = objects.Length,
                ["typeNames"] = records.Select(record => capture.TypeName(record.ObjectType)).Distinct().ToArray(),
                ["handles"] = records.GroupBy(record => (run.RoleOf(record.ProcessId), record.Kind))
                    .OrderBy(static group => group.Key.Item1, StringComparer.Ordinal).ThenBy(static group => group.Key.Kind)
                    .Select(static group => new { role = group.Key.Item1, kind = group.Key.Kind.ToString(), records = group.Count(),
                        named = group.Count(static record => record.Name is not null) })
                    .ToArray(),
            });
        }

        ViewRecord[] workloadViews = WorkloadViews(capture, run);
        List<(TruthView Truth, ViewRecord? Record)> matched = Match(capture, run);

        // Every view of one section shares one value in its file object field, in any process: how many each section's views
        // name, whether it is in both processes, whether it is a section object's address, and whether any other record of the
        // workload's processes names it.
        ulong[] sectionObjects = [.. capture.Handles.Select(static record => record.Object).Distinct()];
        var kinds = new List<Dictionary<string, object?>>();
        foreach (IGrouping<string, (TruthView Truth, ViewRecord? Record)> kind in matched.GroupBy(static pair => pair.Truth.Kind))
        {
            ViewRecord[] records = [.. kind.Select(static pair => pair.Record).OfType<ViewRecord>()];
            ulong[] values = [.. records.Select(static record => record.FileObject).Distinct()];
            kinds.Add(new()
            {
                ["section"] = kind.Key == "section" ? "memory" : "file",
                ["truthViews"] = kind.Count(),
                ["recorded"] = records.Length,
                ["sizesAsLogged"] = kind.All(pair => pair.Record is not { } record || !pair.Truth.Mapped || (long)record.Size == pair.Truth.Bytes),
                ["unmappedAtTheirBase"] = kind.Where(static pair => !pair.Truth.Mapped && pair.Record is not null).All(pair =>
                    kind.Any(map => map.Truth.Mapped && map.Truth.Role == pair.Truth.Role && map.Truth.Round == pair.Truth.Round
                        && map.Record is { } mapped && mapped.Base == pair.Record!.Base)),
                ["viewValues"] = values.Length,
                ["viewValueInBothProcesses"] = values.Length == 1 && records.Select(record => run.RoleOf(record.HeaderProcessId)).Distinct().Count() == 2,
                ["viewValueIsASectionObject"] = values.Any(value => sectionObjects.Contains(value)),
                ["otherRecordsNamingIt"] = workloadViews.Count(view => values.Contains(view.FileObject) && !records.Contains(view)),
                ["miscInfoHighBytes"] = records.Select(static record => "0x" + (record.MiscInfo >> 48).ToString("X2", CultureInfo.InvariantCulture)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                ["maxLagMicroseconds"] = kind.Where(static pair => pair.Record is not null)
                    .Select(pair => Math.Round((pair.Truth.Utc - pair.Record!.Utc).TotalMilliseconds * 1000, 1)).DefaultIfEmpty().Max(),
            });
        }

        // The layout the mappings and unmappings were raised at: their versions and lengths, what they carry between the size
        // and the process, and whether the process they carry is the one that raised them.
        ViewRecord[] all = [.. capture.Views.Where(static view => view.Opcode is 37 or 38)];
        return new()
        {
            ["question"] = "What the kernel records of two named sections two processes share - one backed by the paging file and one by a file - "
                + "under the object manager's handle group and the file view flag: each process's handles to them, and each view it mapped and unmapped.",
            ["truth"] = new Dictionary<string, object?>
            {
                ["sections"] = run.Sections.Length,
                ["creatorViews"] = run.Sections.Length,
                ["openerViews"] = run.Sections.Length * rounds,
                ["viewBytes"] = bytes,
            },
            ["sections"] = sections,
            ["views"] = kinds,
            ["viewLayout"] = new Dictionary<string, object?>
            {
                ["records"] = all.Length,
                ["versions"] = all.Select(static view => view.Version).Distinct().Order().ToArray(),
                ["payloadBytes"] = all.Select(static view => view.PayloadBytes).Distinct().Order().ToArray(),
                ["betweenSizeAndProcessBytes"] = all.Select(static view => view.Between.Length / 2).Distinct().Order().ToArray(),
                ["betweenPageAligned"] = all.All(static view => view.Between.Length != 16
                    || BitConverter.ToUInt64(Convert.FromHexString(view.Between)) % 4096 == 0),
                ["processIsTheRaiser"] = all.All(static view => view.ProcessId == view.HeaderProcessId),
            },
            ["workloadOtherViews"] = workloadViews.Length - matched.Count(static pair => pair.Record is not null),
            ["truthBytesReadThroughViews"] = run.Views.Where(static view => !view.Mapped && view.Role == "opener").Sum(static view => view.Bytes),
            ["counts"] = new Dictionary<string, object?>
            {
                ["handleEvents"] = capture.HandleEvents,
                ["viewEvents"] = capture.ViewEvents,
                ["viewEventsByOpcode"] = capture.ViewsByOpcode.OrderBy(static pair => pair.Key)
                    .ToDictionary(static pair => pair.Key.ToString(CultureInfo.InvariantCulture), static pair => pair.Value),
                ["typeNames"] = capture.TypeNames.Count,
            },
        };
    }

    // The mappings and unmappings the workload's two processes raised, by the process each record's header names.
    private static ViewRecord[] WorkloadViews(Capture capture, Run run) =>
        [.. capture.Views.Where(view => run.RoleOf(view.HeaderProcessId) is "creator" or "opener" && view.Opcode is 37 or 38)];

    // Each truth view by the record of its mapping or unmapping: the nearest in its process, of its kind, within the window of
    // its log line - the call returns, and is logged, after the kernel raised its record - and taken by no earlier truth view.
    private static List<(TruthView Truth, ViewRecord? Record)> Match(Capture capture, Run run)
    {
        ViewRecord[] workloadViews = WorkloadViews(capture, run);
        var matched = new List<(TruthView Truth, ViewRecord? Record)>();
        var taken = new HashSet<ViewRecord>(ReferenceEqualityComparer.Instance);
        foreach (TruthView view in run.Views)
        {
            int process = view.Role == "creator" ? run.CreatorProcessId : run.OpenerProcessId;
            ViewRecord? record = workloadViews
                .Where(candidate => candidate.HeaderProcessId == process && candidate.Opcode == (view.Mapped ? 37 : 38)
                    && Math.Abs((candidate.Utc - view.Utc).TotalMilliseconds) <= MatchWindow.TotalMilliseconds && !taken.Contains(candidate))
                .MinBy(candidate => Math.Abs((candidate.Utc - view.Utc).Ticks));
            if (record is not null)
            {
                taken.Add(record);
            }

            matched.Add((view, record));
        }

        return matched;
    }

    // The ledger of the run's records by role, in the order the session delivered them: each handle record of the run's
    // sections by its section's kind, its object's ordinal and its type, and each view record of a truth view by its section's
    // kind as the truth logged it, the ordinal of its base within its process, its size, its offset, the ordinal of the value
    // its file object field carries and its extra information's high byte - never a PID, a handle value or an address. The
    // workload's other views, the runtime's own, are counted in the report, not listed.
    private static string Ledger(Capture capture, Run run)
    {
        var objectOrdinals = new Dictionary<ulong, int>();
        var baseOrdinals = new Dictionary<(int, ulong), int>();
        var valueOrdinals = new Dictionary<ulong, int>();
        int Ordinal<T>(Dictionary<T, int> ordinals, T key) where T : notnull => ordinals.TryGetValue(key, out int known) ? known : (ordinals[key] = ordinals.Count + 1);
        string Kind(string? name) => run.Sections.FirstOrDefault(section =>
            name?.EndsWith(section[(section.IndexOf('\\', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal) == true) is { } named
            ? (named.EndsWith("-memory", StringComparison.Ordinal) ? "memory" : "file") : string.Empty;
        var rows = new List<(DateTime Utc, string Line)>();
        foreach (HandleRecord record in capture.Handles)
        {
            rows.Add((record.Utc, string.Join(',', record.Kind.ToString(), run.RoleOf(record.ProcessId), Kind(record.Name ?? record.ClosedAs),
                Ordinal(objectOrdinals, record.Object).ToString(CultureInfo.InvariantCulture), capture.TypeName(record.ObjectType) ?? string.Empty,
                record.Name is not null ? "named" : string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)));
        }

        foreach ((TruthView truth, ViewRecord? view) in Match(capture, run))
        {
            if (view is null)
            {
                continue;
            }

            rows.Add((view.Utc, string.Join(',', truth.Mapped ? "MapView" : "UnmapView", truth.Role, truth.Kind == "section" ? "memory" : "file",
                string.Empty, string.Empty, string.Empty,
                Ordinal(baseOrdinals, (view.HeaderProcessId, view.Base)).ToString(CultureInfo.InvariantCulture),
                view.Size.ToString(CultureInfo.InvariantCulture),
                view.Between.Length == 16 ? BitConverter.ToUInt64(Convert.FromHexString(view.Between)).ToString(CultureInfo.InvariantCulture) : string.Empty,
                Ordinal(valueOrdinals, view.FileObject).ToString(CultureInfo.InvariantCulture),
                "0x" + (view.MiscInfo >> 48).ToString("X2", CultureInfo.InvariantCulture))));
        }

        var text = new StringBuilder("kind,role,section,object,type,named,view,bytes,offset,viewValue,miscHigh\n");
        foreach ((_, string line) in rows.OrderBy(static row => row.Utc))
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    // A truth log's views: each mapping and unmapping it logged, by its process's role, its section's kind, its round and when.
    private static IEnumerable<TruthView> TruthViews(string path)
    {
        foreach (string line in File.ReadLines(path).Where(static line => line.Length > 0))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            JsonElement root = record.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            if (kind is not ("ViewMapped" or "ViewUnmapped"))
            {
                continue;
            }

            long Number(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
            yield return new(root.GetProperty("role").GetString()!, root.GetProperty("status").GetString()!, kind == "ViewMapped",
                (int)Number("callId"), kind == "ViewMapped" ? Number("declaredBytes") : Number("completedBytes"),
                root.GetProperty("recordedUtc").GetDateTimeOffset().UtcDateTime);
        }
    }

    /// <summary>One view a truth log mapped or unmapped: its process's role, its section's kind, its round, its bytes and when.</summary>
    internal sealed record TruthView(string Role, string Kind, bool Mapped, int Round, long Bytes, DateTime Utc);

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    internal enum HandleKind
    {
        Create,
        Close,
        Duplicate,
        Rundown,
    }

    internal sealed record HandleRecord(HandleKind Kind, DateTime Utc, int ProcessId, ulong Object, int ObjectType, string? Name)
    {
        /// <summary>For a creation, which names nothing, the name its close carried.</summary>
        public string? ClosedAs { get; init; }
    }

    internal sealed record ViewRecord(int Opcode, DateTime Utc, int ProcessId, ulong Base, ulong FileObject, ulong MiscInfo, ulong Size)
    {
        /// <summary>The process the record's header says raised it, beside the one its payload names.</summary>
        public int HeaderProcessId { get; init; }

        /// <summary>The record's payload as delivered, for the probe's own diagnosis.</summary>
        public string Payload { get; init; } = string.Empty;

        /// <summary>The version the record was raised at, and its payload's length.</summary>
        public int Version { get; init; }

        public int PayloadBytes { get; init; }

        /// <summary>The bytes a record longer than the registered layout carries between its size and its process.</summary>
        public string Between { get; init; } = string.Empty;
    }

    /// <summary>A run's sections by their names, its processes by role, and the views its truth logs mapped and unmapped.</summary>
    internal sealed record Run(string[] Sections, int CoordinatorProcessId, int CreatorProcessId, int OpenerProcessId, TruthView[] Views)
    {
        public string RoleOf(int processId) => processId == CreatorProcessId ? "creator"
            : processId == OpenerProcessId ? "opener"
            : processId == CoordinatorProcessId ? "coordinator"
            : "other";
    }

    /// <summary>What the session delivered: the run's sections' handles, the workload's views, and counts of the rest.</summary>
    internal sealed class Capture
    {
        private readonly HashSet<ulong> objects = [];
        private readonly Dictionary<(int, ulong), HandleRecord> created = [];

        public List<HandleRecord> Handles { get; } = [];

        public List<ViewRecord> Views { get; } = [];

        public Dictionary<int, string> TypeNames { get; } = [];

        public Dictionary<int, long> ViewsByOpcode { get; } = [];

        public long HandleEvents { get; private set; }

        public long ViewEvents { get; private set; }

        public DateTime WorkloadStartedUtc { get; set; }

        public DateTime WorkloadEndedUtc { get; set; }

        public string? TypeName(int type) => TypeNames.GetValueOrDefault(type);

        public void Subscribe(TraceEventDispatcher source)
        {
            source.Kernel.ObjectCreateHandle += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 6);
                var record = new HandleRecord(HandleKind.Create, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.Object,
                    data.ObjectType, name.Length > 0 ? name : null);
                created[(data.ProcessID, data.Object)] = record;
                if (name.Contains(Prefix, StringComparison.Ordinal))
                {
                    Handles.Add(record);
                    objects.Add(data.Object);
                }
            };
            source.Kernel.ObjectCloseHandle += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 6);
                bool held = created.Remove((data.ProcessID, data.Object), out HandleRecord? create);
                if (!name.Contains(Prefix, StringComparison.Ordinal) && !objects.Contains(data.Object))
                {
                    return;
                }

                // The close names the section; the creation it closes, which named none, is the run's too.
                objects.Add(data.Object);
                if (held && !Handles.Contains(create!))
                {
                    Handles.Add(create! with { ClosedAs = name.Length > 0 ? name : null });
                }

                Handles.Add(new(HandleKind.Close, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.Object, data.ObjectType,
                    name.Length > 0 ? name : null));
            };
            source.Kernel.ObjectDuplicateHandle += data =>
            {
                HandleEvents++;
                if (objects.Contains(data.Object))
                {
                    Handles.Add(new(HandleKind.Duplicate, data.TimeStamp.ToUniversalTime(), data.TargetProcessID, data.Object, data.ObjectType, null));
                }
            };
            source.Kernel.ObjectHandleDCEnd += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 10);
                if (name.Contains(Prefix, StringComparison.Ordinal))
                {
                    Handles.Add(new(HandleKind.Rundown, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.Object, data.ObjectType, name));
                }
            };
            source.Kernel.ObjectTypeDCEnd += data => TypeNames[data.ObjectType] = data.ObjectTypeName;
            source.AllEvents += data =>
            {
                // A view's mapping and unmapping and their rundowns, which TraceEvent does not parse, read by the layout the
                // machine registers (§4.2): the view's base, its file object, extra information, its size and its process.
                if (data.TaskGuid != FileIoClass || (int)data.Opcode is < 37 or > 40)
                {
                    return;
                }

                int opcode = (int)data.Opcode;
                ViewEvents++;
                ViewsByOpcode[opcode] = ViewsByOpcode.GetValueOrDefault(opcode) + 1;
                byte[] payload = data.EventData();
                int pointer = data.PointerSize;
                if (payload.Length < (3 * pointer) + 8 + 4)
                {
                    return;
                }

                ulong Pointer(int offset) => pointer == 8 ? BitConverter.ToUInt64(payload, offset) : BitConverter.ToUInt32(payload, offset);
                ulong viewBase = Pointer(0);
                ulong fileObject = Pointer(pointer);
                ulong misc = BitConverter.ToUInt64(payload, 2 * pointer);
                ulong size = Pointer((2 * pointer) + 8);

                // The registered layout ends with the process after the size; a record longer than it carries more between
                // them, so its process is read where the record ends.
                int process = BitConverter.ToInt32(payload, payload.Length - 4);
                Views.Add(new(opcode, data.TimeStamp.ToUniversalTime(), process, viewBase, fileObject, misc, size)
                {
                    HeaderProcessId = data.ProcessID,
                    Version = data.Version,
                    PayloadBytes = payload.Length,
                    Between = payload.Length > (3 * pointer) + 8 + 4 ? Convert.ToHexString(payload, (3 * pointer) + 8, payload.Length - ((3 * pointer) + 8) - 4) : string.Empty,
                    Payload = opcode is 37 or 38 ? Convert.ToHexString(payload) : string.Empty,
                });
            };
        }

        // The name a handle record carries, read by the layout the machine registers (§4.2) after its fixed fields, which
        // TraceEvent does not read; empty when the record names none.
        private static string NameAt(TraceEvent data, int offset)
        {
            byte[] payload = data.EventData();
            if (payload.Length <= offset + sizeof(char))
            {
                return string.Empty;
            }

            string text = Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
            int end = text.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }
    }
}
