using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>A workspace with each member resolved against where it was last found (`contracts/workspace-v13.md` §3).</summary>
internal sealed record WorkspaceDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
    public required IReadOnlyList<WorkspaceMemberDocument> Members { get; init; }
    public required IReadOnlyList<WorkspaceHost> Hosts { get; init; }

    /// <summary>The member whose clock is the workspace's time; null while no member is aligned.</summary>
    public required Guid? TimeReference { get; init; }

    /// <summary>Every alignment revision, in the order recorded; each member's latest one is in force.</summary>
    public required IReadOnlyList<WorkspaceAlignment> Alignments { get; init; }

    /// <summary>Every join decision revision, in the order recorded; each pair's latest one is in force.</summary>
    public required IReadOnlyList<WorkspaceJoin> Joins { get; init; }

    /// <summary>Every revision of a person's confirmation that two host identities are one host, in the order recorded.</summary>
    public required IReadOnlyList<WorkspaceHostEquivalence> HostEquivalences { get; init; }

    /// <summary>Every revision of a person's statement of a known address translation, in the order recorded.</summary>
    public required IReadOnlyList<WorkspaceAddressTranslation> AddressTranslations { get; init; }

    /// <summary>Every revision of a person's notes, in the order written.</summary>
    public required IReadOnlyList<WorkspaceNote> Notes { get; init; }

    /// <summary>Every revision of a person's saved views of the investigation's time, in the order saved.</summary>
    public required IReadOnlyList<WorkspaceView> Views { get; init; }

    /// <summary>Two captures of one host that ran, or may have run, at once, or whose overlap is unknown (§8.4).</summary>
    public required IReadOnlyList<OverlapDocument> Overlaps { get; init; }

    /// <summary>The snapshot vector the overlaps answer (I16): each capture read to place it, at its one generation.</summary>
    public required IReadOnlyList<SnapshotEntryDocument> OverlapsSnapshotVector { get; init; }

    /// <summary>How a person laid out each member's view: the nodes they pinned, where, and its ranking (§26.3).</summary>
    public required IReadOnlyList<WorkspaceLayout> Layouts { get; init; }

    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record OverlapDocument
{
    public required Guid First { get; init; }
    public required Guid Second { get; init; }
    public required OverlapKind Kind { get; init; }

    /// <summary>What the two share of the investigation's time, in 100 ns ticks; null when it is not known.</summary>
    public required TimeRange? Shared { get; init; }

    public required string Statement { get; init; }
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

    /// <summary>The revision of the member's alignment in force; null when it is the time reference or not aligned.</summary>
    public required int? Alignment { get; init; }

    /// <summary>The members it is aligned through to the time reference, nearest first; empty when aligned to it or not placed.</summary>
    public required IReadOnlyList<Guid> Through { get; init; }
}

/// <summary>Two members' instants compared in the workspace's time (`contracts/workspace-v13.md` §5).</summary>
internal sealed record WorkspaceComparisonDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required WorkspaceInstantDocument First { get; init; }
    public required WorkspaceInstantDocument Second { get; init; }
    public required TimeOrder Order { get; init; }

    /// <summary>The second instant less the first in the workspace's time; null when nothing is stated.</summary>
    public required long? DifferenceNanoseconds { get; init; }

    /// <summary>The pair's half-width; null when it is unknown.</summary>
    public required double? UncertaintyNanoseconds { get; init; }

    public required string Statement { get; init; }
}

internal sealed record WorkspaceInstantDocument
{
    public required Guid SessionId { get; init; }
    public required long SessionNanoseconds { get; init; }
    public required long? WorkspaceNanoseconds { get; init; }
    public required double? UncertaintyNanoseconds { get; init; }

    /// <summary>Why the instant has no workspace time, or no known uncertainty; None when it has both.</summary>
    public required WorkspaceTimeGap Gap { get; init; }

    /// <summary>The instant's distance from its member's anchor; null when its member is not aligned.</summary>
    public required long? FromAnchorNanoseconds { get; init; }
}

/// <summary>Candidate joins between an investigation's captures (`contracts/workspace-v13.md` §6).</summary>
internal sealed record WorkspaceCorrelationDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Rule { get; init; }
    public required IReadOnlyList<CandidateDocument> Candidates { get; init; }

    /// <summary>Mirrored pairs not proposed: their lifetimes lie apart beyond their uncertainty.</summary>
    public required int DisjointMirrors { get; init; }

    /// <summary>Mirrored loopback pairs on two hosts, which are never one connection.</summary>
    public required int LoopbackAcrossHosts { get; init; }

    public required IReadOnlyList<UnreadMember> Unread { get; init; }

    /// <summary>A person's decisions in force whose two connections are not a candidate now, and why.</summary>
    public required IReadOnlyList<DecisionDocument> DecidedElsewhere { get; init; }

    public required IReadOnlyList<string> Caveats { get; init; }

    /// <summary>The snapshot vector the candidates answer (I16): each compared capture's one generation, by capture.</summary>
    public required IReadOnlyList<SnapshotEntryDocument> SnapshotVector { get; init; }
}

/// <summary>One capture's generation a workspace result was read from, as a query identity's snapshot vector names it.</summary>
internal sealed record SnapshotEntryDocument
{
    public required string CaptureId { get; init; }
    public required Guid SessionId { get; init; }
    public required long Generation { get; init; }

    /// <summary>The digest of that generation's manifest: a generation number alone is local to one session.</summary>
    public required string Manifest { get; init; }
}

internal sealed record DecisionDocument
{
    public required int Revision { get; init; }
    public required WorkspaceJoinDecision Decision { get; init; }

    /// <summary>Whether it was made under the alignments in force now.</summary>
    public required bool Current { get; init; }

    public required string? Note { get; init; }
    public required string? Why { get; init; }
}

internal sealed record CandidateDocument
{
    public required CandidateEndDocument First { get; init; }
    public required CandidateEndDocument Second { get; init; }
    public required CandidateTiming Timing { get; init; }

    /// <summary>How many other candidates either connection has.</summary>
    public required int Alternatives { get; init; }

    /// <summary>A person's decision in force about it; null when undecided.</summary>
    public required DecisionDocument? Decision { get; init; }

    /// <summary>The address translations a person stated that its endpoints mirror through; empty when they mirror as seen.</summary>
    public required IReadOnlyList<WorkspaceAddressTranslation> Translations { get; init; }

    public required IReadOnlyList<string> Evidence { get; init; }
}

