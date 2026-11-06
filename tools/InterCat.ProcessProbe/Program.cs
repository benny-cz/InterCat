using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ProcessProbe;

/// <summary>
/// M6's process enrichment (plan M6: "enrich process nodes with package, service and user-session information as
/// accessible"): what this machine declares that could say which package a process runs in, which services it hosts and
/// which user and session it runs as, read from TDH without enabling any. Microsoft-Windows-Kernel-Process, whose process
/// start, stop and rundown every capture already collects, at every event and version it registers, with each field; and
/// the registered schema of every manifest provider whose name says services, packages, the application model, sessions
/// or logons, with its events that name a process beside a package, an application, a service, a session or a user. It is
/// read-only: it enables no provider, starts no session and raises no event, so what it writes is evidence of schemas on
/// one machine, never of what any provider emits.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.process-provider-inspection.v1";
    private const string KernelProcess = "Microsoft-Windows-Kernel-Process";

    // The prefix of every system provider's name.
    private const string SystemPrefix = "Microsoft-Windows-";

    // What a provider's name would say, past that prefix, if it could say which package a process runs in, which services it
    // hosts or which user and session it runs as.
    private static readonly string[] ProviderWords = ["service", "appmodel", "appx", "package", "session", "logon", "lsm", "userprofile"];

    // What a field's name would say if it named a process, and if it named a package, an application, a service, a session or
    // a user.
    private static readonly string[] ProcessWords = ["processid", "pid"];
    private static readonly string[] EnrichmentWords = ["package", "appid", "applicationid", "service", "session", "sid", "user", "token", "elevat", "mandatorylabel", "integrity"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        bool inspect = args.Contains("--inspect", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        if (inspect == measure || output is null)
        {
            Console.Error.WriteLine("InterCat.ProcessProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the schema this machine registers for Microsoft-Windows-Kernel-Process -");
            Console.Error.WriteLine("  every event at every version, with its fields - and for every registered provider whose name says");
            Console.Error.WriteLine("  services, packages, the application model, sessions or logons, and writes each of its events that");
            Console.Error.WriteLine("  names a process beside a package, an application, a service, a session or a user.");
            Console.Error.WriteLine("InterCat.ProcessProbe --measure --output <new directory> [--workload <InterCat.TestWorkloads.exe>]");
            Console.Error.WriteLine("  Runs FX-PROC-001 under a real-time session of its own collecting Kernel-Process's process keyword, and");
            Console.Error.WriteLine("  writes each child's process start against what the workload read of it before it ran, and the");
            Console.Error.WriteLine("  session's process rundown against the package the application model gives each process it names.");
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
            return await Measure.RunAsync(output, workload).ConfigureAwait(false);
        }
        var metadata = new TdhEtwMetadataSource();
        object kernelProcess = Manifest(metadata, KernelProcess, every: true);
        string[] named = [.. TraceEventProviders.GetPublishedProviders()
            .Select(TraceEventProviders.GetProviderName)
            .Where(name => name is not null && !string.Equals(name, KernelProcess, StringComparison.Ordinal)
                && ProviderWords.Any(word => Tail(name).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        var providers = new List<object>();
        foreach (string name in named)
        {
            providers.Add(Manifest(metadata, name, every: false));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{named.Length} registered providers named for services, packages, the application model, sessions or logons"));
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: each provider's registered schema as TDH gives it, read as the product reads one, and the "
                + "registered providers' names. No provider was enabled and no session started, so nothing here says what any "
                + "provider emits.",
            providerWords = ProviderWords,
            processWords = ProcessWords,
            enrichmentWords = EnrichmentWords,
            publishedProviders = metadata.CountPublishedProviders(),
            kernelProcess,
            providers,
        }, Json));
        return 0;
    }

    // A provider's registered schema: every event with its fields where every is asked, otherwise only its events naming a
    // process beside a package, an application, a service, a session or a user.
    private static object Manifest(TdhEtwMetadataSource metadata, string name, bool every)
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

        static bool Says(string? text, string[] words) => text is not null && words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        var events = schema.Events
            .Where(descriptor => every || (descriptor.Fields.Any(field => Says(field.Name, ProcessWords))
                && descriptor.Fields.Any(field => Says(field.Name, EnrichmentWords))))
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
                enrichmentFields = descriptor.Fields.Where(static field => Says(field.Name, EnrichmentWords)).Select(static field => field.Name),
            })
            .ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {schema.Events.Count} events, {events.Length} {(every ? "listed" : "naming a process beside a package, an application, a service, a session or a user")}"));
        return new
        {
            name,
            guid = provider.ProviderGuid,
            registered = true,
            schemaFingerprint = schema.SchemaFingerprint,
            events = schema.Events.Count,
            keywords = schema.Keywords.OrderBy(static keyword => keyword.Value)
                .Select(static keyword => new { name = keyword.Key, mask = "0x" + keyword.Value.ToString("X", CultureInfo.InvariantCulture) }),
            listed = events,
        };
    }

    private static string Tail(string name) => name.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase) ? name[SystemPrefix.Length..] : name;

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
