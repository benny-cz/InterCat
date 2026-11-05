using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.UiMessageProbe;

/// <summary>
/// M6's UI messaging work package (plan §4.1, "WM_COPYDATA/window messages, clipboard, DDE: mechanism-specific research"):
/// what this machine declares that could record a window message one process sends another, the clipboard or DDE. Each is
/// windowing's - win32k's, user32's - so its candidates are the registered providers whose names say windowing, input,
/// messages, the clipboard or DDE. For each it reads the registered schema as the product reads one, and lists the events
/// whose symbol, task or fields name a message, a window, the clipboard or DDE. The inspection is read-only: it enables no
/// provider, starts no session and raises no event, so what it writes is evidence of schemas on one machine, never of what
/// anything emits.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.ui-message-provider-inspection.v1";

    // What a provider's name would say, past the "Microsoft-Windows-" every system provider's begins with, if it could record
    // windowing's messages, input, the clipboard or DDE.
    private static readonly string[] ProviderWords = ["win32k", "user32", "uipi", "clipboard", "dde", "dwm", "input", "message"];

    // The prefix of every system provider's name, which says "window" of all of them.
    private const string SystemPrefix = "Microsoft-Windows-";

    // What an event's symbol, task or fields would say if it named a message between windows, the clipboard or DDE: never a
    // plain "message", which many providers' text fields are.
    private static readonly string[] EventWords = ["wm_", "copydata", "clipboard", "dde", "hwnd", "windowmessage", "sendmessage", "postmessage", "uipi"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static int Main(string[] args)
    {
        string? output = Option(args, "--output");
        if (!args.Contains("--inspect", StringComparer.Ordinal) || output is null)
        {
            Console.Error.WriteLine("InterCat.UiMessageProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the schema of every registered provider whose name says windowing,");
            Console.Error.WriteLine("  input, messages, the clipboard or DDE, and lists their events that name a message, a window, the");
            Console.Error.WriteLine("  clipboard or DDE.");
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

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{named.Length} registered providers named for windowing, input, messages, the clipboard or DDE"));
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: each provider's registered schema as TDH gives it, read as the product reads one, and the registered "
                + "providers' names. No provider was enabled and no session started, so nothing here says what anything emits.",
            providerWords = ProviderWords,
            eventWords = EventWords,
            publishedProviders = metadata.CountPublishedProviders(),
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
            $"{name}: {schema.Events.Count} events, {schema.Keywords.Count} keywords, {naming.Length} naming a message, a window, the clipboard or DDE"));
        return new
        {
            name,
            guid = provider.ProviderGuid,
            registered = true,
            schemaFingerprint = schema.SchemaFingerprint,
            events = schema.Events.Count,
            keywords = schema.Keywords.OrderBy(static keyword => keyword.Value)
                .Select(static keyword => new { name = keyword.Key, mask = "0x" + keyword.Value.ToString("X", CultureInfo.InvariantCulture) }),
            eventsNamingMessages = naming,
        };
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
