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
            500_000,
            true,
            false,
            false,
            "default",
            LoadSeries.Default);
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
            else if (name == "--grace" && TryBounded(value, 0, 60, out int grace))
            {
                options = options with { GraceSeconds = grace, GraceWasGiven = true };
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
            else if (name == "--series" && LoadSeries.Find(value) is { } series)
            {
                options = options with { SeriesName = value, Levels = series };
            }
            else if (name == "--levels" && LoadSeries.Select(value) is { } chosen)
            {
                options = options with { SeriesName = value, Levels = chosen };
            }
            else
            {
                error = $"Unknown option or invalid value: {name} {value}";
                return false;
            }
        }

        if (options.RequestCallStacks && !options.GraceWasGiven)
        {
            // Stack walking slows delivery enough that the default grace truncates a level and reports a
            // coverage collapse that is an artefact of the grace, not of the source. The larger default is
            // recorded in the result like any other capture-affecting setting (section 1.4).
            options = options with { GraceSeconds = StackGraceSeconds };
            Console.Error.WriteLine(
                $"Call stacks were requested, so the reorder grace defaults to {StackGraceSeconds} s. "
                + "Pass --grace to choose your own.");
        }

        return true;
    }

    private static bool TryBoolean(string value, out bool parsed) => bool.TryParse(value, out parsed);

    private static bool TryBounded(string value, int minimum, int maximum, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed >= minimum
        && parsed <= maximum;

    private static void PrintHelp()
    {
        Console.Error.WriteLine("InterCat IC-009 admitted-journal versus diagnostic-ETL load series");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  dotnet run --project tools/InterCat.CaptureComparison -c Release -- [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --output <new-dir>       Immutable result directory (default: timestamped bench/results path)");
        Console.Error.WriteLine("  --workload <exe>         InterCat.TestWorkloads.exe override");
        Console.Error.WriteLine("  --seed <n>               Seed used identically by every run");
        Console.Error.WriteLine("  --series <name>          Declared series to run: default, quick");
        Console.Error.WriteLine("  --levels <a,b,...>       Explicit levels from the default series");
        Console.Error.WriteLine("  --grace <seconds>        Reorder grace after each workload");
        Console.Error.WriteLine("  --record-budget <n>      Bounded replay/coverage record budget");
        Console.Error.WriteLine("  --extended-data <bool>   Copy bounded EVENT_RECORD extended items (default true)");
        Console.Error.WriteLine("  --request-stacks <bool>  Ask ETW for call-stack extended items (default false;");
        Console.Error.WriteLine("                           materially raises per-event cost, so it is a separate series)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Declared levels:");
        foreach (LoadLevel level in LoadSeries.Default)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {level.Name,-12} {level.DeclaredMessages,8:N0} messages, {level.InterMessageDelayMilliseconds,3} ms pacing, "
                + $"queue {level.QueueCapacityRecords,6:N0}, batch {level.JournalBatchRecords,5:N0}"));
            Console.Error.WriteLine($"               {level.Intent}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Requires an elevated Windows shell. Existing output is never overwritten.");
    }

    private sealed record Options(
        string OutputDirectory,
        string? WorkloadPath,
        int Seed,
        int GraceSeconds,
        int RecordBudget,
        bool PreserveExtendedData,
        bool RequestCallStacks,
        bool GraceWasGiven,
        string SeriesName,
        IReadOnlyList<LoadLevel> Levels);
}
