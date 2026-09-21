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
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress("Reading provider registration and schema metadata. No capture is started.");
        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        CapabilityReport report = probe.Probe(environment);
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

        if (outputPath is not null)
        {
            string fullPath = Path.GetFullPath(outputPath);
            if (File.Exists(fullPath) && !overwrite)
            {
                ConsoleUi.Failure($"{fullPath} already exists. Pass --overwrite to replace it.");
                return InterCatExitCode.InvalidInvocation;
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
            ConsoleUi.Success($"Capability report written to {fullPath}");
        }

        return report.Diagnostics.Count == 0 ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
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
                source.State.ToString(),
                source.Overhead.ToString(),
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
                mechanism.Mechanism.ToString(),
                mechanism.State.ToString(),
                mechanism.Tier.ToString(),
                mechanism.Coverage.ToString(),
                mechanism.Measurement is null ? "none" : mechanism.Measurement.FixtureId,
            ]);
        }

        ConsoleUi.Table(["Mechanism", "State", "Tier", "Coverage", "Fixture"], mechanismRows);
        ConsoleUi.Note("A tier is computed from fixture measurements only. Unsupported here means not yet measured.");

        if (report.Diagnostics.Count > 0)
        {
            ConsoleUi.Heading("Probe diagnostics");
            foreach (string diagnostic in report.Diagnostics)
            {
                ConsoleUi.Bullet(diagnostic);
            }
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
        ConsoleUi.Line();
        ConsoleUi.Line("  Reports what every candidate Windows source could supply, with registration,");
        ConsoleUi.Line("  enablement, observed health and semantic coverage kept as separate checks.");
        ConsoleUi.Line("  The probe never enables a provider and never starts a session.");
    }
}
