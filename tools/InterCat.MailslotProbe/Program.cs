using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.MailslotProbe;

/// <summary>
/// M6's mailslots work package (plan §4.1, "mailslots: mechanism-specific research"): what this machine declares that could
/// record a mailslot. A mailslot is a file of the mailslot file system, made by its server and opened, written and closed by
/// its clients as a file is, so its candidates are file I/O's: the kernel's classic file I/O class, a flag group only a system
/// logger receives (ADR-035), whose every event's layout it reads from TDH as the product reads ALPC's, and the registered
/// schema of Microsoft-Windows-Kernel-File, which ADR-003 measured recording no named pipe; and any registered provider whose
/// name says it records mailslots. The inspection is read-only: it enables no provider, starts no session and raises no
/// event, so what it writes is evidence of schemas on one machine, never of what the kernel emits.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.mailslot-provider-inspection.v1";

    // The kernel's classic class of file I/O, as root\wmi registers it: its every opcode.
    private static readonly (string Name, Guid Class, int[] Opcodes)[] Classes =
    [
        ("File I/O (FileIo class)", Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3"), [.. Enumerable.Range(0, 256)]),
    ];

    // The registered manifest provider of file operations, which records ordinary files' and, measured, no named pipe's.
    private static readonly string[] Providers = ["Microsoft-Windows-Kernel-File"];

    // What a provider's name would say if it recorded mailslots or the mailslot file system.
    private static readonly string[] MailslotWords = ["mailslot", "msfs"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static int Main(string[] args)
    {
        string? output = Option(args, "--output");
        if (!args.Contains("--inspect", StringComparer.Ordinal) || output is null)
        {
            Console.Error.WriteLine("InterCat.MailslotProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the layouts this machine registers for the kernel's classic file I/O");
            Console.Error.WriteLine("  class and the schema of Microsoft-Windows-Kernel-File, and lists every registered provider whose");
            Console.Error.WriteLine("  name says it records mailslots.");
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

                events.AddRange(schema.Events.OrderBy(static descriptor => descriptor.OpcodeValue).Select(static descriptor => new
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

        // Every registered provider whose name says mailslot or the mailslot file system.
        string[] named = [.. TraceEventProviders.GetPublishedProviders()
            .Select(TraceEventProviders.GetProviderName)
            .Where(name => name is not null && MailslotWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{named.Length} registered providers named for mailslots"));

        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: the classic class's layouts and the provider's registered schema as TDH gives them, read as the "
                + "product reads one, and the registered providers' names. No provider was enabled and no session started, so "
                + "nothing here says what the kernel emits.",
            classes,
            providers,
            publishedProviders = metadata.CountPublishedProviders(),
            providersNamedForMailslots = named,
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
                .Select(static keyword => new { name = keyword.Key, mask = "0x" + keyword.Value.ToString("X", CultureInfo.InvariantCulture) }),
            events = schema.Events.OrderBy(static descriptor => descriptor.EventId).ThenBy(static descriptor => descriptor.Version)
                .Select(static descriptor => new
                {
                    id = descriptor.EventId,
                    version = descriptor.Version,
                    symbol = descriptor.Symbol,
                    task = descriptor.TaskName,
                    opcode = descriptor.OpcodeName,
                    fields = descriptor.Fields.Select(static field => new { name = field.Name, inType = field.InType }),
                }),
            namesMailslots = schema.Events.Any(descriptor => MailslotWords.Any(word =>
                (descriptor.Symbol ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase)
                || (descriptor.TaskName ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase)))
                || schema.Keywords.Keys.Any(keyword => MailslotWords.Any(word => keyword.Contains(word, StringComparison.OrdinalIgnoreCase))),
        };
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
