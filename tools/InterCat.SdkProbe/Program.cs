using System.Globalization;
using System.Text.Json;

namespace InterCat.SdkProbe;

/// <summary>
/// M7's measured fixture (ADR-104): FX-SDK-001's ring of one producer and its readers, each saying what it publishes and
/// consumes through InterCat's cooperative SDK, recorded through the product's own <c>icat record</c> under the opt-in sdk
/// profile, and each run's session read as the product reads one - its logical messages derived as <c>icat messages</c>
/// derives them - and held to the run's truth. Each run's session is deleted once read, as it holds the machine's process
/// names; what is kept is each run's counts, the first run's truth logs and scenario, and the capture's own counters.
/// With <c>--native</c> it records FX-SDK-002 instead (ADR-105): the native sample, saying the same through the SDK's C
/// library, its operations held to their records too. With <c>--transport pipe</c> or <c>socket</c> it records FX-SDK-003
/// or FX-SDK-004: a client's framed messages to a server over a named pipe or a loopback TCP connection, its operations and
/// the bytes each message's frame copied held to their records as well.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        if (output is null || Directory.Exists(output))
        {
            Console.Error.WriteLine("InterCat.SdkProbe --output <new directory> [--runs n] [--duration s] [--readers n] [--icat <InterCat.Cli.exe>]");
            Console.Error.WriteLine("  [--workload <InterCat.TestWorkloads.exe>]: records FX-SDK-001 through the product's own icat record under");
            Console.Error.WriteLine("  the opt-in sdk profile, each run's session read as icat messages reads one and held to its truth, then");
            Console.Error.WriteLine("  deleted. Needs elevation, and Release builds of InterCat.Cli and InterCat.TestWorkloads.");
            Console.Error.WriteLine("  [--native <publish_consume.exe>]: records FX-SDK-002 instead, the native sample said through intercat_sdk.h.");
            Console.Error.WriteLine("  [--transport pipe|socket]: records FX-SDK-003 or FX-SDK-004 instead, a client's framed messages to a server");
            Console.Error.WriteLine("  over a named pipe or a loopback TCP connection.");
            Console.Error.WriteLine("  [--batch n]: the ring's messages said in batches of n, FX-SDK-005, or with --native the sample's in batches of");
            Console.Error.WriteLine("  ten, FX-SDK-006.");
            Console.Error.WriteLine("InterCat.SdkProbe --overhead --output <new directory> [--rounds n] [--messages n] [--duration s]: the SDK's cost,");
            Console.Error.WriteLine("  each round a burst with no session listening and one while icat record --profile sdk records it; with");
            Console.Error.WriteLine("  --batch n each call says a batch of n messages, the messages a whole number of batches.");
            Console.Error.WriteLine("InterCat.SdkProbe --content-impact --output <new directory> [--pairs n] [--messages n]: what keeping the");
            Console.Error.WriteLine("  SDK's content costs the machine, bursts publishing with their bytes in pairs with and without a session");
            Console.Error.WriteLine("  scoped to the burst asking for content, then a decoy beside a named burst; writes impact.json.");
            return 2;
        }

        string root = RepositoryRoot();
        string icat = Option(args, "--icat") ?? Path.Combine(root, "src", "InterCat.Cli", "bin", "Release", "net10.0", "InterCat.Cli.exe");
        string workload = Option(args, "--workload")
            ?? Path.Combine(root, "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe");
        int runs = Integer(args, "--runs") ?? 3;
        int duration = Integer(args, "--duration") ?? 40;
        int readers = Integer(args, "--readers") ?? 3;
        Directory.CreateDirectory(output);
        if (args.Contains("--content-impact", StringComparer.Ordinal))
        {
            Dictionary<string, object?> impact = await ContentImpact.MeasureAsync(workload, Integer(args, "--messages") ?? 100_000,
                Integer(args, "--pairs") ?? 7).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json)).ConfigureAwait(false);
            Console.WriteLine($"What keeping the SDK's content costs measured into {output}.");
            return 0;
        }

        if (args.Contains("--overhead", StringComparer.Ordinal))
        {
            Dictionary<string, object?> overhead = await Overhead.RunAsync(icat, workload, output, Integer(args, "--rounds") ?? 5,
                Integer(args, "--messages") ?? 200_000, duration, Integer(args, "--batch") ?? 1).ConfigureAwait(false);
            await Overhead.WriteAsync(output, overhead).ConfigureAwait(false);
            Console.WriteLine($"The SDK's overhead measured into {output}.");
            return 0;
        }

        string? native = Option(args, "--native");
        string? transport = Option(args, "--transport");
        int batch = Integer(args, "--batch") ?? 1;
        if (transport is not (null or "pipe" or "socket") || (native is not null && transport is not null) || batch < 1
            || (transport is not null && batch > 1))
        {
            Console.Error.WriteLine("--transport is pipe or socket, a run records the native sample or a stream, never both, and --batch, "
                + "at least one, is the ring's or the native sample's.");
            return 2;
        }

        Dictionary<string, object?> measurement = await Product.RunAsync(icat, workload, output, runs, duration, readers, native, transport, batch)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "measurement.json"), JsonSerializer.Serialize(measurement, Json)).ConfigureAwait(false);
        Console.WriteLine($"{measurement["fixture"]} measured through the product into {output}.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int? Integer(string[] args, string name) =>
        Option(args, name) is { } value ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : null;

    private static string RepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("The probe runs from inside the repository.");
    }
}
