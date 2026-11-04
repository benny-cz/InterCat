using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;

namespace InterCat.ComProbe;

/// <summary>
/// M6's COM/DCOM/WinRT work package (plan §4.2, "RPC plus validated component-specific activation/metadata providers"):
/// which providers this machine registers for COM, DCOM and WinRT, and what each one's registered schema declares - its
/// keywords and every event's task, opcode, level, keywords and fields - read as the product's inventory reads a
/// provider's. It is read-only: it enables no provider, starts no session and raises no event, so what it writes is
/// evidence of schemas on one machine, never of what a provider emits.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.com-provider-inspection.v1";

    // The registered providers whose names say COM, DCOM, OLE, COM+, RPCSS or WinRT on Windows 10.0.26220, as logman lists
    // them; Microsoft-Windows-RPC, which the product admits already, is inspected beside them.
    private static readonly string[] Candidates =
    [
        "Microsoft-Windows-COM",
        "Microsoft-Windows-COMRuntime",
        "Microsoft-Windows-COM-Perf",
        "Microsoft-Windows-COM-RundownInstrumentation",
        "Microsoft-Windows-Complus",
        "Microsoft-Windows-DistributedCOM",
        "Microsoft-Windows-OLE-Perf",
        "Microsoft-Windows-RPCSS",
        "Microsoft-Windows-WinRT-Error",
        "Microsoft-Windows-RPC",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        // A session icat record recorded is read, not captured: the ledger of its workload's COM calls as the product
        // derives them, evaluated against the workload's truth.
        if (Option(args, "--session") is { } recorded)
        {
            return SessionLedger.Write(recorded, Option(args, "--truth"), Option(args, "--out"));
        }

        string? output = Option(args, "--output");
        bool inspect = args.Contains("--inspect", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        if (inspect == measure || output is null)
        {
            Console.Error.WriteLine("InterCat.ComProbe --inspect --output <new directory>");
            Console.Error.WriteLine("  Reads, without enabling any, the registered schema of each provider this machine has for COM,");
            Console.Error.WriteLine("  DCOM, OLE, COM+, RPCSS and WinRT, and writes each one's keywords and events with their fields.");
            Console.Error.WriteLine("InterCat.ComProbe --measure --output <new directory> [--calls n] [--delay ms] [--ledger]");
            Console.Error.WriteLine("  [--workload <InterCat.TestWorkloads.exe>]: runs FX-COM-001 under a session of its own reading");
            Console.Error.WriteLine("  Microsoft-Windows-COM-Perf machine-wide, and writes counters of what it recorded of the workload's");
            Console.Error.WriteLine("  calls, and with --ledger each of its records' metadata, never a pointer.");
            Console.Error.WriteLine("  --filter scopes the session to FX-COM-001's two processes beside a decoy pair outside it;");
            Console.Error.WriteLine("  --in-process runs FX-COM-002, one process's calls across an apartment, never a process.");
            Console.Error.WriteLine("  --layering runs FX-COM-001 with Microsoft-Windows-RPC collected beside COM-Perf, as the product's");
            Console.Error.WriteLine("  profiles request it, and sets each COM call beside the RPC calls of its process and thread.");
            Console.Error.WriteLine("  --impact [--pairs n] [--calls n]: machine CPU and workload time with and without a session");
            Console.Error.WriteLine("  collecting COM-Perf machine-wide, in pairs, over FX-COM-001's exchange unpaced.");
            Console.Error.WriteLine("InterCat.ComProbe --session <recorded session> --truth <its workload's truth> --out <calls.csv>");
            Console.Error.WriteLine("  Derives the COM calls of a session icat record recorded under the COM calls profile, as the");
            Console.Error.WriteLine("  product does, writes the ledger of the workload's own by role, never by PID, and evaluates it.");
            return 2;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        Directory.CreateDirectory(output);

        // What collecting COM-Perf costs, in pairs with and without a session collecting it machine-wide, over FX-COM-001's
        // exchange unpaced.
        if (measure && args.Contains("--impact", StringComparer.Ordinal))
        {
            string workload = Measure.Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
            int pairs = int.Parse(Measure.Option(args, "--pairs") ?? "9", CultureInfo.InvariantCulture);
            string calls = Measure.Option(args, "--calls") ?? "16384";
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload,
                ["com-loopback", "--truth", "{truth}", "--calls", calls, "--delay", "0"], pairs);
            File.WriteAllText(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        return !measure ? Inspect(output)
            : args.Contains("--layering", StringComparer.Ordinal) ? await Layering.RunAsync(output, args)
            : args.Contains("--filter", StringComparer.Ordinal) || args.Contains("--in-process", StringComparer.Ordinal)
                ? await Scope.RunAsync(output, args)
                : await Measure.RunAsync(output, args);
    }

    private static int Inspect(string output)
    {
        var metadata = new TdhEtwMetadataSource();
        var providers = new List<object>();
        foreach (string name in Candidates)
        {
            RegisteredProvider? provider = metadata.TryResolveProvider(name);
            if (provider is null)
            {
                providers.Add(new { name, registered = false });
                Console.WriteLine($"{name}: not registered");
                continue;
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
                providers.Add(new { name, guid = provider.ProviderGuid, registered = true, schemaProblem = problem });
                Console.WriteLine($"{name}: registered as {provider.ProviderGuid:B}; no schema read: {problem}");
                continue;
            }

            providers.Add(new
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
                        keywordMask = Hex(descriptor.KeywordMask),
                        fields = descriptor.Fields.Select(static field => new { name = field.Name, inType = field.InType, outType = field.OutType }),
                    }),
            });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name}: registered as {provider.ProviderGuid:B}; {schema.Events.Count} events, {schema.Keywords.Count} keywords"));
        }

        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            schema = Schema,
            inspectedUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            note = "Read-only: each provider's registered schema as TDH gives it, read as the product's inventory reads one. No "
                + "provider was enabled and no session started, so nothing here says what a provider emits.",
            providers,
        }, Json));
        return 0;
    }

    private static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