internal sealed record CandidateEndDocument
{
    public required Guid SessionId { get; init; }
    public required string Key { get; init; }
    public required string Protocol { get; init; }
    public required string LocalEndpoint { get; init; }
    public required string RemoteEndpoint { get; init; }
    public required int ProcessId { get; init; }
    public required string? ImagePath { get; init; }
    public required long FirstNanoseconds { get; init; }
    public required long LastNanoseconds { get; init; }
    public required long SentBytes { get; init; }
    public required long ReceivedBytes { get; init; }
    public required string Lifetime { get; init; }
}

/// <summary>
/// Makes, extends and shows an investigation workspace (ADR-038, M4): one file naming separately valid sessions by identity,
/// one member per capture, resolved against where each was last found, and aligned to one member's clock by a person. It
/// never writes to a session.
/// </summary>
internal static partial class WorkspaceCommand
{
    public const string ResolutionContract = "workspace-resolution-v15";

    public const string ComparisonContract = "workspace-comparison-v1";

    public const string CorrelationContract = "workspace-correlation-v4";

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        // Options that take a value go first, so a value is never read as the verb, the workspace or an operand.
        string? within = command.TakeOption("--within");
        string? drift = command.TakeOption("--drift-ppm");
        string? note = command.TakeOption("--note");
        string? sync = command.TakeOption("--sync");
        string? output = command.TakeOption("--output");
        string? pinned = command.TakeOption("--at");
        string? replacement = command.TakeOption("--replace");
        List<string> only = [];
        while (command.TakeOption("--only") is { } chosen)
        {
            only.Add(chosen);
        }

        string? verb = command.TakePositional();
        string? workspace = command.TakePositional();
        List<string> operands = [];
        while (command.TakePositional() is { } operand)
        {
            operands.Add(operand);
        }

        bool remove = command.TryTakeFlag("--remove");
        bool withdraw = command.TryTakeFlag("--withdraw");
        bool accept = command.TryTakeFlag("--accept");
        bool reject = command.TryTakeFlag("--reject");
        bool sameBoot = command.TryTakeFlag("--same-boot");
        bool wallClock = command.TryTakeFlag("--wall-clock");
        bool check = command.TryTakeFlag("--check");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        (int least, int most, string form) = verb switch
        {
            "new" or "show" => (0, 0, $"icat workspace {verb} <workspace>"),
            "add" => (1, int.MaxValue, "icat workspace add <workspace> <session-dir>..."),
            "relink" => (2, 2, "icat workspace relink <workspace> <session> <session-dir>"),
            "alias" => (remove ? 1 : 2, remove ? 1 : 2, "icat workspace alias <workspace> <host> (<name> | --remove)"),
            "align" when withdraw => (1, 1, "icat workspace align <workspace> <session> --withdraw"),
            "align" when sameBoot => (2, 2, "icat workspace align <workspace> <session> <reference> --same-boot"),
            "align" when wallClock => (2, 2,
                "icat workspace align <workspace> <session> <reference> --wall-clock --sync <duration> --drift-ppm <rate>"),
            "align" => (2, 4, "icat workspace align <workspace> <session>@<seconds> <reference>@<seconds> "
                + "[<session>@<seconds> <reference>@<seconds>] --within <duration>"),
            "compare" => (2, 2, "icat workspace compare <workspace> <session>@<seconds> <session>@<seconds>"),
            "correlate" => (0, 0, "icat workspace correlate <workspace>"),
            "join" => (1, 1, "icat workspace join <workspace> <candidate> (--accept | --reject | --withdraw) [--note <text>]"),
            "same-host" => (2, 2, "icat workspace same-host <workspace> <host> <other-host> [--withdraw] [--note <text>]"),
            "translate" => (2, 2, "icat workspace translate <workspace> <seen-endpoint> <endpoint> [--withdraw] [--note <text>]"),
            "note" => (1, 1, "icat workspace note <workspace> (<text> [--at <session>@<seconds>] | <note> --replace <text> | <note> --remove)"),
            "view" => (remove ? 1 : 3, remove ? 1 : 3, "icat workspace view <workspace> <name> (<from-seconds> <to-seconds> | --remove)"),
            "package" => (0, 0, "icat workspace package <workspace> --output <new-folder> [--only <session>]... [--check]"),
            _ => (-1, -1, string.Empty),
        };
        bool manual = verb == "align" && !withdraw && !sameBoot && !wallClock;
        bool misplaced = (remove && verb is not ("alias" or "note" or "view"))
            || ((pinned is not null || replacement is not null) && verb != "note")
            || (verb == "note" && new[] { pinned is not null, replacement is not null, remove }.Count(flag => flag) > 1)
            || ((sameBoot || wallClock) && verb != "align")
            || (withdraw && verb is not ("align" or "join" or "same-host" or "translate"))
            || ((accept || reject) && verb != "join")
            || (verb == "align" && new[] { withdraw, sameBoot, wallClock }.Count(flag => flag) > 1)
            || (verb == "join" && new[] { accept, reject, withdraw }.Count(flag => flag) != 1)
            || (within is null) == manual
            || (sync is null) == wallClock
            || (drift is not null && !manual && !wallClock) || (wallClock && drift is null)
            || (note is not null && !(verb is "align" or "same-host" or "translate" && !withdraw) && verb != "join")
            || ((output is not null || only.Count > 0 || check) && verb != "package")
            || (verb == "package" && output is null && !check);
        if (least < 0 || workspace is null || operands.Count < least || operands.Count > most || (manual && operands.Count == 3) || misplaced)
        {
            ConsoleUi.Failure(least < 0
                ? "icat workspace expects new, add, show, relink, alias, align, compare, correlate, join, same-host, translate, note, view or package"
                    + (verb is null ? "." : $"; '{verb}' is none of them.")
                : $"Use {form}.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = verb == "new" ? InvestigationWorkspace.PathFor(workspace) : Path.GetFullPath(workspace);
        try
        {
            if (verb == "compare")
            {
                return Compare(path, operands[0], operands[1], json);
            }

            if (verb == "correlate")
            {
                return Correlate(path, json, cancellationToken);
            }

            if (verb == "package")
            {
                return Package(path, output, only, check, json, cancellationToken);
            }

            if (verb == "join")
            {
                return Join(path, operands[0],
                    accept ? WorkspaceJoinDecision.Accepted : reject ? WorkspaceJoinDecision.Rejected : WorkspaceJoinDecision.Withdrawn,
                    note, json, cancellationToken);
            }

            InterCatExitCode written = verb switch
            {
                "new" => New(path),
                "add" => Add(path, operands),
                "relink" => Relink(path, operands[0], operands[1]),
                "alias" => Alias(path, operands[0], remove ? null : operands[1]),
                "same-host" => SameHost(path, operands[0], operands[1], withdraw, note),
                "translate" => Translate(path, operands[0], operands[1], withdraw, note),
                "note" => Note(path, operands[0], pinned, replacement, remove),
                "view" => View(path, operands, remove),
                "align" when withdraw => Withdraw(path, operands[0]),
                "align" when sameBoot => AlignSameBoot(path, operands[0], operands[1], note),
                "align" when wallClock => AlignByWallClock(path, operands[0], operands[1], sync!, drift!, note),
                "align" => Align(path, operands, within!, drift, note),
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

    private static InterCatExitCode Align(string path, List<string> instants, string within, string? drift, string? note)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        (WorkspaceMember member, long at) = Instant(workspace, instants[0]);
        (WorkspaceMember reference, long referenceAt) = Instant(workspace, instants[1]);
        (long, long)? second = null;
        if (instants.Count == 4)
        {
            (WorkspaceMember secondMember, long secondAt) = Instant(workspace, instants[2]);
            (WorkspaceMember secondReference, long secondReferenceAt) = Instant(workspace, instants[3]);
            if (secondMember.SessionId != member.SessionId || secondReference.SessionId != reference.SessionId)
            {
                throw new InvalidOperationException("A second instant names the same two sessions as the first, in the same order: "
                    + $"{Short(member.SessionId)}@<seconds> {Short(reference.SessionId)}@<seconds>.");
            }

            second = (secondAt, secondReferenceAt);
        }

        long bound = InvestigationInput.Duration(within)
            ?? throw new InvalidOperationException($"--within expects a duration with its unit, such as 500us, 2ms or 1s; '{within}' is not one.");
        double? rate = drift is null
            ? null
            : InvestigationInput.PartsPerMillion(drift)
                ?? throw new InvalidOperationException($"--drift-ppm expects a non-negative rate in parts per million; '{drift}' is not one.");

        WorkspaceAlignment alignment = InvestigationWorkspace.Align(
            path, member.SessionId, at, reference.SessionId, referenceAt, bound, rate, note, DateTimeOffset.UtcNow, second);
        ConsoleUi.Success($"Session {Short(member.SessionId)} at {Seconds(at)} is session {Short(reference.SessionId)} at "
            + $"{Seconds(referenceAt)}"
            + (second is { } other ? $", and at {Seconds(other.Item1)} its {Seconds(other.Item2)}," : string.Empty)
            + $" within ±{OperationText.Duration(bound, CultureInfo.CurrentCulture)} "
            + string.Create(CultureInfo.InvariantCulture, $"(alignment revision {alignment.Revision})."));
        if (second is not null)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"The two instants measure its clock running {Rate(InvestigationWorkspace.MeasuredPartsPerMillion(alignment))} against the reference's."));
        }

        if (rate is null)
        {
            ConsoleUi.Warn(second is null
                ? "No drift bound was stated, so away from the anchor this member's uncertainty is unknown and no order is stated "
                    + "there. --drift-ppm bounds how fast the two clocks drift apart."
                : "No bound on the rate's wander was stated, so away from the two instants this member's uncertainty is unknown "
                    + "and no order is stated there. --drift-ppm bounds how far the rate may wander from the one they measure.");
        }

        return InterCatExitCode.Success;
    }

