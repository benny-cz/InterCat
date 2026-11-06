using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SectionProbe;

/// <summary>
/// M6's section discovery work package (plan §4.2: shared memory and sections, "feasibility-gated mapping/object evidence"):
/// what this machine declares that could record a section two processes share - its creation, a process opening it by name
/// or given a handle to it, a view of it mapped or unmapped - read from TDH without enabling any. The kernel's object manager
/// and its memory manager have no manifest: each is a classic kernel class, a flag group only a system logger receives
/// (ADR-035), so it reads the layout the machine registers for each of their events, as the product reads ALPC's - the object
/// manager's every opcode, which name a handle's object and type, and the page fault class's, among which a file view's
/// mapping and unmapping. It also reads the registered schema of every manifest provider whose name says memory, sections or
/// mappings, and lists their events that name one. It is read-only: it enables no provider, starts no session and raises no
/// event, so what it writes is evidence of schemas on one machine, never of what the kernel emits.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.section-provider-inspection.v1";

    // The kernel's classic classes that name a kernel object, an address range or a mapped view, as root\wmi registers them on
    // Windows 10.0.26220, each read at every opcode: the object manager's (ObTrace), whose handle events name an object's type,
    // a section's among them; the memory manager's page fault class, whose events include a virtual allocation's; and the file
    // I/O class, whose events include a view of a file's mapping and unmapping.
    private static readonly (string Name, Guid Class)[] Classes =
    [
        ("Object manager (ObTrace class)", Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7")),
        ("Memory manager (PageFault class)", Guid.Parse("3d6fa8d3-fe05-11d0-9dda-00c04fd7ba7c")),
        ("File I/O (FileIo class)", Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3")),
    ];

    // What a provider's name would say, past the "Microsoft-Windows-" every system provider's begins with, if it could record a
    // section, a mapping or shared memory.
    private static readonly string[] ProviderWords = ["memory", "section", "mapping", "sharedmemory", "vmm"];

    // The prefix of every system provider's name.
    private const string SystemPrefix = "Microsoft-Windows-";

    // What an event's symbol, task or fields would say if it named a section, a mapped view or shared memory.
    private static readonly string[] EventWords = ["section", "mapview", "viewbase", "viewsize", "mapfile", "unmapfile", "filemapping", "sharedmemory", "mapping"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static int Main(string[] args)
    {
        string? output = Option(args, "--output");
        if (!args.Contains("--inspect", StringComparer.Ordinal) || output is null)
        {
            Console.Error.WriteLine("InterCat.SectionProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the layouts this machine registers for the kernel object manager's");
            Console.Error.WriteLine("  and memory manager's classic events, and the schema of every registered provider whose name says");
            Console.Error.WriteLine("  memory, sections or mappings, and writes each event that could name a section or a mapped view.");
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
        foreach ((string name, Guid classGuid) in Classes)
        {
            // Every version a classic class could carry: TDH describes an opcode only at a version the machine registers.
            var events = new List<object>();
            for (int version = 0; version <= 5; version++)
            {
                if (metadata.TryReadClassic(classGuid, name, version, [.. Enumerable.Range(0, 256)]) is not { } schema)
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

        string[] named = [.. TraceEventProviders.GetPublishedProviders()
            .Select(TraceEventProviders.GetProviderName)
            .Where(name => name is not null && ProviderWords.Any(word => (name.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase)
                ? name[SystemPrefix.Length..] : name).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        var providers = new List<object>();
        foreach (string name in named)
        {
            providers.Add(Manifest(metadata, name));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{named.Length} registered providers named for memory, sections or mappings"));
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: each classic class's layouts and each provider's registered schema as TDH gives them, read as the "
                + "product reads one, and the registered providers' names. No provider was enabled and no session started, so "
                + "nothing here says what the kernel or any provider emits.",
            providerWords = ProviderWords,
            eventWords = EventWords,
            publishedProviders = metadata.CountPublishedProviders(),
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

        bool Says(string? text) => text is not null && EventWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        var naming = schema.Events
            .Where(descriptor => Says(descriptor.Symbol) || Says(descriptor.TaskName) || descriptor.Fields.Any(field => Says(field.Name)))
            .OrderBy(static descriptor => descriptor.EventId).ThenBy(static descriptor => descriptor.Version)
            .Select(static descriptor => new
            {
                id = descriptor.EventId,
                version = descriptor.Version,
                symbol = descriptor.Symbol,
                task = descriptor.TaskName,
                opcode = descriptor.OpcodeName,
                keywords = descriptor.Keywords,
                fields = descriptor.Fields.Select(static field => new { name = field.Name, inType = field.InType }),
            })
            .ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {schema.Events.Count} events, {schema.Keywords.Count} keywords, {naming.Length} naming a section, a view or a mapping"));
        return new
        {
            name,
            guid = provider.ProviderGuid,
            registered = true,
            schemaFingerprint = schema.SchemaFingerprint,
            events = schema.Events.Count,
            keywords = schema.Keywords.OrderBy(static keyword => keyword.Value)
                .Select(static keyword => new { name = keyword.Key, mask = "0x" + keyword.Value.ToString("X", CultureInfo.InvariantCulture) }),
            eventsNamingSections = naming,
        };
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
