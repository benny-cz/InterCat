using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;

namespace InterCat.SyncProbe;

/// <summary>
/// M6's named synchronization objects work package (plan §4.1, "optional context-switch/ready-thread tracing, object
/// evidence"): what this machine declares for the kernel's object manager and its dispatcher. Neither has a manifest: each
/// is a classic kernel class, a flag group only a system logger receives (ADR-035). So it reads from TDH the layout the
/// machine registers for each of their events, as the product reads ALPC's, and the registered schema of the one manifest
/// provider whose name says it audits kernel API calls. It is read-only: it enables no provider, starts no session and
/// raises no event, so what it writes is evidence of schemas on one machine, never of what the kernel emits.
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

    private static int Main(string[] args)
    {
        string? output = Option(args, "--output");
        if (!args.Contains("--inspect", StringComparer.Ordinal) || output is null)
        {
            Console.Error.WriteLine("InterCat.SyncProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the layouts this machine registers for the kernel object manager's");
            Console.Error.WriteLine("  events and the dispatcher's context switch and ready thread, and the schema of the provider that");
            Console.Error.WriteLine("  audits kernel API calls, and writes each event with its fields.");
            return 2;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        Directory.CreateDirectory(output);
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
