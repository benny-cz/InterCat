using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AfdProbe;

/// <summary>
/// M6's Unix-domain sockets work package (plan §4.2, "investigate relevant Winsock/AFD schemas and controlled fixtures"):
/// what Microsoft-Windows-Winsock-AFD records of an AF_UNIX stream exchange - each socket's creation with its address
/// family, its bind, connect, accept and close, and each send and receive with its length - measured against FX-UNIX-001,
/// whose two processes log every message they sent and read. The provider is enabled in one uniquely named real-time
/// session of this probe's own, asked to scope it to the workload's two processes, beside a decoy pair running the same
/// exchange outside them; Kernel-Network is enabled beside it, machine-wide, to say whether TCP/IP's own tracing holds any
/// of it. A record is read as belonging to the process that created its socket, whichever process's thread raised it.
/// Records are compared in memory: only counters are written, and with --ledger each workload record's metadata - its
/// time, owner, the context it was raised in, event, phase, location, a number standing for its socket, its family,
/// length and status - never a pointer, an address or a byte.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.afd-unix-feasibility.v1";

    // AFD_KW_SOURCE_WINSOCK and AFD_KW_SOURCE_TRANSPORT: the socket calls and what the transport indicated to them.
    private const ulong AfdKeywords = 0x4 | 0x8;

    // The keywords the product's Kernel-Network source requests (WindowsSourceCatalog).
    private const ulong NetworkKeywords = 0x30;

    private const ulong AfUnix = 1;

    private static readonly Guid Afd = Guid.Parse("e53c6823-7bb8-44bb-90dc-3f86090d48a6");
    private static readonly Guid KernelNetwork = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

    // The events this probe reads, and the fields each must declare for it to read them.
    private static readonly (int Id, string[] Fields)[] Needed =
    [
        (1000, ["EnterExit", "Location", "Endpoint", "AddressFamily", "SocketType", "Protocol", "ProcessId", "Status"]),
        (1001, ["EnterExit", "Location", "Endpoint", "Status"]),
        (1002, ["EnterExit", "Location", "Endpoint", "Status"]),
        (1003, ["EnterExit", "Location", "Endpoint", "BufferLength", "Status"]),
        (1004, ["EnterExit", "Location", "Endpoint", "BufferLength", "Status"]),
        (1018, ["EnterExit", "Location", "Endpoint", "Status", "Address"]),
        (1027, ["EnterExit", "Location", "Endpoint", "Status", "Address", "AcceptEndpoint"]),
        (1030, ["EnterExit", "Location", "Endpoint", "Status", "Address"]),
        (1037, ["EnterExit", "Location", "Endpoint", "Backlog", "Status"]),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        int connections = int.Parse(Option(args, "--connections") ?? "4", CultureInfo.InvariantCulture);
        int messages = int.Parse(Option(args, "--messages") ?? "16", CultureInfo.InvariantCulture);
        int bytes = int.Parse(Option(args, "--bytes") ?? "4096", CultureInfo.InvariantCulture);
        int concurrency = int.Parse(Option(args, "--concurrency") ?? "1", CultureInfo.InvariantCulture);
        int delay = int.Parse(Option(args, "--delay") ?? "15", CultureInfo.InvariantCulture);
        bool ledger = args.Contains("--ledger", StringComparer.Ordinal);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0",
            "InterCat.TestWorkloads.exe"));
        if (output is null || connections < 1 || messages < 1)
        {
            Console.Error.WriteLine("InterCat.AfdProbe --output <new directory> [--connections n] [--messages n] [--bytes n] [--delay ms]");
            Console.Error.WriteLine("  [--concurrency n] [--ledger] [--workload <InterCat.TestWorkloads.exe>]");
            Console.Error.WriteLine("  [--impact [--pairs n] [--traffic unix|tcp]]: machine CPU and workload time with and without a session");
            Console.Error.WriteLine("             collecting AFD machine-wide, in pairs, over eight connections at once unpaced");
            Console.Error.WriteLine("  Runs FX-UNIX-001's AF_UNIX exchange under a session of its own reading Winsock AFD, asked to scope it");
            Console.Error.WriteLine("  to the workload's two processes beside a decoy pair outside them, and Kernel-Network machine-wide;");
            Console.Error.WriteLine("  writes counters, and with --ledger each workload record's metadata, never a pointer or a byte.");
            return 2;
        }

        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("Enabling a provider for other processes needs elevation; nothing was started.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        if (!File.Exists(workload))
        {
            Console.Error.WriteLine($"No truth workload at {workload}; build src/InterCat.TestWorkloads in Release or pass --workload.");
            return 2;
        }

        Directory.CreateDirectory(output);

        // The layout is taken from the provider's registered schema, read as the product's inventory reads it; an event
        // this build declares otherwise is a finding, and nothing is read by guess.
        ManifestReadResult manifest = new TdhEtwMetadataSource().TryReadManifest(Afd);
        ProviderSchema? schema = manifest.ManifestXml is null ? null : ManifestParser.Parse(manifest.ManifestXml);
        string? schemaProblem = schema is null
            ? "the provider's schema is not registered on this machine: " + manifest.FailureReason
            : CheckSchema(schema);
        if (schemaProblem is not null)
        {
            await WriteAsync(output, new { schema = Schema, measuredUtc = DateTimeOffset.UtcNow, schemaProblem });
            Console.Error.WriteLine("The AFD events do not have the layout this probe reads: " + schemaProblem);
            return 4;
        }

        // What collecting the provider costs, in pairs with and without a session collecting it machine-wide.
        if (args.Contains("--impact", StringComparer.Ordinal))
        {
            int pairs = int.Parse(Option(args, "--pairs") ?? "7", CultureInfo.InvariantCulture);
            string traffic = Option(args, "--traffic") ?? "unix";
            string[] arguments = traffic switch
            {
                "unix" => ["unix-loopback", "--truth", "{truth}", "--connections", "8", "--concurrency", "8", "--messages", messages.ToString(CultureInfo.InvariantCulture), "--delay", "0"],
                "tcp" => ["tcp-loopback", "--truth", "{truth}", "--connections", "8", "--concurrency", "8", "--messages", messages.ToString(CultureInfo.InvariantCulture), "--delay", "0"],
                _ => throw new ArgumentException($"--traffic expects unix or tcp; '{traffic}' is neither."),
            };
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload, arguments, pairs);
            await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        string truth = Path.Combine(output, "truth");
        string decoyTruth = Path.Combine(output, "decoy-truth");
        string socket = Path.Combine(truth, "fx.sock");
        string decoySocket = Path.Combine(decoyTruth, "fx.sock");
        if (socket.Length >= 108 || decoySocket.Length >= 108)
        {
            Console.Error.WriteLine("The output directory's path is too long for a Unix-domain socket's path under it.");
            return 2;
        }

        Process Start(string verb, string truthDirectory, string path, int seed)
        {
            var start = new ProcessStartInfo(workload)
            {
                ArgumentList =
                {
                    verb, "--truth", truthDirectory, "--socket", path, "--seed", seed.ToString(CultureInfo.InvariantCulture),
                    "--connections", connections.ToString(CultureInfo.InvariantCulture),
                    "--messages", messages.ToString(CultureInfo.InvariantCulture),
                    "--bytes", bytes.ToString(CultureInfo.InvariantCulture),
                    "--delay", delay.ToString(CultureInfo.InvariantCulture),
                    "--concurrency", concurrency.ToString(CultureInfo.InvariantCulture),
                    "--wait-for-start",
                },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} did not start.");
        }

        using Process server = Start("unix-loopback-server", truth, socket, 20_261_104);
        using Process client = Start("unix-loopback-client", truth, socket, 20_261_104);
        using Process decoyServer = Start("unix-loopback-server", decoyTruth, decoySocket, 20_261_105);
        using Process decoyClient = Start("unix-loopback-client", decoyTruth, decoySocket, 20_261_105);
        Process[] all = [server, client, decoyServer, decoyClient];
        var roles = new Dictionary<int, string>
        {
            [server.Id] = "server", [client.Id] = "client", [decoyServer.Id] = "decoy server", [decoyClient.Id] = "decoy client",
        };

        var records = new List<Record>();
        var gate = new Lock();
        long unreadable = 0, networkOfTheWorkload = 0, networkInAll = 0, arrival = 0;
        int eventsLost;
        Dictionary<(int, int), ProviderSchemaEvent> events = schema!.Events
            .GroupBy(e => (e.EventId, e.Version)).ToDictionary(group => group.Key, group => group.First());
        string sessionName = string.Create(CultureInfo.InvariantCulture, $"InterCat-AfdProbe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var clock = Stopwatch.StartNew();
        try
        {
            using var session = new TraceEventSession(sessionName) { StopOnDispose = true };
            session.Source.AllEvents += data =>
            {
                if (data.ProviderGuid == KernelNetwork)
                {
                    Interlocked.Increment(ref networkInAll);
                    byte[] network = data.EventData();
                    int named = network.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(network) : -1;
                    if (data.ProcessID == server.Id || data.ProcessID == client.Id || named == server.Id || named == client.Id)
                    {
                        Interlocked.Increment(ref networkOfTheWorkload);
                    }

                    return;
                }

                if (data.ProviderGuid != Afd) return;

                // Every record is kept, whichever process raised it: the session's process filter is a request, whose holding
                // is measured, and a socket's record can be raised on another process's thread.
                if (!events.TryGetValue(((int)data.ID, data.Version), out ProviderSchemaEvent? declared)
                    || Fields(declared, data.EventData(), data.PointerSize) is not { } fields)
                {
                    Interlocked.Increment(ref unreadable);
                    return;
                }

                lock (gate)
                {
                    records.Add(new((int)data.ID, declared.TaskName ?? declared.Symbol ?? string.Empty, data.ProcessID,
                        data.ThreadID, data.TimeStampRelativeMSec, arrival++, fields));
                }
            };
            session.EnableProvider(Afd, TraceEventLevel.Informational, AfdKeywords,
                new TraceEventProviderOptions { ProcessIDFilter = [server.Id, client.Id] });
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, NetworkKeywords);
            var reader = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "afd-probe-events" };
            reader.Start();

            // The enable reaches the processes asynchronously; then each server listens, and once it says so its client
            // connects.
            await Task.Delay(1_500);
            foreach ((Process listening, Process connecting) in new[] { (server, client), (decoyServer, decoyClient) })
            {
                await listening.StandardInput.WriteLineAsync("start");
                await listening.StandardInput.FlushAsync();
                string? ready = await listening.StandardOutput.ReadLineAsync();
                if (ready is null) throw new InvalidOperationException("A server ended before it listened.");
                await connecting.StandardInput.WriteLineAsync("start");
                await connecting.StandardInput.FlushAsync();
            }

            foreach (Process started in all)
            {
                Task<string> errors = started.StandardError.ReadToEndAsync();
                _ = await started.StandardOutput.ReadToEndAsync();
                if (!started.WaitForExit(180_000))
                {
                    started.Kill();
                    Console.Error.WriteLine($"Process {started.Id} did not finish within three minutes and was stopped.");
                }

                string said = await errors;
                if (said.Length > 0) Console.Error.WriteLine($"{started.Id}: " + said.Trim());
            }

            // A real-time session hands over its last buffers on its flush timer.
            await Task.Delay(2_500);
            eventsLost = session.EventsLost;
            session.Stop();
            reader.Join(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (Process started in all)
            {
                if (!started.HasExited) started.Kill();
            }
        }

        double seconds = clock.Elapsed.TotalSeconds;
        Dictionary<Record, Incarnation> sockets = Incarnations(records, out long unowned, out long reused);
        object report = new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            workload = new { connections, messages, bytes, concurrency, delay },
            session = new
            {
                eventsLost,
                seconds = Math.Round(seconds, 1),
                afdRecords = records.Count,
                afdRecordsUnreadable = unreadable,
                afdRecordsRaisedBy = records.GroupBy(record => roles.GetValueOrDefault(record.RaisedBy, "another process"))
                    .ToDictionary(group => group.Key, group => group.Count()),
                processesRaisingAfdRecords = records.Select(record => record.RaisedBy).Distinct().Count(),
                recordsOfNoSocketCreatedInTheSession = unowned,
                socketsCreatedAtAnAddressAnEarlierSocketHad = reused,
                kernelNetworkRecordsNamingTheWorkload = networkOfTheWorkload,
                kernelNetworkRecordsInAll = networkInAll,
                note = "The session asked AFD for the workload's two processes alone, beside a decoy pair running the same exchange "
                    + "outside them; Kernel-Network was enabled machine-wide, as the product's TCP and UDP source requests it.",
            },
            server = Describe(records, sockets, server.Id, client.Id, Truth(truth, "truth-server.jsonl"), socket),
            client = Describe(records, sockets, client.Id, server.Id, Truth(truth, "truth-client.jsonl"), socket),
            decoyServer = Brief(records, sockets, decoyServer.Id, Truth(decoyTruth, "truth-server.jsonl")),
            decoyClient = Brief(records, sockets, decoyClient.Id, Truth(decoyTruth, "truth-client.jsonl")),
        };
        await WriteAsync(output, report);
        if (ledger)
        {
            WriteLedger(Path.Combine(output, "records.csv"), records, sockets, roles, server.Id, client.Id);
        }

        // The truth logs name the socket by its path in this run's own folder; the exchange's bytes are synthetic.
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    /// <summary>
    /// Each record's socket, read in time order: a create (its entry) begins a socket at its endpoint address, owned by the
    /// process it was made for, and every later record naming that address is that socket's until another create names it;
    /// an accept's new socket, at an address no socket holds yet, is its listener's owner's. A record naming an address no
    /// create of the session named belongs to none.
    /// </summary>
    private static Dictionary<Record, Incarnation> Incarnations(List<Record> records, out long unowned, out long reused)
    {
        var sockets = new Dictionary<Record, Incarnation>(ReferenceEqualityComparer.Instance);
        var current = new Dictionary<ulong, Incarnation>();
        var seen = new HashSet<ulong>();
        long numbered = 0;
        unowned = 0;
        reused = 0;
        foreach (Record record in records.OrderBy(record => record.Milliseconds).ThenBy(record => record.Arrival))
        {
            if (record.Number("Endpoint") is not { } endpoint || endpoint == 0) continue;
            if (record.Event == 1000 && record.Number("EnterExit") == 0)
            {
                int owner = record.Number("ProcessId") is { } named && named != 0 ? (int)named : record.RaisedBy;
                current[endpoint] = new(++numbered, owner, record.Number("AddressFamily") ?? 0);
                if (!seen.Add(endpoint)) reused++;
            }

            if (!current.TryGetValue(endpoint, out Incarnation? socket))
            {
                unowned++;
                continue;
            }

            sockets[record] = socket;
            if (record.Event is 1024 or 1027 && record.Number("AcceptEndpoint") is { } accepted && accepted != 0
                && !current.ContainsKey(accepted))
            {
                current[accepted] = socket with { Number = ++numbered };
            }
        }

        return sockets;
    }

    /// <summary>
    /// What one process's AF_UNIX sockets' records say beside its truth: each event's records on them by phase and location,
    /// with their lengths and the context each was raised in - its own process, its peer's, or another's.
    /// </summary>
    private static object Describe(List<Record> all, Dictionary<Record, Incarnation> sockets, int process, int peer,
        TruthTally truth, string socket)
    {
        Record[] own = [.. all.Where(record => sockets.TryGetValue(record, out Incarnation? made) && made.Owner == process && made.Family == AfUnix)];
        string Context(Record record) => record.RaisedBy == process ? "own" : record.RaisedBy == peer ? "peer's" : "another's";

        return new
        {
            truth,
            socketsCreated = sockets.Values.Where(made => made.Owner == process).DistinctBy(made => made.Number)
                .GroupBy(made => made.Family).ToDictionary(group => "family " + group.Key.ToString(CultureInfo.InvariantCulture), group => group.Count()),
            unixSockets = own.Select(record => sockets[record].Number).Distinct().Count(),
            recordsOnThem = own.Length,
            byEvent = own
                .GroupBy(record => (record.Event, record.Name, Phase: record.Number("EnterExit"), Location: record.Number("Location")))
                .OrderBy(group => group.Key.Event).ThenBy(group => group.Key.Phase).ThenBy(group => group.Key.Location)
                .ToDictionary(
                    group => string.Create(CultureInfo.InvariantCulture,
                        $"{group.Key.Event} {group.Key.Name}, phase {group.Key.Phase}, location {group.Key.Location}"),
                    group => new
                    {
                        records = group.Count(),
                        statusZero = group.Count(record => record.Number("Status") is null or 0),
                        bufferLength = group.Sum(record => (long)(record.Number("BufferLength") ?? 0)),
                        raisedIn = group.GroupBy(Context).ToDictionary(context => context.Key, context => context.Count()),
                    }),
            addresses = Addresses(own, socket),
        };
    }

    /// <summary>A decoy's sockets and records, counted: whether the session held them out, and whether they were AF_UNIX.</summary>
    private static object Brief(List<Record> all, Dictionary<Record, Incarnation> sockets, int process, TruthTally truth)
    {
        Record[] own = [.. all.Where(record => sockets.TryGetValue(record, out Incarnation? made) && made.Owner == process && made.Family == AfUnix)];
        return new
        {
            truth,
            unixSockets = own.Select(record => sockets[record].Number).Distinct().Count(),
            recordsOnThem = own.Length,
            sendLength = own.Where(record => record.Event == 1003 && record.Number("EnterExit") == 1 && record.Number("Location") == 3051)
                .Sum(record => (long)(record.Number("BufferLength") ?? 0)),
            recordsRaisedByIt = all.Count(record => record.RaisedBy == process),
        };
    }

    /// <summary>Each address a bind, connect or accept named on these sockets, by family and whether its path is the run's socket's.</summary>
    private static Dictionary<string, int> Addresses(Record[] own, string socket) => own
        .Where(record => record.Bytes("Address") is not null)
        .GroupBy(record =>
        {
            string named = string.Create(CultureInfo.InvariantCulture, $"{record.Event} {record.Name}, phase {record.Number("EnterExit")}");
            byte[] address = record.Bytes("Address")!;
            if (address.Length < 2) return named + ", no address";
            ushort family = BinaryPrimitives.ReadUInt16LittleEndian(address);
            if (family != AfUnix) return named + ", family " + family.ToString(CultureInfo.InvariantCulture);
            int end = Array.IndexOf(address, (byte)0, 2);
            string path = Encoding.UTF8.GetString(address, 2, (end < 0 ? address.Length : end) - 2);
            return named + ", AF_UNIX, " + (string.Equals(path, socket, StringComparison.OrdinalIgnoreCase)
                ? "the run's socket path"
                : path.Length == 0 ? "no path" : "another path");
        })
        .ToDictionary(group => group.Key, group => group.Count());

    private static TruthTally Truth(string directory, string file)
    {
        var tally = new TruthTally();
        foreach (string line in File.ReadLines(Path.Combine(directory, file)))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            string kind = record.RootElement.GetProperty("kind").GetString()!;
            long completed = record.RootElement.TryGetProperty("completedBytes", out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64() : 0;
            switch (kind)
            {
                case "ConnectionEstablished" or "ConnectionAccepted":
                    tally.Connections++;
                    break;
                case "MessageSent":
                    tally.Sends++;
                    tally.BytesSent += completed;
                    break;
                case "MessageReceived":
                    tally.Receives++;
                    tally.BytesReceived += completed;
                    break;
            }
        }

        return tally;
    }

    /// <summary>Each record on the workload's sockets, in time order: its metadata, never a pointer, an address or a byte.</summary>
    private static void WriteLedger(string path, List<Record> records, Dictionary<Record, Incarnation> sockets,
        Dictionary<int, string> roles, int server, int client)
    {
        // A socket is numbered by its incarnation, in the order the ledger first names it: never by its address.
        var numbers = new Dictionary<long, int>();
        int Number(Incarnation socket) => numbers.TryGetValue(socket.Number, out int known) ? known : numbers[socket.Number] = numbers.Count + 1;
        Record[] workload = [.. records.Where(record => sockets.TryGetValue(record, out Incarnation? made)
            && (made.Owner == server || made.Owner == client))
            .OrderBy(record => record.Milliseconds).ThenBy(record => record.Arrival)];
        double first = workload.Length == 0 ? 0 : workload[0].Milliseconds;
        var lines = new List<string> { "milliseconds,owner,raisedBy,thread,event,phase,location,socket,family,length,status" };
        foreach (Record record in workload)
        {
            Incarnation socket = sockets[record];
            lines.Add(string.Join(',',
                (record.Milliseconds - first).ToString("0.000", CultureInfo.InvariantCulture),
                roles[socket.Owner],
                roles.GetValueOrDefault(record.RaisedBy, "another process"),
                record.Thread.ToString(CultureInfo.InvariantCulture),
                record.Event.ToString(CultureInfo.InvariantCulture),
                record.Number("EnterExit")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.Number("Location")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Number(socket).ToString(CultureInfo.InvariantCulture),
                socket.Family.ToString(CultureInfo.InvariantCulture),
                record.Number("BufferLength")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.Number("Status")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// The record's fields, read in order by their declared widths - a pointer by the record's own pointer size, a binary
    /// field by the length another field states - or null where the body ends before a field it declares.
    /// </summary>
    private static Dictionary<string, object>? Fields(ProviderSchemaEvent declared, byte[] body, int pointerSize)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        int offset = 0;
        foreach (ProviderSchemaField field in declared.Fields)
        {
            int width = field.WidthKind switch
            {
                FieldWidthKind.Fixed => field.FixedWidth,
                FieldWidthKind.PointerSized => pointerSize,
                _ => field.LengthField is { } named && values.TryGetValue(named, out object? length) && length is ulong count ? (int)count : -1,
            };
            if (width < 0 || offset + width > body.Length) return null;
            ReadOnlySpan<byte> slice = body.AsSpan(offset, width);
            object value = (field.InType, width) switch
            {
                ("win:UInt32" or "win:HexInt32" or "win:Int32", 4) => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slice),
                ("win:UInt16" or "win:Int16", 2) => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(slice),
                ("win:Pointer", 8) or ("win:UInt64" or "win:Int64" or "win:HexInt64", 8) => BinaryPrimitives.ReadUInt64LittleEndian(slice),
                ("win:Pointer", 4) => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slice),
                _ => slice.ToArray(),
            };
            values[field.Name] = value;
            offset += width;
        }

        return values;
    }

    private static string? CheckSchema(ProviderSchema schema)
    {
        foreach ((int id, string[] needed) in Needed)
        {
            if (schema.FindEvent(id) is not { } descriptor)
            {
                return $"event {id} is not declared";
            }

            string[] missing = [.. needed.Where(name => descriptor.Fields.All(field => field.Name != name))];
            if (missing.Length > 0)
            {
                return $"event {id} declares no {string.Join(", ", missing)}";
            }
        }

        return null;
    }

    private static async Task WriteAsync(string output, object report) =>
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));

    private static string? Option(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
        }

        return null;
    }

    /// <summary>
    /// One socket's life at one endpoint address: from the create that named the address to the next create that names it
    /// again, an address the kernel frees and reuses being no identity of its own (R22). Its owner is the process the create
    /// was made for, or an accept's listener's owner for the socket the accept made.
    /// </summary>
    private sealed record Incarnation(long Number, int Owner, ulong Family);

    /// <summary>One AFD record: its event, the process and thread that raised it, its place in arrival order and its fields as declared.</summary>
    private sealed record Record(int Event, string Name, int RaisedBy, int Thread, double Milliseconds, long Arrival,
        Dictionary<string, object> FieldValues)
    {
        public ulong? Number(string name) => FieldValues.TryGetValue(name, out object? value) && value is ulong number ? number : null;

        public byte[]? Bytes(string name) => FieldValues.TryGetValue(name, out object? value) ? value as byte[] : null;
    }

    /// <summary>What one process's truth log says it did.</summary>
    private sealed class TruthTally
    {
        public int Connections { get; set; }
        public int Sends { get; set; }
        public long BytesSent { get; set; }
        public int Receives { get; set; }
        public long BytesReceived { get; set; }
    }
}
