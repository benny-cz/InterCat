using System.Globalization;

namespace InterCat.CaptureComparison;

internal static partial class Program
{
    private const int StackGraceSeconds = 6;

    private static bool TryParse(string[] args, out Options options, out string? error)
    {
        string root = RepositoryRoot();
        options = new(
            Path.Combine(
                root,
                "bench",
                "results",
                $"capture-comparison-{DateTime.UtcNow:yyyyMMddTHHmmssZ}"),
            null,
            20_260_920,
            2,
            8,
            4_096,
            2,
            4_096,
            500_000,
            true,
            false,
            false);
        error = null;
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (index + 1 >= args.Length)
            {
                error = $"Option '{name}' requires a value.";
                return false;
            }

            string value = args[++index];
            if (name == "--output")
            {
                options = options with { OutputDirectory = value };
            }
            else if (name == "--workload")
            {
                options = options with { WorkloadPath = value };
            }
            else if (name == "--seed" && TryBounded(value, 1, int.MaxValue, out int seed))
            {
                options = options with { Seed = seed };
            }
            else if (name == "--connections" && TryBounded(value, 1, 1_024, out int connections))
            {
                options = options with { Connections = connections };
            }
            else if (name == "--messages" && TryBounded(value, 1, 100_000, out int messages))
            {
                options = options with { Messages = messages };
            }
            else if (name == "--bytes" && TryBounded(value, 1, 1_048_576, out int maximumBytes))
            {
                options = options with { MaximumBytes = maximumBytes };
            }
            else if (name == "--grace" && TryBounded(value, 0, 60, out int grace))
            {
                options = options with { GraceSeconds = grace, GraceWasGiven = true };
            }
            else if (name == "--batch-records" && TryBounded(value, 1, 100_000, out int batch))
            {
                options = options with { JournalBatchRecords = batch };
            }
            else if (name == "--record-budget" && TryBounded(value, 1, 1_000_000, out int budget))
            {
                options = options with { RecordBudget = budget };
            }
            else if (name == "--extended-data" && TryBoolean(value, out bool extendedData))
            {
                options = options with { PreserveExtendedData = extendedData };
            }
            else if (name == "--request-stacks" && TryBoolean(value, out bool requestStacks))
            {
                options = options with { RequestCallStacks = requestStacks };
            }
            else
            {
                error = $"Unknown option or invalid value: {name} {value}";
                return false;
            }
        }

        if (options.RequestCallStacks && !options.GraceWasGiven)
        {
            // Stack walking slows delivery enough that the default grace truncates the run and reports a
            // coverage collapse that is an artefact of the grace, not of the source. The larger default is
            // recorded in the result like any other capture-affecting setting (section 1.4).
            options = options with { GraceSeconds = StackGraceSeconds };
            Console.Error.WriteLine(
                $"Call stacks were requested, so the reorder grace defaults to {StackGraceSeconds} s. "
                + "Pass --grace to choose your own.");
        }

        return true;
    }

    private static bool TryBoolean(string value, out bool parsed) =>
        bool.TryParse(value, out parsed);

    private static bool TryBounded(string value, int minimum, int maximum, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed >= minimum
        && parsed <= maximum;

    private static void PrintHelp()
    {
        Console.Error.WriteLine("InterCat IC-009 admitted-journal versus diagnostic-ETL comparison");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  dotnet run --project tools/InterCat.CaptureComparison -c Release -- [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --output <new-dir>       Immutable result directory (default: timestamped bench/results path)");
        Console.Error.WriteLine("  --workload <exe>         InterCat.TestWorkloads.exe override");
        Console.Error.WriteLine("  --seed <n>               Seed used identically by both runs");
        Console.Error.WriteLine("  --connections <n>        TCP connections per run");
        Console.Error.WriteLine("  --messages <n>           Messages per connection");
        Console.Error.WriteLine("  --bytes <n>              Maximum message bytes");
        Console.Error.WriteLine("  --grace <seconds>        Reorder grace after each workload");
        Console.Error.WriteLine("  --batch-records <n>      Records per durable journal-probe batch");
        Console.Error.WriteLine("  --record-budget <n>      Bounded replay/coverage record budget");
        Console.Error.WriteLine("  --extended-data <bool>   Copy bounded EVENT_RECORD extended items (default true)");
        Console.Error.WriteLine("  --request-stacks <bool>  Ask ETW for call-stack extended items (default false;");
        Console.Error.WriteLine("                           materially raises per-event cost, so it is a separate load point)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Requires an elevated Windows shell. Existing output is never overwritten.");
    }

    private sealed record Options(
        string OutputDirectory,
        string? WorkloadPath,
        int Seed,
        int Connections,
        int Messages,
        int MaximumBytes,
        int GraceSeconds,
        int JournalBatchRecords,
        int RecordBudget,
        bool PreserveExtendedData,
        bool RequestCallStacks,
        bool GraceWasGiven);
}
