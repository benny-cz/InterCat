using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;

namespace InterCat.SyncProbe;

/// <summary>
/// M6's named synchronization objects work package (plan §4.1, "optional context-switch/ready-thread tracing, object
/// evidence"): what this machine declares for the kernel's object manager and its dispatcher. Neither has a manifest: each
/// is a classic kernel class, a flag group only a system logger receives (ADR-035). So it reads from TDH the layout the
/// machine registers for each of their events, as the product reads ALPC's, and the registered schema of the one manifest
/// provider whose name says it audits kernel API calls. The inspection is read-only: it enables no provider, starts no
/// session and raises no event, so what it writes is evidence of schemas on one machine, never of what the kernel emits.
/// With --measure it records what the kernel emits of FX-SYNC-001, under a session of its own (<see cref="Measure"/>).
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.sync-provider-inspection.v1";

    // The kernel's classic classes that name a kernel object or a thread's wait, as root\wmi registers them on Windows
    // 10.0.26220: the object manager's (ObTrace) every opcode, and the thread class's context switch and ready thread - its
    // other events are a thread's lifecycle, which the product reads from Kernel-Process.
    private static readonly (string Name, Guid Class, int[] Opcodes)[] Classes =
    [
        ("Object manager (ObTrace class)", Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7"), [.. Enumerable.Range(0, 256)]),
        ("Thread dispatcher (Thread class)", Guid.Parse("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c"), [36, 50]),
    ];

    // The registered manifest providers whose names say they audit kernel objects or API calls, as logman lists them.
    private static readonly string[] Providers = ["Microsoft-Windows-Kernel-Audit-API-Calls"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        // A session icat record recorded is read, not captured: the ledger of its workload's synchronization handle records
        // as the product holds them, evaluated against the workload's truth.
        if (Option(args, "--session") is { } recorded)
        {
            return SessionLedger.Write(recorded, Option(args, "--truth"), Option(args, "--out"));
        }

        string? output = Option(args, "--output");
        bool inspect = args.Contains("--inspect", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        if (inspect == measure || output is null)
        {
            Console.Error.WriteLine("InterCat.SyncProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the layouts this machine registers for the kernel object manager's");
            Console.Error.WriteLine("  events and the dispatcher's context switch and ready thread, and the schema of the provider that");
            Console.Error.WriteLine("  audits kernel API calls, and writes each event with its fields.");
            Console.Error.WriteLine("InterCat.SyncProbe --measure --output <new directory> [--rounds n] [--hold-ms ms] [--ledger]");
            Console.Error.WriteLine("  [--workload <InterCat.TestWorkloads.exe>]: runs FX-SYNC-001 under a private system logger of its own");
            Console.Error.WriteLine("  collecting the kernel's handle and dispatcher events machine-wide, and writes what it recorded of the");
            Console.Error.WriteLine("  run's three named objects and of the threads one process's signals readied in the other; with");
            Console.Error.WriteLine("  --ledger each kept record by role, never a PID, a thread, a handle's value or an address.");
            Console.Error.WriteLine("  --impact [--pairs n] [--churn n] [--objects]: machine CPU and workload time with and without a session");
            Console.Error.WriteLine("  collecting the object manager's handle group - and with --objects its object group - machine-wide, in");
            Console.Error.WriteLine("  pairs, over FX-SYNC-001's opener opening and closing each object that many times, its rounds unpaced.");
            Console.Error.WriteLine("  --session-check: an owned capture through the product's session host, its plan the handle group alone,");
            Console.Error.WriteLine("  while FX-SYNC-001 runs: what its session delivered by class and opcode, and its group masks.");
            Console.Error.WriteLine("  --types: the object type table read from user mode before a session, against the type rundown at its");
            Console.Error.WriteLine("  stop, and the machine's handle records by type over FX-SYNC-001.");
            Console.Error.WriteLine("  --product-impact [--rounds n] [--churn n] [--icat <InterCat.Cli.exe>]: machine CPU and workload time over");
            Console.Error.WriteLine("  FX-SYNC-001's opener churning handles, in rounds with no capture, under icat record --profile sync-objects");
            Console.Error.WriteLine("  and under icat record --profile explore, keeping only counters.");
            Console.Error.WriteLine("  --duplication [--ledger]: FX-SYNC-002, the opener also given each object's handle by a duplication -");
            Console.Error.WriteLine("  pushed, pulled, and made between the two by a third process: which process raised each duplication's");
            Console.Error.WriteLine("  record and its handle's, and where the synchronization types' kernel handles' records are raised.");
            Console.Error.WriteLine("  --generations n [--rounds n] [--hold-ms ms]: FX-SYNC-003, the three objects made and ended n times one");
            Console.Error.WriteLine("  after another: how many objects the kernel made at an address an earlier generation's held, counted.");
            Console.Error.WriteLine("InterCat.SyncProbe --session <recorded session> --truth <its workload's truth> --out <records.csv>");
            Console.Error.WriteLine("  Reads the synchronization handle records of a session icat record recorded under the synchronization");
            Console.Error.WriteLine("  objects profile, as the product holds them, writes the ledger of the workload's own and of other");
            Console.Error.WriteLine("  processes' on its objects by role, never by PID, and evaluates it.");
            return 2;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        Directory.CreateDirectory(output);

        // Whether a capture can know the handle records' type numbers as it starts, and how the records divide among types.
        if (measure && args.Contains("--types", StringComparer.Ordinal))
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            Dictionary<string, object?> types = await Types.RunAsync(workload, Path.Combine(output, "truth")).ConfigureAwait(false);
            types["schema"] = "intercat.sync-objects-types.v1";
            types["measuredUtc"] = DateTimeOffset.UtcNow;
            types["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(types, Json));
            Console.WriteLine(JsonSerializer.Serialize(types, Json));
            return 0;
        }

        // What the synchronization objects profile costs through the product: icat record itself, beside Explore and no
        // capture, over FX-SYNC-001's opener churning handles.
        if (measure && args.Contains("--product-impact", StringComparer.Ordinal))
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            string icat = Option(args, "--icat") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.Cli", "bin", "Release", "net10.0", "InterCat.Cli.exe"));
            int rounds = int.Parse(Option(args, "--rounds") ?? "9", CultureInfo.InvariantCulture);
            int churn = int.Parse(Option(args, "--churn") ?? "4096", CultureInfo.InvariantCulture);
            string scratch = Path.Combine(output, "scratch");
            Directory.CreateDirectory(scratch);
            Dictionary<string, object?> impact = await ProductImpact.MeasureAsync(workload, icat, churn, rounds, scratch).ConfigureAwait(false);
            Directory.Delete(scratch, recursive: true);
            var document = new Dictionary<string, object?>
            {
                ["schema"] = "intercat.sync-objects-product-impact.v1",
                ["measuredUtc"] = DateTimeOffset.UtcNow,
                ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            };
            foreach ((string key, object? value) in impact)
            {
                document[key] = value;
            }

            File.WriteAllText(Path.Combine(output, "product-impact.json"), JsonSerializer.Serialize(document, Json));
            Console.WriteLine(JsonSerializer.Serialize(document, Json));
            return 0;
        }

        // Where the kernel makes objects made and ended one generation after another: FX-SYNC-003's addresses, counted.
        if (measure && Option(args, "--generations") is { } generationCount)
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            Dictionary<string, object?> generations = await Generations.RunAsync(workload, output,
                int.Parse(generationCount, CultureInfo.InvariantCulture),
                int.Parse(Option(args, "--rounds") ?? "2", CultureInfo.InvariantCulture),
                int.Parse(Option(args, "--hold-ms") ?? "0", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            generations["schema"] = "intercat.sync-objects-generations.v1";
            generations["measuredUtc"] = DateTimeOffset.UtcNow;
            generations["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(generations, Json));
            Console.WriteLine(JsonSerializer.Serialize(generations, Json));
            return 0;
        }

        // Which process a handle's duplication is raised in, and the kernel's own handles' records: FX-SYNC-002.
        if (measure && args.Contains("--duplication", StringComparer.Ordinal))
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            Dictionary<string, object?> duplication = await Duplication.RunAsync(workload, output,
                args.Contains("--ledger", StringComparer.Ordinal)).ConfigureAwait(false);
            duplication["schema"] = "intercat.sync-objects-duplication.v1";
            duplication["measuredUtc"] = DateTimeOffset.UtcNow;
            duplication["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(duplication, Json));
            Console.WriteLine(JsonSerializer.Serialize(duplication, Json));
            return 0;
        }

        // The product's own capture path for the handle group: an owned capture through the product's session host.
        if (measure && args.Contains("--session-check", StringComparer.Ordinal))
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            Dictionary<string, object?> check = await SessionCheck.RunAsync(workload, Path.Combine(output, "truth")).ConfigureAwait(false);
            check["schema"] = "intercat.sync-objects-session-check.v1";
            check["measuredUtc"] = DateTimeOffset.UtcNow;
            check["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(check, Json));
            Console.WriteLine(JsonSerializer.Serialize(check, Json));
            return 0;
        }

        // What collecting the object manager's groups costs, in pairs with and without a session collecting them
        // machine-wide, over FX-SYNC-001's opener churning handles.
        if (measure && args.Contains("--impact", StringComparer.Ordinal))
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            int pairs = int.Parse(Option(args, "--pairs") ?? "9", CultureInfo.InvariantCulture);
            string churn = Option(args, "--churn") ?? "4096";
            uint[] groups = args.Contains("--objects", StringComparer.Ordinal)
                ? [GroupMask.ObjectHandles, GroupMask.ObjectLifetimes]
                : [GroupMask.ObjectHandles];
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload,
                ["sync-objects", "--truth", "{truth}", "--rounds", "64", "--hold-ms", "0", "--churn", churn], pairs, groups).ConfigureAwait(false);
            impact["schema"] = "intercat.sync-objects-impact.v1";
            impact["measuredUtc"] = DateTimeOffset.UtcNow;
            impact["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        return measure ? await Measure.RunAsync(output, args).ConfigureAwait(false) : Inspect(output);
    }

    private static int Inspect(string output)
    {
        var metadata = new TdhEtwMetadataSource();
        var classes = new List<object>();
        foreach ((string name, Guid classGuid, int[] opcodes) in Classes)
        {
            // Every version a classic class could carry: TDH describes an opcode only at a version the machine registers.
            var events = new List<object>();
            for (int version = 0; version <= 5; version++)
            {
                if (metadata.TryReadClassic(classGuid, name, version, opcodes) is not { } schema)
                {
                    continue;
                }

                events.AddRange(schema.Events.Select(static descriptor => new
                {
                    version = descriptor.Version,
                    opcode = descriptor.OpcodeValue,
                    task = descriptor.TaskName,
                    opcodeName = descriptor.OpcodeName,
                    fields = descriptor.Fields.Select(static field => new { name = field.Name, type = field.InType }),
                }));
            }

            classes.Add(new { name, guid = classGuid, events });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {events.Count} events described"));
        }

        var providers = new List<object>();
        foreach (string name in Providers)
        {
            providers.Add(Manifest(metadata, name));
        }

        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: each classic class's layouts and each provider's registered schema as TDH gives them, read as "
                + "the product reads one. No provider was enabled and no session started, so nothing here says what the "
                + "kernel emits.",
            classes,
            providers,
        }, Json));
        return 0;
    }

    private static object Manifest(TdhEtwMetadataSource metadata, string name)
    {
        RegisteredProvider? provider = metadata.TryResolveProvider(name);
        if (provider is null)
        {
            Console.WriteLine($"{name}: not registered");
            return new { name, registered = false };
        }

        ManifestReadResult manifest = metadata.TryReadManifest(provider.ProviderGuid);
        ProviderSchema? schema = null;
        string? problem = manifest.FailureReason;
        if (manifest.ManifestXml is { } xml)
        {
            try
            {
                schema = ManifestParser.Parse(xml);
            }
            catch (InvalidDataException exception)
            {
                problem = "The manifest could not be interpreted: " + exception.Message;
            }
        }

        if (schema is null)
        {
            Console.WriteLine($"{name}: registered as {provider.ProviderGuid:B}; no schema read: {problem}");
            return new { name, guid = provider.ProviderGuid, registered = true, schemaProblem = problem };
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: registered as {provider.ProviderGuid:B}; {schema.Events.Count} events, {schema.Keywords.Count} keywords"));
        return new
        {
            name,
            guid = provider.ProviderGuid,
            registered = true,
            schemaFingerprint = schema.SchemaFingerprint,
            keywords = schema.Keywords.OrderBy(static keyword => keyword.Value)
                .Select(static keyword => new { name = keyword.Key, mask = Hex(keyword.Value) }),
            events = schema.Events.OrderBy(static descriptor => descriptor.EventId).ThenBy(static descriptor => descriptor.Version)
                .Select(static descriptor => new
                {
                    id = descriptor.EventId,
                    version = descriptor.Version,
                    symbol = descriptor.Symbol,
                    task = descriptor.TaskName,
                    opcode = descriptor.OpcodeName,
                    level = descriptor.Level,
                    keywords = descriptor.Keywords,
                    fields = descriptor.Fields.Select(static field => new { name = field.Name, inType = field.InType, outType = field.OutType }),
                }),
        };
    }

    private static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