    /// <summary>
    /// `icat workspace same-host`: records a person's confirmation that two host identities are one host, or withdraws it
    /// (§8.3). Their captures are then compared as one host's; their identities stay what they recorded.
    /// </summary>
    private static InterCatExitCode SameHost(string path, string host, string other, bool withdraw, string? note)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        Guid first = InvestigationWorkspace.HostNamed(workspace, host);
        Guid second = InvestigationWorkspace.HostNamed(workspace, other);
        WorkspaceHostEquivalence revision = withdraw
            ? InvestigationWorkspace.WithdrawOneHost(path, first, second, DateTimeOffset.UtcNow)
            : InvestigationWorkspace.ConfirmOneHost(path, first, second, note, DateTimeOffset.UtcNow);
        ConsoleUi.Success(withdraw
            ? string.Create(CultureInfo.InvariantCulture, $"Hosts {Short(first)} and {Short(second)} are two hosts again (host revision {revision.Revision}); ")
                + "the confirmation it withdraws is kept in the file."
            : string.Create(CultureInfo.InvariantCulture, $"Hosts {Short(first)} and {Short(second)} are one host by your confirmation (host revision {revision.Revision}). ")
                + "Their captures are compared as one host's - for overlaps, and loopback candidates - and their identities stay as recorded.");
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// `icat workspace translate`: records a person's statement that an endpoint one capture sees is an endpoint the other
    /// holds - a port forward, a NAT or a proxy - or withdraws it (§8.3). Candidate joins then mirror through it.
    /// </summary>
    private static InterCatExitCode Translate(string path, string seen, string actual, bool withdraw, string? note)
    {
        WorkspaceAddressTranslation revision = withdraw
            ? InvestigationWorkspace.WithdrawTranslation(path, seen, actual, DateTimeOffset.UtcNow)
            : InvestigationWorkspace.StateTranslation(path, seen, actual, note, DateTimeOffset.UtcNow);
        ConsoleUi.Success(withdraw
            ? string.Create(CultureInfo.InvariantCulture, $"{revision.Seen} and {revision.Is} are two endpoints again (translation revision {revision.Revision}); ")
                + "the statement it withdraws is kept in the file."
            : string.Create(CultureInfo.InvariantCulture, $"{revision.Seen} is {revision.Is} by your statement (translation revision {revision.Revision}). ")
                + "Candidate joins mirror through it, and each one that does says it rests on it.");
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// `icat workspace note`: adds a note, pinned at an instant of a member's session when `--at` gives one; replaces a
    /// note's words with `--replace`; or removes one with `--remove`. Every revision is kept (§8.4).
    /// </summary>
    private static InterCatExitCode Note(string path, string operand, string? pinned, string? replacement, bool remove)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        if (remove || replacement is not null)
        {
            WorkspaceNote named = InvestigationWorkspace.NoteNamed(workspace, operand);
            WorkspaceNote changed = remove
                ? InvestigationWorkspace.RemoveNote(path, named.NoteId, DateTimeOffset.UtcNow)
                : InvestigationWorkspace.EditNote(path, named.NoteId, replacement!, DateTimeOffset.UtcNow);
            ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture, $"Note {Short(named.NoteId)} ")
                + (remove ? "removed" : "reworded") + string.Create(CultureInfo.InvariantCulture, $" (note revision {changed.Revision}); its earlier words are kept."));
            return InterCatExitCode.Success;
        }

        WorkspaceNoteAnchor? at = null;
        if (pinned is not null)
        {
            (WorkspaceMember member, long nanoseconds) = Instant(workspace, pinned);
            at = new WorkspaceNoteAnchor(member.SessionId, nanoseconds);
        }

        WorkspaceNote added = InvestigationWorkspace.AddNote(path, operand, at, DateTimeOffset.UtcNow);
        ConsoleUi.Success($"Note {Short(added.NoteId)} added"
            + (at is { } where ? $", pinned at session {Short(where.SessionId)}'s {Seconds(where.Nanoseconds)}" : ", about the whole investigation")
            + string.Create(CultureInfo.InvariantCulture, $" (note revision {added.Revision})."));
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// `icat workspace view`: saves an interval of the investigation's time, in seconds of its reference session's clock, as
    /// a named view, replacing one of that name; or removes one. Every revision is kept (§8.4).
    /// </summary>
    private static InterCatExitCode View(string path, List<string> operands, bool remove)
    {
        if (remove)
        {
            WorkspaceView removed = InvestigationWorkspace.RemoveView(path, operands[0], DateTimeOffset.UtcNow);
            ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture, $"View '{removed.Name}' removed (view revision {removed.Revision}); its revisions are kept."));
            return InterCatExitCode.Success;
        }

        long from = InvestigationInput.Seconds(operands[1])
            ?? throw new InvalidOperationException($"A view's start is in seconds of the investigation's time, such as 12.5; '{operands[1]}' is not.");
        long to = InvestigationInput.Seconds(operands[2])
            ?? throw new InvalidOperationException($"A view's end is in seconds of the investigation's time, such as 14; '{operands[2]}' is not.");
        if (to / 100 <= from / 100)
        {
            throw new InvalidOperationException($"A view shows some of the investigation's time, so its end comes after its start; {Seconds(to)} is not after {Seconds(from)}.");
        }

        WorkspaceView saved = InvestigationWorkspace.SaveView(path, operands[0], new TimeRange(from / 100, to / 100), DateTimeOffset.UtcNow);
        ConsoleUi.Success($"View '{saved.Name}' saved: {Seconds(from)} to {Seconds(to)} of the investigation's time"
            + string.Create(CultureInfo.InvariantCulture, $" (view revision {saved.Revision})."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode AlignSameBoot(string path, string session, string reference, string? note)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        WorkspaceMember member = InvestigationWorkspace.MemberNamed(workspace, session);
        WorkspaceMember to = InvestigationWorkspace.MemberNamed(workspace, reference);
        WorkspaceAlignment alignment = InvestigationWorkspace.AlignSameBoot(path, member.SessionId, to.SessionId, note, DateTimeOffset.UtcNow);
        ConsoleUi.Success($"Session {Short(member.SessionId)} ran in session {Short(to.SessionId)}'s boot "
            + $"({Short(alignment.BootToken!.Value)}), so both read one counter: its instant 0 s is {Seconds(alignment.ReferenceNanoseconds!.Value)} "
            + $"of session {Short(to.SessionId)}'s, "
            + (alignment.WithinNanoseconds == 0 ? "exactly" : "within ±2 ns of rounding")
            + string.Create(CultureInfo.InvariantCulture, $" (alignment revision {alignment.Revision})."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode AlignByWallClock(string path, string session, string reference, string sync, string drift, string? note)
    {
        long agreement = InvestigationInput.Duration(sync)
            ?? throw new InvalidOperationException($"--sync expects a duration with its unit, such as 10ms; '{sync}' is not one.");
        double rate = InvestigationInput.PartsPerMillion(drift)
            ?? throw new InvalidOperationException($"--drift-ppm expects a non-negative rate in parts per million; '{drift}' is not one.");
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        WorkspaceMember member = InvestigationWorkspace.MemberNamed(workspace, session);
        WorkspaceMember to = InvestigationWorkspace.MemberNamed(workspace, reference);
        WorkspaceAlignment alignment = InvestigationWorkspace.AlignByWallClock(
            path, member.SessionId, to.SessionId, agreement, rate, note, DateTimeOffset.UtcNow);
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Success($"Session {Short(member.SessionId)} at {Seconds(alignment.SessionNanoseconds!.Value)} is session "
            + $"{Short(to.SessionId)} at {Seconds(alignment.ReferenceNanoseconds!.Value)}, within "
            + $"±{OperationText.DurationAtLeast(alignment.WithinNanoseconds!.Value, culture)}: the wall clocks' stated agreement "
            + $"±{OperationText.DurationAtLeast(agreement, culture)}, the samples' acquisition "
            + $"±{OperationText.DurationAtLeast(alignment.AcquisitionNanoseconds!.Value, culture)}, and the drift over the "
            + $"{OperationText.Duration(alignment.GapNanoseconds!.Value, culture)} between them"
            + string.Create(CultureInfo.InvariantCulture, $" (alignment revision {alignment.Revision})."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Withdraw(string path, string session)
    {
        WorkspaceMember member = InvestigationWorkspace.MemberNamed(InvestigationWorkspace.Read(path), session);
        WorkspaceAlignment withdrawal = InvestigationWorkspace.Withdraw(path, member.SessionId, DateTimeOffset.UtcNow);
        ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture,
            $"Session {Short(member.SessionId)} is not aligned any more (alignment revision {withdrawal.Revision}); its earlier revisions are kept."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Compare(string path, string first, string second, bool json)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        (WorkspaceMember a, long at) = Instant(workspace, first);
        (WorkspaceMember b, long bt) = Instant(workspace, second);
        WorkspaceComparison comparison = InvestigationWorkspace.Compare(workspace, a.SessionId, at, b.SessionId, bt);
        var document = new WorkspaceComparisonDocument
        {
            Contract = ComparisonContract,
            Path = path,
            First = InstantDocument(comparison.First),
            Second = InstantDocument(comparison.Second),
            Order = comparison.Result.Order,
            DifferenceNanoseconds = comparison.Result.DifferenceNanoseconds,
            UncertaintyNanoseconds = comparison.Result.Uncertainty?.HalfWidthNanoseconds,
            Statement = comparison.Statement(CultureInfo.CurrentCulture),
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Comparison");
        ConsoleUi.Field("First", Placed(comparison.First, workspace));
        ConsoleUi.Field("Second", Placed(comparison.Second, workspace));
        ConsoleUi.Line();
        ConsoleUi.Line("  " + document.Statement);
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Correlate(string path, bool json, CancellationToken cancellationToken)
    {
        ConsoleUi.Progress("Reading every session's one-sided connections and comparing their endpoints, mirrored.");
        WorkspaceCorrelationResult result = WorkspaceCorrelation.Candidates(path, cancellationToken: cancellationToken);
        var document = new WorkspaceCorrelationDocument
        {
            Contract = CorrelationContract,
            Path = path,
            Rule = result.Rule,
            Candidates = [.. result.Candidates.Select(candidate => new CandidateDocument
            {
                First = End(candidate.First),
                Second = End(candidate.Second),
                Timing = candidate.Timing,
                Alternatives = candidate.Alternatives,
                Translations = candidate.Translations,
                Decision = candidate.Decision is { } decision ? DecisionOf(decision, candidate.DecisionCurrent, null) : null,
                Evidence = candidate.Evidence,
            })],
            DisjointMirrors = result.DisjointMirrors,
            LoopbackAcrossHosts = result.LoopbackAcrossHosts,
            Unread = result.Unread,
            DecidedElsewhere = [.. (result.DecidedElsewhere ?? []).Select(unmatched => DecisionOf(unmatched.Join, true, unmatched.Why))],
            Caveats = result.Caveats,
            SnapshotVector = SnapshotOf(path, result.Snapshot),
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading("Connection candidates");
        ConsoleUi.Field("Investigation", path);
        ConsoleUi.Field("Rule", result.Rule);
        ConsoleUi.Field("Read", document.SnapshotVector.Count == 0
            ? "no session"
            : string.Join("; ", document.SnapshotVector.Select(entry =>
                string.Create(culture, $"{Short(entry.SessionId)} at generation {entry.Generation:N0}"))));
        int ambiguous = result.Candidates.Count(candidate => candidate.Ambiguous);
        ConsoleUi.Field("Candidates", string.Create(culture,
            $"{result.Candidates.Count:N0}, {ambiguous:N0} of them not the only match of a connection"));
        ConsoleUi.Line();
        int number = 0;
        foreach (CandidateDocument candidate in document.Candidates)
        {
            number++;
            ConsoleUi.Line(string.Create(culture, $"  {number:N0}. {candidate.First.Protocol} {candidate.First.LocalEndpoint} ⇄ "
                + $"{candidate.First.RemoteEndpoint} · ")
                + (candidate.Timing == CandidateTiming.Overlapping ? "lifetimes overlap" : "lifetimes not comparable")
                + (candidate.Alternatives > 0 ? string.Create(culture, $" · {candidate.Alternatives:N0} other candidates") : string.Empty));
            foreach (CandidateEndDocument end in new[] { candidate.First, candidate.Second })
            {
                ConsoleUi.Line(string.Create(culture, $"     {Short(end.SessionId)} · {Image(end)} (PID {end.ProcessId}): ")
                    + string.Create(culture, $"{end.SentBytes:N0} B sent, {end.ReceivedBytes:N0} B received; {end.Lifetime}"));
            }

            foreach (string line in candidate.Evidence)
            {
                ConsoleUi.Note("   - " + line);
            }
        }

        if (result.Candidates.Count == 0)
        {
            ConsoleUi.Note("No connection one session holds one end of has its mirrored end in another.");
        }

        ConsoleUi.Line();
        if (result.DisjointMirrors > 0 || result.LoopbackAcrossHosts > 0)
        {
            ConsoleUi.Note(string.Create(culture, $"Not proposed: {result.DisjointMirrors:N0} mirrored pairs whose lifetimes lie apart "
                + $"beyond their uncertainty, and {result.LoopbackAcrossHosts:N0} loopback pairs of two hosts."));
        }

        foreach (UnreadMember unread in result.Unread)
        {
            ConsoleUi.Note($"Not compared: session {Short(unread.SessionId)}, because {unread.Reason}");
        }

        foreach (DecisionDocument elsewhere in document.DecidedElsewhere)
        {
            ConsoleUi.Note(string.Create(culture, $"Join revision {elsewhere.Revision} is {elsewhere.Decision.ToString().ToLowerInvariant()} ")
                + $"by a person, but {elsewhere.Why}.");
        }

        foreach (string caveat in result.Caveats)
        {
            ConsoleUi.Note(caveat);
        }

        return InterCatExitCode.Success;
    }

    private static InterCatExitCode Join(string path, string candidate, WorkspaceJoinDecision decision, string? note, bool json,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        {
            throw new InvalidOperationException($"A candidate is named by its number in icat workspace correlate's list; '{candidate}' is not one.");
        }

        WorkspaceCorrelationResult result = WorkspaceCorrelation.Candidates(path, cancellationToken: cancellationToken);
        if (number > result.Candidates.Count)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"There are {result.Candidates.Count} candidates now, so there is no candidate {number}; icat workspace correlate lists them."));
        }

        ConnectionCandidate chosen = result.Candidates[number - 1];
        WorkspaceJoin join = InvestigationWorkspace.Decide(path, chosen.Ends.First, chosen.Ends.Second, decision, note, DateTimeOffset.UtcNow);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(join, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConnectionSummary summary = chosen.First.Connection.Summary;
        string what = decision switch
        {
            WorkspaceJoinDecision.Accepted => "accepted as one connection, by you, never as evidence",
            WorkspaceJoinDecision.Rejected => "rejected: its two ends are not one connection",
            _ => "undecided again; its earlier decisions are kept",
        };
        ConsoleUi.Success(string.Create(CultureInfo.InvariantCulture, $"Candidate {number} ({summary.LocalEndpoint} ⇄ {summary.RemoteEndpoint}) is ")
            + what + string.Create(CultureInfo.InvariantCulture, $" (join revision {join.Revision})."));
        return InterCatExitCode.Success;
    }

    private static DecisionDocument DecisionOf(WorkspaceJoin join, bool current, string? why) => new()
    {
        Revision = join.Revision,
        Decision = join.Decision,
        Current = current,
        Note = join.Note,
        Why = why,
    };

    private static CandidateEndDocument End(WorkspaceConnection connection)
    {
        ConnectionSummary summary = connection.Connection.Summary;
        return new()
        {
            SessionId = connection.SessionId,
            Key = summary.Key,
            Protocol = summary.Mechanism == Mechanism.Udp ? "UDP" : "TCP",
            LocalEndpoint = summary.LocalEndpoint,
            RemoteEndpoint = summary.RemoteEndpoint,
            ProcessId = connection.Connection.Holder.ProcessId,
            ImagePath = connection.Connection.Holder.ImagePath,
            FirstNanoseconds = connection.Connection.FirstNanoseconds,
            LastNanoseconds = connection.Connection.LastNanoseconds,
            SentBytes = summary.SentBytes,
            ReceivedBytes = summary.ReceivedBytes,
            Lifetime = summary.Lifetime,
        };
    }

    private static string Image(CandidateEndDocument end) =>
        end.ImagePath is { Length: > 0 } image ? Path.GetFileName(image) : "a process of no recorded image";

    private static WorkspaceInstantDocument InstantDocument(WorkspaceInstant instant) => new()
    {
        SessionId = instant.SessionId,
        SessionNanoseconds = instant.SessionNanoseconds,
        WorkspaceNanoseconds = instant.WorkspaceNanoseconds,
        UncertaintyNanoseconds = instant.Uncertainty?.HalfWidthNanoseconds,
        Gap = instant.Gap,
        FromAnchorNanoseconds = instant.FromAnchorNanoseconds,
    };

    /// <summary>An instant as a person reads it: its session and time, and where it falls in the workspace's time.</summary>
    private static string Placed(WorkspaceInstant instant, InvestigationWorkspaceFile workspace)
    {
        string at = $"session {Short(instant.SessionId)} at {Seconds(instant.SessionNanoseconds)}";
        return instant switch
        {
            _ when instant.SessionId == workspace.TimeReference => at + ", the workspace's time reference",
            { WorkspaceNanoseconds: { } placed, Uncertainty: { } uncertainty } =>
                $"{at}, which is {Seconds(placed)} ±{OperationText.DurationAtLeast(uncertainty.HalfWidthNanoseconds, CultureInfo.CurrentCulture)} of workspace time",
            { WorkspaceNanoseconds: { } placed } => $"{at}, which is {Seconds(placed)} of workspace time, its uncertainty unknown",
            _ => at + ", which has no workspace time",
        };
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
            workspace.TimeReference is null
                ? "No member is aligned, so the workspace has no time across members and states no order, latency or pairing "
                    + "between them: their uncertainty is unknown, not zero."
                : "The workspace's time is its reference member's clock. An aligned member's instants are placed in it with "
                    + "the uncertainty its alignment states, and an order across members is stated only beyond it; an "
                    + "alignment is an annotation and changes no timestamp.",
        ];
        InvestigationOverlaps overlaps = InvestigationTimeline.OverlapsRead(path, cancellationToken);
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
                Alignment = InvestigationWorkspace.ActiveAlignment(workspace, resolution.Member.SessionId)?.Revision,
                Through = InvestigationWorkspace.ChainOf(workspace, resolution.Member.SessionId) is { } chain
                    ? [.. chain.Links.Skip(1).Select(link => link.Clock)]
                    : [],
            })],
            Hosts = hosts,
            TimeReference = workspace.TimeReference,
            Alignments = workspace.Alignments,
            Joins = workspace.Joins,
            HostEquivalences = workspace.HostEquivalences,
            AddressTranslations = workspace.AddressTranslations,
            Notes = workspace.Notes,
            Views = workspace.Views,
            Overlaps = [.. overlaps.Overlaps.Select(overlap => new OverlapDocument
            {
                First = overlap.First,
                Second = overlap.Second,
                Kind = overlap.Kind,
                Shared = overlap.Shared,
                Statement = overlap.Statement(CultureInfo.CurrentCulture),
            })],
            OverlapsSnapshotVector = SnapshotOf(path, overlaps.Snapshot),
            Layouts = workspace.Layouts,
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
        int others = document.Members.Count - 1;
        ConsoleUi.Field("Time", document.TimeReference is { } reference
            ? $"session {Short(reference)}'s clock; " + string.Create(CultureInfo.CurrentCulture,
                $"{document.Members.Count(member => member.Alignment is not null):N0} of {others:N0} other {(others == 1 ? "member" : "members")} aligned to it")
                + (document.Members.Any(member => member.Through.Count > 0) ? ", directly or through another" : string.Empty)
            : "none: no member is aligned, so no order across members is stated");
        ConsoleUi.Line();
        if (document.Members.Count == 0)
        {
            ConsoleUi.Note("This workspace names no session yet: icat workspace add <workspace> <session-dir> adds one.");
            return;
        }

        ConsoleUi.Table(
            ["Session", "State", "Generation", "Host", "Time", "Path"],
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
                member.SessionId == document.TimeReference ? "reference"
                    : member.Alignment is { } revision ? string.Create(CultureInfo.CurrentCulture, $"aligned (revision {revision:N0})")
                    : "not aligned",
                Shown(member.Path, member.FullPath),
            ])]);
        foreach (WorkspaceMemberDocument member in document.Members.Where(member => member.Reason is not null))
        {
            ConsoleUi.Note($"{Short(member.SessionId)}: {member.Reason}");
        }

        WorkspaceAlignment[] inForce = [.. document.Members
            .Select(member => document.Alignments.Where(alignment => alignment.SessionId == member.SessionId).MaxBy(alignment => alignment.Revision))
            .OfType<WorkspaceAlignment>()
            .Where(alignment => alignment.Mode != WorkspaceAlignmentMode.Withdrawn)];
        if (inForce.Length > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Heading("Alignments");
            ConsoleUi.Table(
                ["Session", "To", "By", "At", "Is there at", "Within", "Drift", "Revision", "Note"],
                [.. inForce.Select(alignment => (IReadOnlyList<string>)
                [
                    Short(alignment.SessionId),
                    alignment.ReferenceSessionId == document.TimeReference ? "reference" : Short(alignment.ReferenceSessionId!.Value),
                    alignment.Mode switch
                    {
                        WorkspaceAlignmentMode.SameBoot => "one boot",
                        WorkspaceAlignmentMode.WallClock => "wall clocks",
                        _ => "a person",
                    },
                    Seconds(alignment.SessionNanoseconds!.Value)
                        + (alignment.SecondSessionNanoseconds is { } second ? ", " + Seconds(second) : string.Empty),
                    Seconds(alignment.ReferenceNanoseconds!.Value)
                        + (alignment.SecondReferenceNanoseconds is { } secondReference ? ", " + Seconds(secondReference) : string.Empty),
                    alignment.WithinNanoseconds == 0
                        ? "exact"
                        : "±" + OperationText.DurationAtLeast(alignment.WithinNanoseconds!.Value, CultureInfo.CurrentCulture),
                    alignment.Mode == WorkspaceAlignmentMode.SameBoot ? "none: one counter"
                        : alignment.SecondSessionNanoseconds is not null
                            ? $"rate {Rate(InvestigationWorkspace.MeasuredPartsPerMillion(alignment))}, wander "
                                + (alignment.DriftPartsPerMillion is { } wander
                                    ? string.Create(CultureInfo.CurrentCulture, $"≤ {wander:0.###} ppm")
                                    : "not stated")
                        : alignment.DriftPartsPerMillion is { } drift ? string.Create(CultureInfo.CurrentCulture, $"≤ {drift:0.###} ppm")
                        : "not stated",
                    ConsoleUi.Count(alignment.Revision),
                    alignment.Note ?? "-",
                ])]);
            int earlier = document.Alignments.Count - inForce.Length;
            if (earlier > 0)
            {
                ConsoleUi.Note(earlier == 1
                    ? "1 earlier alignment revision is kept in the file; icat workspace show --json lists it."
                    : string.Create(CultureInfo.CurrentCulture,
                        $"{earlier:N0} earlier alignment revisions are kept in the file; icat workspace show --json lists them."));
            }
        }

        InvestigationWorkspaceFile current = InvestigationWorkspace.Read(document.Path);
        IReadOnlyList<WorkspaceNote> notes = InvestigationWorkspace.NotesInForce(current);
        if (notes.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Heading("Notes");
            foreach (WorkspaceNote written in notes)
            {
                string where = written.At is not { } at ? "about the whole investigation"
                    : InvestigationWorkspace.Place(current, at.SessionId, at.Nanoseconds) is { WorkspaceNanoseconds: { } placed } instant
                        ? $"at session {Short(at.SessionId)}'s {Seconds(at.Nanoseconds)}, the investigation's {Seconds(placed)}"
                            + (instant.Uncertainty is { } uncertainty
                                ? uncertainty.HalfWidthNanoseconds == 0 ? " exactly"
                                    : " within ±" + OperationText.DurationAtLeast(uncertainty.HalfWidthNanoseconds, CultureInfo.CurrentCulture)
                                : ", how surely unknown")
                        : $"at session {Short(at.SessionId)}'s {Seconds(at.Nanoseconds)}, which has no place in the investigation's time";
                ConsoleUi.Note($"{Short(written.NoteId)} ({where}): {written.Text}");
            }
        }

        IReadOnlyList<WorkspaceView> views = InvestigationWorkspace.ViewsInForce(current);
        if (views.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Heading("Saved views");
            foreach (WorkspaceView view in views)
            {
                ConsoleUi.Note($"{view.Name}: {Seconds(view.Interval!.Value.StartTicks * 100)} to {Seconds(view.Interval.Value.EndTicks * 100)} of the investigation's time"
                    + (InvestigationWorkspace.ViewIsCurrent(current, view) ? "." : $", saved on session {Short(view.Reference!.Value)}'s clock, which is not its time now."));
            }
        }

        if (document.Layouts.Count > 0)
        {
            // What the Desktop puts back when a member is opened from this investigation (§26.3).
            ConsoleUi.Line();
            ConsoleUi.Heading("Layouts");
            foreach (WorkspaceLayout layout in document.Layouts)
            {
                string? pinned = layout.Pins.Count == 0 ? null : string.Create(CultureInfo.CurrentCulture,
                    $"{layout.Pins.Count:N0} {(layout.Pins.Count == 1 ? "node" : "nodes")} pinned on its graph");
                string? ranked = layout.RankBy is null && !layout.PerSecond ? null
                    : $"its rows ranked by {RankingMetrics.Phrase(layout.RankBy ?? RankingMetric.Records)}{(layout.PerSecond ? " per second" : string.Empty)}";
                string? counted = layout.EvidencePolicy == EvidencePolicy.IncludeCandidates ? "its records counted with candidates" : null;
                ConsoleUi.Note($"Session {Short(layout.SessionId)}: "
                    + string.Join(", and ", new[] { pinned, ranked, counted }.OfType<string>())
                    + ", put back when it is opened from this investigation.");
            }
        }

        IReadOnlyList<WorkspaceAddressTranslation> translations = InvestigationWorkspace.TranslationsInForce(document.AddressTranslations);
        if (translations.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Heading("Known address translations");
            foreach (WorkspaceAddressTranslation translation in translations)
            {
                ConsoleUi.Note(string.Create(CultureInfo.InvariantCulture, $"{translation.Seen} is {translation.Is}, by a person (revision {translation.Revision})")
                    + (translation.Note is { } remark ? $": {remark}" : "."));
            }
        }

        if (document.Overlaps.Count > 0)
        {
            ConsoleUi.Line();
            ConsoleUi.Heading("Overlaps");
            foreach (OverlapDocument overlap in document.Overlaps)
            {
                ConsoleUi.Note(overlap.Statement);
            }

            // An overlap of sessions with no place reads none of them: it is unknown, and says so.
            if (document.OverlapsSnapshotVector.Count > 0)
            {
                ConsoleUi.Note("Read from " + string.Join("; ", document.OverlapsSnapshotVector.Select(entry =>
                    string.Create(CultureInfo.CurrentCulture, $"{Short(entry.SessionId)} at generation {entry.Generation:N0}"))) + ".");
            }
        }

        ConsoleUi.Line();
        ConsoleUi.Heading("Hosts");
        ConsoleUi.Table(
            ["Host", "Name", "Members", "One host with, by a person"],
            [.. document.Hosts.Select(host => (IReadOnlyList<string>)
            [
                host.HostId.ToString("N"),
                host.Alias ?? "-",
                string.Join(", ", host.Members.Select(Short)),
                host.OneHostWith.Count == 0 ? "-" : string.Join(", ", host.OneHostWith.Select(Short)),
            ])]);
        ConsoleUi.Line();
        foreach (string caveat in document.Caveats)
        {
            ConsoleUi.Note(caveat);
        }
    }

    /// <summary>A member and an instant of its session time, written `session@seconds`.</summary>
    private static (WorkspaceMember Member, long Nanoseconds) Instant(InvestigationWorkspaceFile workspace, string text)
    {
        int at = text.LastIndexOf('@');
        return at > 0 && InvestigationInput.Seconds(text[(at + 1)..]) is { } nanoseconds
            ? (InvestigationWorkspace.MemberNamed(workspace, text[..at]), nanoseconds)
            : throw new InvalidOperationException(
                $"An instant is written <session>@<seconds>, its session time in seconds, such as 3f2a9c1b@12.5; '{text}' is not one.");
    }

    /// <summary>A measured rate against the reference, signed, as a person reads it: "+12.5 ppm".</summary>
    private static string Rate(double partsPerMillion) =>
        (partsPerMillion >= 0 ? "+" : "−") + Math.Abs(partsPerMillion).ToString("0.###", CultureInfo.CurrentCulture) + " ppm";

    /// <summary>A session instant in seconds, to the nanosecond, as a person reads it.</summary>
    private static string Seconds(long nanoseconds) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", CultureInfo.CurrentCulture) + " s";

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    /// <summary>A result's snapshot vector as documents, each capture beside the member session that holds it.</summary>
    private static SnapshotEntryDocument[] SnapshotOf(string path, IReadOnlyList<SnapshotEntry> snapshot)
    {
        IReadOnlyList<WorkspaceMember> members = InvestigationWorkspace.Read(path).Members;
        return
        [
            .. snapshot.Select(entry => new SnapshotEntryDocument
            {
                CaptureId = entry.CaptureId.Value.ToString("N"),
                SessionId = members.First(member => member.CaptureId == entry.CaptureId.Value).SessionId,
                Generation = entry.Generation,
                Manifest = entry.ManifestDigest,
            }),
        ];
    }

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
        ConsoleUi.Line("icat workspace align <workspace> <session>@<seconds> <reference>@<seconds>");
        ConsoleUi.Line("                     [<session>@<seconds> <reference>@<seconds>] --within <duration>");
        ConsoleUi.Line("                     [--drift-ppm <rate>] [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> <reference> --same-boot [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> <reference> --wall-clock --sync <duration>");
        ConsoleUi.Line("                     --drift-ppm <rate> [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> --withdraw [--json]");
        ConsoleUi.Line("icat workspace compare <workspace> <session>@<seconds> <session>@<seconds> [--json]");
        ConsoleUi.Line("icat workspace correlate <workspace> [--json]");
        ConsoleUi.Line("icat workspace join <workspace> <candidate> (--accept | --reject | --withdraw) [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace same-host <workspace> <host> <other-host> [--withdraw] [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace translate <workspace> <seen-endpoint> <endpoint> [--withdraw] [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace note <workspace> <text> [--at <session>@<seconds>] [--json]");
        ConsoleUi.Line("icat workspace note <workspace> <note> (--replace <text> | --remove) [--json]");
        ConsoleUi.Line("  A note that starts with a dash follows --, as in: icat workspace note <workspace> -- \"-> retry storm\"");
        ConsoleUi.Line("icat workspace view <workspace> <name> (<from-seconds> <to-seconds> | --remove) [--json]");
        ConsoleUi.Line("icat workspace package <workspace> --output <new-folder> [--only <session>]... [--check] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("An investigation over separately captured sessions (workspace-v4, ADR-038): one file that names each");
        ConsoleUi.Line("session by identity - its session and the capture its journal records - and never writes to one.");
        ConsoleUi.Line("  new      makes an empty workspace; a name without an extension gets .icat-workspace.");
        ConsoleUi.Line("  add      adds sessions at their current generation. A capture is one member: a copy of a");
        ConsoleUi.Line("           member, or another session of its capture, is refused.");
        ConsoleUi.Line("  show     resolves each member where it was last found: present, advanced (a newer generation");
        ConsoleUi.Line("           was published), replaced (an older or separately derived one is there), missing,");
        ConsoleUi.Line("           different or unreadable, and why. Exits 1 when any member is not present.");
        ConsoleUi.Line("  relink   points a member at a new path, or at its own to select what is there, only when the");
        ConsoleUi.Line("           session there is that member. <session> is its identity or a unique leading part.");
        ConsoleUi.Line("  alias    names a member's host for people; one name never names two host identities.");
        ConsoleUi.Line("           <host> is its identity, a unique leading part, or its current name.");
        ConsoleUi.Line("  align    states that an instant of one member is an instant of another, within a bound: the");
        ConsoleUi.Line("           first alignment makes the other member's clock the workspace's time. --drift-ppm");
        ConsoleUi.Line("           bounds how fast the clocks drift apart; without it the uncertainty away from the");
        ConsoleUi.Line("           anchor is unknown. Seconds are session time; a duration takes ns, us, ms or s. A second");
        ConsoleUi.Line("           pair of instants, well apart from the first, measures the clocks' rate; --drift-ppm then");
        ConsoleUi.Line("           bounds how far that rate may wander, and without it only the two instants are placed.");
        ConsoleUi.Line("           --same-boot aligns two captures that recorded one boot exactly: they read one counter.");
        ConsoleUi.Line("           <reference> is the time reference, or any member placed in its time: a member aligned");
        ConsoleUi.Line("           through another is placed through both, and two members aligned through one compare as");
        ConsoleUi.Line("           that shared alignment allows.");
        ConsoleUi.Line("           --wall-clock anchors on the two captures' recorded wall-clock samples; --sync states how");
        ConsoleUi.Line("           closely their wall clocks agreed, which no sample can measure, and --drift-ppm bounds");
        ConsoleUi.Line("           their counters' drift, over the time between the samples and away from them.");
        ConsoleUi.Line("  compare  places two instants in the workspace's time and states their order only beyond");
        ConsoleUi.Line("           their combined uncertainty; nothing when an instant has no time or its uncertainty");
        ConsoleUi.Line("           is unknown. Two instants of one member are ordered exactly.");
        ConsoleUi.Line("  correlate proposes candidate joins: a connection one session holds one end of, and another");
        ConsoleUi.Line("           session its mirrored end, where their lifetimes can overlap in the investigation's");
        ConsoleUi.Line("           time. A candidate is never established; its evidence and alternatives are listed.");
        ConsoleUi.Line("  join     records your decision about candidate <n> of correlate's list: accepted as one");
        ConsoleUi.Line("           connection, rejected, or withdrawn; each is a kept revision, and one made before the");
        ConsoleUi.Line("           alignments changed is flagged for review.");
        ConsoleUi.Line("  same-host records your confirmation that two host identities are one host - a machine renamed");
        ConsoleUi.Line("           or reinstalled, or a file imported from it - so their captures are compared as one host's:");
        ConsoleUi.Line("           for overlaps, and loopback candidates. Equal identities are evidence; yours is a kept");
        ConsoleUi.Line("           revision, withdrawn with --withdraw, and never evidence. <host> as for alias.");
        ConsoleUi.Line("  translate records your statement that an endpoint one capture sees - a port forward's, a NAT's or a");
        ConsoleUi.Line("           proxy's - is an endpoint the other holds, so candidate joins mirror through it and say so.");
        ConsoleUi.Line("           Both with a port, or both an address alone, whose ports pass through; never loopback.");
        ConsoleUi.Line("  note     adds a note to the investigation, pinned with --at at an instant of a session, which");
        ConsoleUi.Line("           its time places; --replace rewords one and --remove removes it, each a kept revision.");
        ConsoleUi.Line("           <note> is its identity or a unique leading part.");
        ConsoleUi.Line("  view     saves an interval of the investigation's time, in seconds of its reference's clock, as a");
        ConsoleUi.Line("           named view the Desktop's timeline can show again; --remove removes it. A view saved on");
        ConsoleUi.Line("           another time reference is kept, and said not to be of the time now.");
        ConsoleUi.Line("  package  copies the investigation with its sessions into a new folder, which opens anywhere as");
        ConsoleUi.Line("           the same investigation: each session that is where it was last found, or each named by");
        ConsoleUi.Line("           --only, as an exact original package, beside the investigation's file. Any other stays a");
        ConsoleUi.Line("           reference to relink. It is unredacted. --check measures it and writes nothing. Exits 1");
        ConsoleUi.Line("           when a session could not be copied.");
    }
}
