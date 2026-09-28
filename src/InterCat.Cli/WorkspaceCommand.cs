using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>A workspace with each member resolved against where it was last found (`contracts/workspace-v1.md` §3).</summary>
internal sealed record WorkspaceDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
    public required IReadOnlyList<WorkspaceMemberDocument> Members { get; init; }
    public required IReadOnlyList<WorkspaceHost> Hosts { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record WorkspaceMemberDocument
{
    public required Guid SessionId { get; init; }
    public required Guid CaptureId { get; init; }
    public required string Path { get; init; }
    public required string FullPath { get; init; }
    public required WorkspaceMemberState State { get; init; }

    /// <summary>Why the member is not present at its selected generation; null when it is.</summary>
    public required string? Reason { get; init; }

    public required long Generation { get; init; }

    /// <summary>The generation at its path, when a session is there; null when none is.</summary>
    public required long? CurrentGeneration { get; init; }

    public required string ManifestDigest { get; init; }
    public required Guid HostId { get; init; }

    /// <summary>A person's name for the member's host, when one was given.</summary>
    public required string? Host { get; init; }

    public required Guid ClockId { get; init; }
    public required long CaptureEpochNativeTicks { get; init; }
    public required DateTimeOffset AddedUtc { get; init; }
}

/// <summary>
/// Makes, extends and shows an investigation workspace (ADR-038, M4): one file naming separately valid sessions by identity,
/// one member per capture, resolved against where each was last found. It never writes to a session.
/// </summary>
internal static class WorkspaceCommand
{
    public const string ResolutionContract = "workspace-resolution-v1";

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? verb = command.TakePositional();
        string? workspace = command.TakePositional();
        List<string> operands = [];
        while (command.TakePositional() is { } operand)
        {
            operands.Add(operand);
        }

        bool remove = command.TryTakeFlag("--remove");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        (int least, int most, string form) = verb switch
        {
            "new" or "show" => (0, 0, $"icat workspace {verb} <workspace>"),
            "add" => (1, int.MaxValue, "icat workspace add <workspace> <session-dir>..."),
            "relink" => (2, 2, "icat workspace relink <workspace> <session> <session-dir>"),
            "alias" => (remove ? 1 : 2, remove ? 1 : 2, "icat workspace alias <workspace> <host> (<name> | --remove)"),
            _ => (-1, -1, string.Empty),
        };
        if (least < 0 || workspace is null || operands.Count < least || operands.Count > most || (remove && verb != "alias"))
        {
            ConsoleUi.Failure(least < 0
                ? $"icat workspace expects new, add, show, relink or alias{(verb is null ? string.Empty : $"; '{verb}' is none of them")}."
                : $"Use {form}.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = verb == "new" ? InvestigationWorkspace.PathFor(workspace) : Path.GetFullPath(workspace);
        try
        {
            InterCatExitCode written = verb switch
            {
                "new" => New(path),
                "add" => Add(path, operands),
                "relink" => Relink(path, operands[0], operands[1]),
                "alias" => Alias(path, operands[0], remove ? null : operands[1]),
                _ => InterCatExitCode.Success,
            };
            WorkspaceDocument document = Describe(path, cancellationToken);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
            }
            else
            {
                Render(document);
            }

            return verb != "show" ? written
                : document.Members.All(member => member.State == WorkspaceMemberState.Present) ? InterCatExitCode.Success
                : InterCatExitCode.PartialResultSuccess;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
    }

    private static InterCatExitCode New(string path)
    {
        InvestigationWorkspace.Create(path, DateTimeOffset.UtcNow);
        ConsoleUi.Success($"Workspace made at {path}. Add sessions with icat workspace add.");
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Add(string path, List<string> directories)
    {
        int added = 0;
        foreach (string directory in directories)
        {
            try
            {
                WorkspaceMember member = InvestigationWorkspace.Add(path, directory, DateTimeOffset.UtcNow);
                ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture,
                    $"Added session {Short(member.SessionId)} at generation {member.Generation}, recorded on host {Short(member.HostId)}."));
                added++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                ConsoleUi.Failure($"{Path.GetFullPath(directory)} was not added: {exception.Message}");
            }
        }

        return added == directories.Count ? InterCatExitCode.Success
            : added > 0 ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.InvalidInvocation;
    }

    private static InterCatExitCode Relink(string path, string session, string directory)
    {
        WorkspaceMember member = InvestigationWorkspace.MemberNamed(InvestigationWorkspace.Read(path), session);
        WorkspaceMember relinked = InvestigationWorkspace.Relink(path, member.SessionId, directory, DateTimeOffset.UtcNow);
        ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture,
            $"Session {Short(relinked.SessionId)} relinked to {Shown(relinked.Path, directory)}, at generation {relinked.Generation}."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Alias(string path, string host, string? name)
    {
        Guid hostId = InvestigationWorkspace.HostNamed(InvestigationWorkspace.Read(path), host);
        InvestigationWorkspace.Alias(path, hostId, name, DateTimeOffset.UtcNow);
        ConsoleUi.Success(name is null ? $"Host {Short(hostId)} has no name now." : $"Host {Short(hostId)} is called '{name.Trim()}'.");
        return InterCatExitCode.Success;
    }

    private static WorkspaceDocument Describe(string path, CancellationToken cancellationToken)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(workspace);
        List<string> caveats =
        [
            "A member is named by its session and the capture its journal records, and one capture is one member, so no "
                + "record is counted twice. Showing a workspace opens each session to learn what it is and writes to none.",
            "Members are grouped by host identity: a live capture's is derived from its installation and its machine's name "
                + "and build, an import's from its file. Equal identities are evidence of one host, never proof, and no name "
                + "or address makes two of them one.",
        ];
        if (hosts.Count > 1)
        {
            caveats.Add("No clock mapping between these hosts exists yet, so no order, latency or pairing across them is "
                + "stated: its uncertainty is unknown, not zero.");
        }

        return new()
        {
            Contract = ResolutionContract,
            Path = path,
            WorkspaceId = workspace.WorkspaceId,
            CreatedUtc = workspace.CreatedUtc,
            UpdatedUtc = workspace.UpdatedUtc,
            Members = [.. InvestigationWorkspace.Resolve(path, workspace, cancellationToken).Select(resolution => new WorkspaceMemberDocument
            {
                SessionId = resolution.Member.SessionId,
                CaptureId = resolution.Member.CaptureId,
                Path = resolution.Member.Path,
                FullPath = resolution.FullPath,
                State = resolution.State,
                Reason = resolution.Reason,
                Generation = resolution.Member.Generation,
                CurrentGeneration = resolution.CurrentGeneration,
                ManifestDigest = resolution.Member.ManifestDigest,
                HostId = resolution.Member.HostId,
                Host = hosts.First(host => host.HostId == resolution.Member.HostId).Alias,
                ClockId = resolution.Member.ClockId,
                CaptureEpochNativeTicks = resolution.Member.CaptureEpochNativeTicks,
                AddedUtc = resolution.Member.AddedUtc,
            })],
            Hosts = hosts,
            Caveats = caveats,
        };
    }

    private static void Render(WorkspaceDocument document)
    {
        ConsoleUi.Heading("Workspace");
        ConsoleUi.Field("File", document.Path);
        ConsoleUi.Field("Updated", document.UpdatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        ConsoleUi.Field("Members", document.Members.Count == 0
            ? "none yet"
            : string.Join(", ", document.Members.GroupBy(member => member.State).OrderBy(group => group.Key)
                .Select(group => string.Create(CultureInfo.CurrentCulture, $"{group.Count():N0} {group.Key.ToString().ToLowerInvariant()}"))));
        ConsoleUi.Line();
        if (document.Members.Count == 0)
        {
            ConsoleUi.Note("This workspace names no session yet: icat workspace add <workspace> <session-dir> adds one.");
            return;
        }

        ConsoleUi.Table(
            ["Session", "State", "Generation", "Host", "Path"],
            [.. document.Members.Select(member => (IReadOnlyList<string>)
            [
                Short(member.SessionId),
                member.State switch
                {
                    WorkspaceMemberState.Advanced => string.Create(CultureInfo.CurrentCulture, $"Advanced to {member.CurrentGeneration:N0}"),
                    WorkspaceMemberState.Replaced => string.Create(CultureInfo.CurrentCulture, $"Replaced by {member.CurrentGeneration:N0}"),
                    _ => member.State.ToString(),
                },
                ConsoleUi.Count(member.Generation),
                member.Host ?? Short(member.HostId),
                Shown(member.Path, member.FullPath),
            ])]);
        foreach (WorkspaceMemberDocument member in document.Members.Where(member => member.Reason is not null))
        {
            ConsoleUi.Note($"{Short(member.SessionId)}: {member.Reason}");
        }

        ConsoleUi.Line();
        ConsoleUi.Heading("Hosts");
        ConsoleUi.Table(
            ["Host", "Name", "Members"],
            [.. document.Hosts.Select(host => (IReadOnlyList<string>)
            [
                host.HostId.ToString("N"),
                host.Alias ?? "-",
                string.Join(", ", host.Members.Select(Short)),
            ])]);
        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    /// <summary>A member's path as this system writes paths: relative as stored, or the whole path it resolves to.</summary>
    private static string Shown(string stored, string full) =>
        Path.IsPathRooted(stored) ? Path.GetFullPath(full) : stored.Replace('/', Path.DirectorySeparatorChar);

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat workspace new <workspace> [--json]");
        ConsoleUi.Line("icat workspace add <workspace> <session-dir>... [--json]");
        ConsoleUi.Line("icat workspace show <workspace> [--json]");
        ConsoleUi.Line("icat workspace relink <workspace> <session> <session-dir> [--json]");
        ConsoleUi.Line("icat workspace alias <workspace> <host> (<name> | --remove) [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("An investigation over separately captured sessions (workspace-v1, ADR-038): one file that names each");
        ConsoleUi.Line("session by identity - its session and the capture its journal records - and never writes to one.");
        ConsoleUi.Line("  new     makes an empty workspace; a name without an extension gets .icat-workspace.");
        ConsoleUi.Line("  add     adds sessions at their current generation. A capture is one member: a copy of a");
        ConsoleUi.Line("          member, or another session of its capture, is refused.");
        ConsoleUi.Line("  show    resolves each member where it was last found: present, advanced (a newer generation");
        ConsoleUi.Line("          was published), replaced (an older or separately derived one is there), missing,");
        ConsoleUi.Line("          different or unreadable, and why. Exits 1 when any member is not present.");
        ConsoleUi.Line("  relink  points a member at a new path, or at its own to select what is there, only when the");
        ConsoleUi.Line("          session there is that member. <session> is its identity or a unique leading part.");
        ConsoleUi.Line("  alias   names a member's host for people; one name never names two host identities.");
        ConsoleUi.Line("          <host> is its identity, a unique leading part, or its current name.");
    }
}
