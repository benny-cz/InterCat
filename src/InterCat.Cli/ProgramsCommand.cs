using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The programs whose processes talked to each other (§6.3, R18): every pair of programs a session's relationships join,
/// each program by its executable as the graph's groups are, the busiest first, named by which program's processes made
/// the connections - or the pairs one program is in, as the window's inspector lists them for a chosen program.
/// </summary>
internal static class ProgramsCommand
{
    /// <summary>The document --json writes: how many pairs there are, the program asked about, and the pairs listed.</summary>
    internal const string Contract = "program-pairs-v1";

    private const int DefaultLimit = 50;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? programText = command.TakeOption("--program");
        string? limitText = command.TakeOption("--limit");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat programs <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of pairs of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? asked = programText?.Trim();
        if (asked is { Length: 0 })
        {
            ConsoleUi.Failure("--program takes a program's name as icat programs lists it - svchost.exe - or its executable's path.");
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionOverviewBundle overview;
        try
        {
            overview = SessionOverviewProjector.Project(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        IReadOnlyList<ProgramPair> pairs = ProgramPairs.Of(overview.Edges, overview.Nodes, overview.Groups);
        if (asked is null)
        {
            return List(pairs, null, 0, path, limit, json);
        }

        ProcessGroup[] named = Named(overview.Groups, asked);
        if (named.Length != 1)
        {
            ConsoleUi.Failure(named.Length == 0
                ? $"No program of this session is named {asked}: a program is named by its executable's file name, as icat programs "
                    + "lists it, or by the executable's path."
                : $"--program {asked} names {named.Length} programs - "
                    + WorkspaceLayout.Series([.. named.Select(group => group.Name)]) + " - so name one as they are listed.");
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessGroup program = named[0];
        return List([.. pairs.Where(pair => pair.First.Key == program.Key || pair.Second.Key == program.Key)], program,
            overview.Nodes.Count(node => node.GroupKey == program.Key), path, limit, json);
    }

    /// <summary>
    /// The programs <paramref name="asked"/> names, case aside: by name as listed; else every one whose executable has that
    /// file name, as two of one name in different folders are listed apart, or that file name but for its extension -
    /// svchost for svchost.exe; else the one at that path.
    /// </summary>
    internal static ProcessGroup[] Named(IReadOnlyList<ProcessGroup> groups, string asked)
    {
        ProcessGroup[] byName = [.. groups.Where(group => string.Equals(group.Name, asked, StringComparison.OrdinalIgnoreCase))];
        if (byName.Length > 0)
        {
            return byName;
        }

        ProcessGroup[] byFile = [.. groups.Where(group => group.Detail is { } detail
            && string.Equals(FileName(detail), asked, StringComparison.OrdinalIgnoreCase))];
        if (byFile.Length > 0)
        {
            return byFile;
        }

        ProcessGroup[] byStem = [.. groups.Where(group => group.Detail is { } detail
            && string.Equals(Stem(FileName(detail)), asked, StringComparison.OrdinalIgnoreCase))];
        return byStem.Length > 0
            ? byStem
            : [.. groups.Where(group => string.Equals(group.Detail, asked, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The file name a witnessed Windows or POSIX path ends with.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>A file name but for its extension, if it has one: svchost of svchost.exe.</summary>
    private static string Stem(string file) => file.LastIndexOf('.') is > 0 and var dot ? file[..dot] : file;

    private static InterCatExitCode List(IReadOnlyList<ProgramPair> pairs, ProcessGroup? program, int members, string path, int limit,
        bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
                Program = program is null ? null : new { program.Name, Path = program.Detail, Processes = members },
                Pairs = pairs.Count,
                Listed = pairs.Take(limit).Select(pair => new
                {
                    First = new { pair.First.Name, Path = pair.First.Detail },
                    Second = new { pair.Second.Name, Path = pair.Second.Detail },
                    pair.Joined,
                    pair.Relationships,
                    pair.Processes,
                    Mechanisms = pair.Mechanisms.Select(MechanismText.Name),
                    pair.AcceptedPorts,
                    pair.Records,
                    pair.FirstConnected,
                    pair.SecondConnected,
                }),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading(program is null ? "Programs that talked to each other" : $"Programs {program.Name} talked with");
        ConsoleUi.Field("Session", path);
        if (program is not null)
        {
            ConsoleUi.Field("Program", program.Name + " · " + CountText.Of(members, "process", "processes"));
            if (program.Detail is { } executable)
            {
                ConsoleUi.Field("Executable", executable);
            }
        }

        if (pairs.Count == 0)
        {
            ConsoleUi.Note(program is null
                ? "No relationship joins two processes of this session, so no program talked with another. A process's "
                    + "connections to other hosts are listed by icat hosts <directory>."
                : $"No relationship joins {program.Name}'s processes to a process of this session. Their connections to other "
                    + "hosts are listed by icat hosts <directory>.");
            return InterCatExitCode.Success;
        }

        int programs = pairs.SelectMany(pair => new[] { pair.First.Key, pair.Second.Key }).Distinct(StringComparer.Ordinal).Count();
        if (program is null)
        {
            ConsoleUi.Field("Pairs", CountText.Of(pairs.Count, "pair") + " of programs, among " + CountText.Of(programs, "program"));
        }
        else
        {
            // The program asked about is among them, at both ends where its own processes connected to each other.
            bool own = pairs.Any(pair => pair.First.Key == pair.Second.Key);
            string others = CountText.Of(programs - 1, "other program");
            ConsoleUi.Field("Talked with", programs == 1 ? "its own processes, with each other"
                : own ? others + ", and its own processes with each other" : others);
        }
        ConsoleUi.Table(
            ["Pair of programs", "Relationships", "Processes", "Over", "Records"],
            [.. pairs.Take(limit).Select(pair => (IReadOnlyList<string>)
            [
                pair.Joined, ConsoleUi.Count(pair.Relationships), ConsoleUi.Count(pair.Processes), pair.Over,
                ConsoleUi.Count(pair.Records),
            ])]);
        if (pairs.Count > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{pairs.Count - limit:N0} more, quieter; --limit {pairs.Count:N0} lists every one."));
        }

        bool calls = pairs.Any(pair => pair.Mechanisms.Contains(Mechanism.Rpc));
        ConsoleUi.Note("Each program is counted by its executable, and one named at both ends is its own processes connecting to "
            + "each other. An arrow points at the program connected to, where the capture saw the connections open"
            + (calls ? ", or called, where an RPC call was linked to the call that served it" : string.Empty)
            + ", ⇄ joins two that each " + (calls ? "did so to" : "connected to") + " the other, and two joined by \"and\" were "
            + "seen opening none. "
            + (pairs.Take(limit).Any(pair => pair.AcceptedPorts.Count > 0)
                ? "Over names the ports the connections arrived at, at the program connected to. "
                : string.Empty)
            + "Processes counts those at either end, and records are both ends'. icat channels <directory> names each "
            + "connection's two processes.");
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat programs <session-directory> [--program <name>] [--limit <n>] [--json]");
        ConsoleUi.Line("  The programs whose processes talked to each other: every pair of programs the session's");
        ConsoleUi.Line("  relationships join, each program by its executable, the busiest first by their records, and");
        ConsoleUi.Line("  each named by which program's processes connected to which, where the capture saw them open.");
        ConsoleUi.Line("  --program lists the pairs one program is in: its name as listed, its executable's file name,");
        ConsoleUi.Line("  or its path. --limit lists that many pairs, 50 unless it says so; --json writes them as a");
        ConsoleUi.Line("  document, with how many there are in all.");
    }
}
