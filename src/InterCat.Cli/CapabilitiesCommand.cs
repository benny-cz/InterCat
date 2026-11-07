using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// `icat capabilities`: a read-only inventory of what each candidate source could supply on this machine,
/// with the checks it did not attempt stated as such (IC-002, section 21.2 item 1).
/// </summary>
internal static class CapabilitiesCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? outputPath = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        bool catalog = command.TryTakeFlag("--catalog");
        string? find = command.TakeOption("--find");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        // The catalog is InterCat's own, read from no machine, so it is printed rather than written as a machine's report; a
        // search is of the catalog.
        if (catalog || find is not null)
        {
            if (outputPath is not null)
            {
                ConsoleUi.Failure("--output writes this machine's capability report, and the catalog is InterCat's own, read from "
                    + "no machine: leave out --catalog and --find to probe this machine.");
                ConsoleUi.Explain(PrintHelp);
                return InterCatExitCode.InvalidInvocation;
            }

            Catalog(find, json);
            return InterCatExitCode.Success;
        }

        // A report that exists is refused before anything is read, as every command refuses it.
        string? fullPath = outputPath is null ? null : Path.GetFullPath(outputPath);
        if (OutputFiles.Refusal(fullPath, overwrite) is { } refusedOutput)
        {
            ConsoleUi.Failure(refusedOutput);
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress("Reading provider registration and schema metadata. No capture is started.");
        CapabilityReport report = Probe();
        cancellationToken.ThrowIfCancellationRequested();

        string payload = JsonSerializer.Serialize(report, JsonContracts.Indented);
        if (json)
        {
            Console.Out.WriteLine(payload);
        }
        else
        {
            Render(report);
        }

        if (fullPath is not null)
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Capability report written to {fullPath}.");
        }

        return report.Diagnostics.Count == 0 ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>This machine's capability report: what each candidate source could supply, read without starting a capture.</summary>
    internal static CapabilityReport Probe()
    {
        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        return new CapabilityInventoryProbe(new TdhEtwMetadataSource()).Probe(environment);
    }

    public static void Render(CapabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        ConsoleUi.Heading("Environment");
        ConsoleUi.Field("Operating system", report.Environment.OperatingSystem);
        ConsoleUi.Field("Build identity", report.Environment.BuildId);
        ConsoleUi.Field(
            "Support tier",
            report.Environment.IsSupportedBuild
                ? report.Environment.Support.Describe()
                : "untested build: every result below is evidence on this machine only");
        ConsoleUi.Field("Elevated", report.Environment.IsElevated ? "yes" : "no");
        ConsoleUi.Field("Adapter", report.AdapterVersion);
        ConsoleUi.Field("Capture started", report.ProbeOnly ? "no, this is a read-only probe" : "yes");

        ConsoleUi.Heading("Sources");
        var sourceRows = new List<IReadOnlyList<string>>(report.Sources.Count);
        foreach (SourceCapability source in report.Sources)
        {
            sourceRows.Add(
            [
                source.SourceId,
                CapabilityText.State(source.State),
                CapabilityText.Overhead(source.Overhead),
                DescribeChecks(source),
                source.Events.Count.ToString(CultureInfo.CurrentCulture),
            ]);
        }

        ConsoleUi.Table(
            ["Source", "State", "Overhead", "Registration/Enable/Health/Semantics", "Admitted descriptors"],
            sourceRows);

        foreach (SourceCapability source in report.Sources)
        {
            if (source.UnavailableReason is not null)
            {
                ConsoleUi.Note($"{source.SourceId}: {source.UnavailableReason}");
            }

            if (source.OverheadEvidence is not null)
            {
                ConsoleUi.Note($"{source.SourceId}: overhead evidence {source.OverheadEvidence}");
            }
        }

        ConsoleUi.Heading("Mechanisms");
        var mechanismRows = new List<IReadOnlyList<string>>(report.Mechanisms.Count);
        foreach (MechanismCapability mechanism in report.Mechanisms)
        {
            mechanismRows.Add(
            [
                MechanismText.Name(mechanism.Mechanism),
                CapabilityText.State(mechanism.State),
                CapabilityText.Tier(mechanism.Tier),
                CoverageStateText.Value(mechanism.Coverage),
                mechanism.Measurement is null ? "none" : mechanism.Measurement.FixtureId,
            ]);
        }

        ConsoleUi.Table(["Mechanism", "State", "Tier", "Coverage", "Fixture"], mechanismRows);
        ConsoleUi.Note("A tier is computed from fixture measurements only; unsupported here means not yet measured.");
        ConsoleUi.Note("icat capabilities --catalog explains every mechanism, a source or not, and --find <text> searches it.");

        if (report.Diagnostics.Count > 0)
        {
            ConsoleUi.Heading("Probe diagnostics");
            foreach (string diagnostic in report.Diagnostics)
            {
                ConsoleUi.Bullet(diagnostic);
            }
        }
    }

    /// <summary>
    /// The capability catalog: every mechanism InterCat names, the sources that record it, what fixtures measured on each
    /// build and why it stands where it does - read from no machine. <paramref name="find"/> keeps the mechanisms whose words
    /// hold it, and says how many did.
    /// </summary>
    private static void Catalog(string? find, bool json)
    {
        IReadOnlyList<MechanismCatalogEntry> entries = MechanismCatalog.Find(find);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new CatalogDocument { Contract = CatalogContract, Find = find, Mechanisms = MechanismCatalog.Entries.Count, Entries = entries },
                JsonContracts.Indented));
            return;
        }

        ConsoleUi.Heading("Capability catalog");
        ConsoleUi.Note("Every mechanism InterCat names, as this InterCat knows it, read from no machine: icat capabilities "
            + "without --catalog probes this one.");
        if (find is not null)
        {
            ConsoleUi.Note(entries.Count == 0
                ? $"No mechanism's words hold \"{find}\"."
                : string.Create(CultureInfo.CurrentCulture, $"{entries.Count} of {MechanismCatalog.Entries.Count} mechanisms hold \"{find}\"."));
        }

        foreach (MechanismCatalogEntry entry in entries)
        {
            ConsoleUi.Line();
            ConsoleUi.Line(entry.Name);
            ConsoleUi.Field("Sources", entry.SourceIds.Count > 0 ? string.Join(", ", entry.SourceIds) : "none in the catalog");
            if (entry.UnrequestedSourceIds.Count > 0)
            {
                ConsoleUi.Field("Requested by no profile", string.Join(", ", entry.UnrequestedSourceIds));
            }

            ConsoleUi.Field("Measured", entry.Measured.Count == 0
                ? "no fixture has measured it"
                : string.Join("; ", entry.Measured.Select(measured =>
                    $"{CapabilityText.Tier(measured.Tier)} on {measured.Build} by {measured.Fixture}, {measured.Date}")));
            ConsoleUi.Field("Why", entry.Explanation);
        }
    }

    private static string DescribeChecks(SourceCapability source)
    {
        string registration = Symbol(source.OutcomeOf(CapabilityCheckKind.Registration));
        string enablement = Symbol(source.OutcomeOf(CapabilityCheckKind.Enablement));
        string health = Symbol(source.OutcomeOf(CapabilityCheckKind.ObservedHealth));
        string semantics = Symbol(source.OutcomeOf(CapabilityCheckKind.SemanticCoverage));
        return $"{registration} / {enablement} / {health} / {semantics}";
    }

    private static string Symbol(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Passed => "pass",
        CheckOutcome.Failed => "fail",
        CheckOutcome.Inconclusive => "open",
        _ => "----",
    };

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat capabilities [--output <path>] [--overwrite] [--json]");
        ConsoleUi.Line("icat capabilities --catalog [--find <text>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Reports what every candidate Windows source could supply, with registration,");
        ConsoleUi.Line("  enablement, observed health and semantic coverage kept as separate checks.");
        ConsoleUi.Line("  The probe never enables a provider and never starts a session.");
        ConsoleUi.Line();
        ConsoleUi.Line("  --catalog explains every mechanism InterCat names instead, read from no machine:");
        ConsoleUi.Line("  the sources that record it, the tier fixtures measured on each build, and why it");
        ConsoleUi.Line("  stands where it does - an unsupported one included. --find <text> keeps the");
        ConsoleUi.Line("  mechanisms whose name, sources, fixtures or explanation hold the text.");
    }

    /// <summary>The contract the catalog's document is written under (`contracts/capability-report-v1.md` §The catalog).</summary>
    internal const string CatalogContract = "capability-catalog-v1";

    /// <summary>The catalog as `--json` writes it: the search asked for, how many mechanisms it has, and those kept.</summary>
    internal sealed record CatalogDocument
    {
        public required string Contract { get; init; }

        public required string? Find { get; init; }

        public required int Mechanisms { get; init; }

        public required IReadOnlyList<MechanismCatalogEntry> Entries { get; init; }
    }
}
