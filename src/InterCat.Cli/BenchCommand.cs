using System.Globalization;
using System.Text.Json;
using InterCat.Benchmarks;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// IC-010's reproducible baseline: the machine a result would be taken on, the section 12 budget table
/// with what has measured each entry, and the per-build validation backlog. It starts no capture and
/// needs no elevation, so the baseline can be published from any machine in the matrix.
/// </summary>
internal static class BenchCommand
{
    private const string Schema = "intercat.baseline.v1";

    /// <summary>
    /// Builds whose fixture corpus has been run, and what ran it. Each entry names its evidence so a
    /// reader can check the claim rather than take it; everything else in the matrix is listed as owing
    /// the corpus (section 13.4).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> MeasuredBuilds =
        new Dictionary<int, string>
        {
            [26220] =
                "FX-TCP-001, FX-PIPE-001, FX-RPC-001 and two five-level IC-009 series were measured on "
                + "2026-09-21. Evidence: fixtures/ and bench/results/.",
        };

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? output = command.TakeOption("--output");
        string? series = command.TakeOption("--series");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool skipStorage = command.TryTakeFlag("--no-storage-probe");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(
                $"Unknown option: {unknown}. Accepted: --output, --series, --overwrite, --json, "
                + "--no-storage-probe.");
            return InterCatExitCode.InvalidInvocation;
        }

        string probeDirectory = output is null
            ? Path.GetFullPath(".")
            : Path.GetDirectoryName(Path.GetFullPath(output)) ?? Path.GetFullPath(".");

        if (!skipStorage)
        {
            ConsoleUi.Progress("Measuring the volume a session would be written to. No capture is started.");
        }

        MachineDescriptor machine = skipStorage
            ? MachineProbe.DescribeWithoutStorage(
                probeDirectory,
                "The storage probe was skipped with --no-storage-probe.")
            : MachineProbe.Describe(probeDirectory);

        var report = new BaselineReport
        {
            Schema = Schema,
            ProducedUtc = DateTimeOffset.UtcNow,
            Machine = machine,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(new TraceEventSessionHost().IsElevated),
            Budgets = PerformanceBudgets.Complete(
                ReadSeriesBudgets(series),
                series is null
                    ? "Nothing in this baseline measures this budget. Pass --series <series.json> from an "
                        + "IC-009 comparison run to fill in the callback stages."
                    : "The named series does not measure this budget. The stages after the journal arrive "
                        + "with IC-011 and M2."),
            BuildBacklog = BuildValidationBacklog.Build(MeasuredBuilds),
            Notes =
            [
                "A budget is a proposed engineering target from section 12, not a measured result.",
                "A budget nothing measured reports as unmeasured. That is an open item, never a pass.",
                "The reference device is defined by sustained throughput, so this measures the volume rather than quoting a model name.",
                "OverheadClass stays Unmeasured until a capture-impact measurement exists; this baseline starts no capture.",
            ],
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonContracts.Indented));
        }
        else
        {
            Print(report);
        }

        if (output is not null)
        {
            string path = Path.GetFullPath(output);
            if (File.Exists(path) && !overwrite)
            {
                ConsoleUi.Failure($"Refusing to replace '{path}'. Pass --overwrite to replace it.");
                return InterCatExitCode.InvalidInvocation;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(report, JsonContracts.Indented),
                cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Baseline written to {path}");
        }

        return report.BudgetsMissed > 0
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    /// <summary>
    /// Reads the budget results an IC-009 series measured, so the baseline and the series agree rather
    /// than each carrying its own half of the table. A series that cannot be read is refused with the
    /// reason; it is never treated as an empty one, which would read as "nothing was measured".
    /// </summary>
    private static IReadOnlyList<BudgetResult> ReadSeriesBudgets(string? seriesPath)
    {
        if (seriesPath is null)
        {
            return [];
        }

        string path = Path.GetFullPath(seriesPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The named series result does not exist.", path);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("budgets", out JsonElement budgets))
        {
            throw new InvalidDataException(
                $"'{path}' has no budgets array. It was produced before IC-010 added one.");
        }

        BudgetResult[] results = budgets.Deserialize<BudgetResult[]>(JsonContracts.Indented)
            ?? throw new InvalidDataException($"'{path}' has an unreadable budgets array.");
        string run = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        return
        [
            .. results
                .Where(result => result.Outcome != BudgetOutcome.Unmeasured)
                .Select(result => result with { Evidence = $"{result.Evidence} Series '{run}'." }),
        ];
    }

    private static void Print(BaselineReport report)
    {
        ConsoleUi.Heading("Machine");
        ConsoleUi.Field("Operating system", report.Machine.OperatingSystem);
        ConsoleUi.Field("Build identity", report.Environment.BuildId);
        ConsoleUi.Field("Support tier", report.Environment.Support.Describe());
        ConsoleUi.Field(
            "Logical processors",
            report.Machine.LogicalProcessors.ToString("N0", CultureInfo.CurrentCulture));
        ConsoleUi.Field(
            "Memory",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{report.Machine.TotalPhysicalMemoryBytes / (1024d * 1024 * 1024):N0} GiB"));
        ConsoleUi.Field("Volume", report.Machine.Storage.Describe());
        ConsoleUi.Field(
            "Reference machine",
            report.Machine.MeetsReferenceMachine ? "met" : "not met");
        foreach (string gap in report.Machine.ReferenceGaps)
        {
            ConsoleUi.Bullet(gap);
        }

        ConsoleUi.Heading("Section 12 budgets");
        var rows = new List<IReadOnlyList<string>>(report.Budgets.Count);
        foreach (BudgetResult result in report.Budgets)
        {
            rows.Add(
            [
                result.Budget.Stage,
                result.Budget.Describe(),
                result.Measured is { } measured
                    ? measured.ToString("N0", CultureInfo.CurrentCulture)
                    : "not measured",
                result.Outcome.ToString(),
            ]);
        }

        ConsoleUi.Table(["Stage", "Budget", "Measured", "Outcome"], rows);
        ConsoleUi.Field(
            "Summary",
            string.Create(
                CultureInfo.CurrentCulture,
                $"{report.BudgetsMet} met, {report.BudgetsMissed} missed, {report.BudgetsUnmeasured} unmeasured"));

        ConsoleUi.Heading("Release build validation backlog");
        var backlog = new List<IReadOnlyList<string>>(report.BuildBacklog.Count);
        foreach (BuildValidationEntry entry in report.BuildBacklog)
        {
            backlog.Add(
            [
                entry.Name,
                entry.BuildNumber.ToString(CultureInfo.InvariantCulture),
                entry.IsPrerelease ? "pre-release" : "retail",
                entry.Tier.ToString(),
                entry.CorpusMeasured ? "measured" : "owes the corpus",
            ]);
        }

        ConsoleUi.Table(["Build", "Number", "Branch", "Tier", "Fixture corpus"], backlog);

        ConsoleUi.Heading("Notes");
        foreach (string note in report.Notes)
        {
            ConsoleUi.Bullet(note);
        }
    }
}
