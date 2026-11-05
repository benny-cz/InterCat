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

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        bool inspect = args.Contains("--inspect", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        if (inspect == measure || output is null)
        {
            Console.Error.WriteLine("InterCat.UiMessageProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the schema of every registered provider whose name says windowing,");
            Console.Error.WriteLine("  input, messages, the clipboard or DDE, and lists their events that name a message, a window, the");
            Console.Error.WriteLine("  clipboard or DDE.");
            Console.Error.WriteLine("InterCat.UiMessageProbe --measure --output <new directory> [--copies n] [--posts n] [--bytes n]");
            Console.Error.WriteLine("  [--workload <InterCat.TestWorkloads.exe>]: runs FX-WMSG-001 under an owned session enabling");
            Console.Error.WriteLine("  Microsoft-Windows-Win32k at its message pump keyword, machine-wide, and writes what it recorded of the");
            Console.Error.WriteLine("  messages the sender sent and posted to the receiver's window.");
            Console.Error.WriteLine("  --impact [--pairs n] [--copies n] [--posts n]: machine CPU and workload time with and without a session");
            Console.Error.WriteLine("  collecting the message pump keyword machine-wide, in pairs, over FX-WMSG-001's sender sending and");
            Console.Error.WriteLine("  posting that many messages, and the processor time of the workload's processes and of the probe's");
            Console.Error.WriteLine("  own consumer.");
            Console.Error.WriteLine("  --order [--copies n] [--posts n] [--bytes n] [--ledger <csv>]: whether a session with the product's buffer");
            Console.Error.WriteLine("  size delivers each of FX-WMSG-001's messages' ends in the order they were raised, and what a gate");
            Console.Error.WriteLine("  following a message by its pointer as records arrive would number wrongly; the ledger is the run's ends");
            Console.Error.WriteLine("  as delivered, each pointer by the order it first appeared in.");
            return 2;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        Directory.CreateDirectory(output);
        if (measure)
        {
            string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));

            // What collecting the message pump costs, in pairs with and without a session collecting it machine-wide, over
            // FX-WMSG-001's sender sending and posting thousands of messages.
            if (args.Contains("--impact", StringComparer.Ordinal))
            {
                int pairs = int.Parse(Option(args, "--pairs") ?? "9", CultureInfo.InvariantCulture);
                string copies = Option(args, "--copies") ?? "20000";
                string posts = Option(args, "--posts") ?? "20000";
                Dictionary<string, object?> impact = await Impact.MeasureAsync(workload,
                    ["window-message", "--truth", "{truth}", "--copies", copies, "--posts", posts, "--bytes", "64"], pairs).ConfigureAwait(false);
                impact["schema"] = "intercat.window-message-impact.v1";
                impact["measuredUtc"] = DateTimeOffset.UtcNow;
                impact["build"] = CapabilityInventoryProbe.DescribeEnvironment().BuildId;
                impact["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
                File.WriteAllText(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
                Console.WriteLine(JsonSerializer.Serialize(impact, Json));
                return 0;
            }

            // Whether a session delivers each message's ends in the order they were raised, as a gate deciding records as they
            // arrive would meet them.
            if (args.Contains("--order", StringComparer.Ordinal))
            {
                Dictionary<string, object?> ordered = await Order.RunAsync(workload, output,
                    int.Parse(Option(args, "--copies") ?? "32", CultureInfo.InvariantCulture),
                    int.Parse(Option(args, "--posts") ?? "32", CultureInfo.InvariantCulture),
                    int.Parse(Option(args, "--bytes") ?? "2048", CultureInfo.InvariantCulture), Option(args, "--ledger")).ConfigureAwait(false);
                ordered["schema"] = "intercat.window-message-order.v1";
                ordered["measuredUtc"] = DateTimeOffset.UtcNow;
                ordered["build"] = CapabilityInventoryProbe.DescribeEnvironment().BuildId;
                ordered["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(ordered, Json));
                Console.WriteLine(JsonSerializer.Serialize(ordered, Json));
                return 0;
            }

            Dictionary<string, object?> measured = await Measure.RunAsync(workload, output,
                int.Parse(Option(args, "--copies") ?? "32", CultureInfo.InvariantCulture),
                int.Parse(Option(args, "--posts") ?? "32", CultureInfo.InvariantCulture),
                int.Parse(Option(args, "--bytes") ?? "2048", CultureInfo.InvariantCulture), Option(args, "--dump")).ConfigureAwait(false);
            measured["schema"] = "intercat.ui-message-measurement.v1";
            measured["measuredUtc"] = DateTimeOffset.UtcNow;
            measured["build"] = CapabilityInventoryProbe.DescribeEnvironment().BuildId;
            measured["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(measured, Json));
            Console.WriteLine(JsonSerializer.Serialize(measured, Json));
            return 0;
        }

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
