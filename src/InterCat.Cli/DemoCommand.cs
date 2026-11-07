using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>What `icat demo --json` answers: where the demo was written, and what it is.</summary>
internal sealed record DemoDocument
{
    public required string Contract { get; init; }
    public required string Directory { get; init; }
    public required string Investigation { get; init; }
    public required IReadOnlyList<string> Sessions { get; init; }
    public required string SourceIdentity { get; init; }
    public required string Disclosure { get; init; }
}

/// <summary>
/// `icat demo`: writes InterCat's generated demo investigation (<see cref="DemoInvestigation"/>) into a new folder, so a
/// person can try every view of an investigation of two hosts without capturing anything. It says what it is first.
/// </summary>
internal static class DemoCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return Task.FromResult(InterCatExitCode.Success);
        }

        bool json = command.TryTakeFlag("--json");
        bool discovery = command.TryTakeFlag("--discovery");
        string? directory = command.TakePositional();
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        if (directory is null)
        {
            ConsoleUi.Failure("Say where the demo goes: a new or empty folder.");
            ConsoleUi.Explain(PrintHelp);
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        string full = Path.GetFullPath(directory);
        if (File.Exists(full) || (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()))
        {
            ConsoleUi.Failure($"{full} already holds something; the demo is written only into a new or empty folder.");
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        if (OutputFiles.FolderRefusal(full, "the demo is written into a new or empty folder") is { } blocked)
        {
            ConsoleUi.Failure(blocked);
            return Task.FromResult(InterCatExitCode.InvalidInvocation);
        }

        if (discovery)
        {
            return Task.FromResult(Discovery(full, json, cancellationToken));
        }

        ConsoleUi.Progress(DemoInvestigation.Disclosure + " Writing its two sessions and the investigation that holds them.");
        DemoInvestigationResult made = DemoInvestigation.Create(full, cancellationToken);
        var document = new DemoDocument
        {
            Contract = DemoInvestigation.SourceIdentity,
            Directory = made.Directory,
            Investigation = made.WorkspacePath,
            Sessions = [made.ClientSession, made.ServerSession],
            SourceIdentity = DemoInvestigation.SourceIdentity,
            Disclosure = DemoInvestigation.Disclosure,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            ConsoleUi.Heading("Demo investigation");
            ConsoleUi.Field("Investigation", made.WorkspacePath);
            ConsoleUi.Field("Demo client", made.ClientSession);
            ConsoleUi.Field("Demo server", made.ServerSession);
            ConsoleUi.Note(DemoInvestigation.Disclosure);
            ConsoleUi.Note($"Open it with: icat workspace show \"{made.WorkspacePath}\" (or correlate, timeline), or in the "
                + "window's Investigation menu.");
        }

        return Task.FromResult(InterCatExitCode.Success);
    }

    // The discovery demo: one generated host whose processes share named objects and sections, which its views find.
    private static InterCatExitCode Discovery(string full, bool json, CancellationToken cancellationToken)
    {
        ConsoleUi.Progress(DemoInvestigation.Disclosure + " Writing its session and the investigation that holds it.");
        DiscoveryDemoResult made = DiscoveryDemo.Create(full, cancellationToken);
        var document = new DemoDocument
        {
            Contract = DemoInvestigation.SourceIdentity,
            Directory = made.Directory,
            Investigation = made.WorkspacePath,
            Sessions = [made.Session],
            SourceIdentity = DemoInvestigation.SourceIdentity,
            Disclosure = DemoInvestigation.Disclosure,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Discovery demo");
        ConsoleUi.Field("Investigation", made.WorkspacePath);
        ConsoleUi.Field("Demo workstation", made.Session);
        ConsoleUi.Note(DemoInvestigation.Disclosure);
        ConsoleUi.Note($"Its resources: icat resources \"{made.Session}\", or open the investigation in the window's "
            + "Investigation menu, whose graph draws the ones two processes held.");
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat demo <directory> [--discovery] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Writes InterCat's generated demo into a new or empty folder: an investigation of two hosts that");
        ConsoleUi.Line("  exchange TCP, one of which also sends UDP to a host neither recorded, each with a session of its");
        ConsoleUi.Line("  own, aligned by their wall clocks. Nothing in it was captured, and every view of it says so.");
        ConsoleUi.Line("  Its sessions are the same, record for record, every time it is written.");
        ConsoleUi.Line();
        ConsoleUi.Line("  --discovery writes the discovery demo instead: one host whose processes share named");
        ConsoleUi.Line("  synchronization objects and a section, by its handles and by its views, as a capture of");
        ConsoleUi.Line("  them would find them.");
    }
}
