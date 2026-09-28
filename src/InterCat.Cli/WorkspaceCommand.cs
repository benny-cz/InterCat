using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>A workspace with each member resolved against where it was last found (`contracts/workspace-v3.md` §3).</summary>
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

    /// <summary>The revision of the member's alignment in force; null when it is the time reference or not aligned.</summary>
    public required int? Alignment { get; init; }
}

/// <summary>Two members' instants compared in the workspace's time (`contracts/workspace-v3.md` §5).</summary>
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

/// <summary>Candidate joins between an investigation's captures (`contracts/workspace-v3.md` §6).</summary>
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
    public required IReadOnlyList<string> Caveats { get; init; }
}

internal sealed record CandidateDocument
{
    public required CandidateEndDocument First { get; init; }
    public required CandidateEndDocument Second { get; init; }
    public required CandidateTiming Timing { get; init; }

    /// <summary>How many other candidates either connection has.</summary>
    public required int Alternatives { get; init; }

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
    public const string ResolutionContract = "workspace-resolution-v3";

    public const string ComparisonContract = "workspace-comparison-v1";

    public const string CorrelationContract = "workspace-correlation-v1";

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
        string? within = command.TakeOption("--within");
        string? drift = command.TakeOption("--drift-ppm");
        string? note = command.TakeOption("--note");
        string? sync = command.TakeOption("--sync");
        List<string> operands = [];
        while (command.TakePositional() is { } operand)
        {
            operands.Add(operand);
        }

        bool remove = command.TryTakeFlag("--remove");
        bool withdraw = command.TryTakeFlag("--withdraw");
        bool sameBoot = command.TryTakeFlag("--same-boot");
        bool wallClock = command.TryTakeFlag("--wall-clock");
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
            "align" when withdraw => (1, 1, "icat workspace align <workspace> <session> --withdraw"),
            "align" when sameBoot => (2, 2, "icat workspace align <workspace> <session> <reference> --same-boot"),
            "align" when wallClock => (2, 2,
                "icat workspace align <workspace> <session> <reference> --wall-clock --sync <duration> --drift-ppm <rate>"),
            "align" => (2, 2, "icat workspace align <workspace> <session>@<seconds> <reference>@<seconds> --within <duration>"),
            "compare" => (2, 2, "icat workspace compare <workspace> <session>@<seconds> <session>@<seconds>"),
            "correlate" => (0, 0, "icat workspace correlate <workspace>"),
            _ => (-1, -1, string.Empty),
        };
        bool manual = verb == "align" && !withdraw && !sameBoot && !wallClock;
        bool misplaced = (remove && verb != "alias")
            || ((withdraw || sameBoot || wallClock) && verb != "align")
            || new[] { withdraw, sameBoot, wallClock }.Count(flag => flag) > 1
            || (within is null) == manual
            || (sync is null) == wallClock
            || (drift is not null && !manual && !wallClock) || (wallClock && drift is null)
            || (note is not null && (verb != "align" || withdraw));
        if (least < 0 || workspace is null || operands.Count < least || operands.Count > most || misplaced)
        {
            ConsoleUi.Failure(least < 0
                ? "icat workspace expects new, add, show, relink, alias, align, compare or correlate"
                    + (verb is null ? "." : $"; '{verb}' is none of them.")
                : $"Use {form}.");
            PrintHelp();
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

            InterCatExitCode written = verb switch
            {
                "new" => New(path),
                "add" => Add(path, operands),
                "relink" => Relink(path, operands[0], operands[1]),
                "alias" => Alias(path, operands[0], remove ? null : operands[1]),
                "align" when withdraw => Withdraw(path, operands[0]),
                "align" when sameBoot => AlignSameBoot(path, operands[0], operands[1], note),
                "align" when wallClock => AlignByWallClock(path, operands[0], operands[1], sync!, drift!, note),
                "align" => Align(path, operands[0], operands[1], within!, drift, note),
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

    private static InterCatExitCode Align(string path, string instant, string referenceInstant, string within, string? drift, string? note)
    {
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        (WorkspaceMember member, long at) = Instant(workspace, instant);
        (WorkspaceMember reference, long referenceAt) = Instant(workspace, referenceInstant);
        long bound = Duration(within)
            ?? throw new InvalidOperationException($"--within expects a duration with its unit, such as 500us, 2ms or 1s; '{within}' is not one.");
        double? rate = null;
        if (drift is not null)
        {
            rate = double.TryParse(drift.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed) && parsed >= 0
                ? parsed
                : throw new InvalidOperationException($"--drift-ppm expects a non-negative rate in parts per million; '{drift}' is not one.");
        }

        WorkspaceAlignment alignment = InvestigationWorkspace.Align(
            path, member.SessionId, at, reference.SessionId, referenceAt, bound, rate, note, DateTimeOffset.UtcNow);
        ConsoleUi.Success($"Session {Short(member.SessionId)} at {Seconds(at)} is session {Short(reference.SessionId)} at "
            + $"{Seconds(referenceAt)}, within ±{OperationText.Duration(bound, CultureInfo.CurrentCulture)} "
            + string.Create(CultureInfo.InvariantCulture, $"(alignment revision {alignment.Revision})."));
        if (rate is null)
        {
            ConsoleUi.Warn("No drift bound was stated, so away from the anchor this member's uncertainty is unknown and no order "
                + "is stated there. --drift-ppm bounds how fast the two clocks drift apart.");
        }

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
            + (alignment.WithinNanoseconds == 0 ? "of the reference, exactly" : "of the reference, within ±2 ns of rounding")
            + string.Create(CultureInfo.InvariantCulture, $" (alignment revision {alignment.Revision})."));
        return InterCatExitCode.Success;
    }

    private static InterCatExitCode AlignByWallClock(string path, string session, string reference, string sync, string drift, string? note)
    {
        long agreement = Duration(sync)
            ?? throw new InvalidOperationException($"--sync expects a duration with its unit, such as 10ms; '{sync}' is not one.");
        double rate = double.TryParse(drift.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException($"--drift-ppm expects a non-negative rate in parts per million; '{drift}' is not one.");
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
                Evidence = candidate.Evidence,
            })],
            DisjointMirrors = result.DisjointMirrors,
            LoopbackAcrossHosts = result.LoopbackAcrossHosts,
            Unread = result.Unread,
            Caveats = result.Caveats,
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

        foreach (string caveat in result.Caveats)
        {
            ConsoleUi.Note(caveat);
        }

        return InterCatExitCode.Success;
    }

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
            })],
            Hosts = hosts,
            TimeReference = workspace.TimeReference,
            Alignments = workspace.Alignments,
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
                ["Session", "By", "At", "Is reference at", "Within", "Drift", "Revision", "Note"],
                [.. inForce.Select(alignment => (IReadOnlyList<string>)
                [
                    Short(alignment.SessionId),
                    alignment.Mode switch
                    {
                        WorkspaceAlignmentMode.SameBoot => "one boot",
                        WorkspaceAlignmentMode.WallClock => "wall clocks",
                        _ => "a person",
                    },
                    Seconds(alignment.SessionNanoseconds!.Value),
                    Seconds(alignment.ReferenceNanoseconds!.Value),
                    alignment.WithinNanoseconds == 0
                        ? "exact"
                        : "±" + OperationText.DurationAtLeast(alignment.WithinNanoseconds!.Value, CultureInfo.CurrentCulture),
                    alignment.Mode == WorkspaceAlignmentMode.SameBoot ? "none: one counter"
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

    /// <summary>A member and an instant of its session time, written `session@seconds`.</summary>
    private static (WorkspaceMember Member, long Nanoseconds) Instant(InvestigationWorkspaceFile workspace, string text)
    {
        int at = text.LastIndexOf('@');
        decimal seconds = 0;
        if (at <= 0 || !decimal.TryParse(text[(at + 1)..].Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out seconds) || Math.Abs(seconds) > 9_000_000_000m)
        {
            throw new InvalidOperationException(
                $"An instant is written <session>@<seconds>, its session time in seconds, such as 3f2a9c1b@12.5; '{text}' is not one.");
        }

        return (InvestigationWorkspace.MemberNamed(workspace, text[..at]), (long)Math.Round(seconds * 1_000_000_000m, MidpointRounding.ToEven));
    }

    /// <summary>A duration written with its unit - ns, us, µs, ms or s - in nanoseconds; null when it is not one.</summary>
    private static long? Duration(string text)
    {
        Match match = DurationPattern().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }

        decimal scale = match.Groups[2].Value switch
        {
            "ns" => 1m,
            "us" or "µs" => 1_000m,
            "ms" => 1_000_000m,
            _ => 1_000_000_000m,
        };
        decimal nanoseconds = value * scale;
        return nanoseconds > long.MaxValue ? null : (long)Math.Ceiling(nanoseconds);
    }

    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*(ns|us|µs|ms|s)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    /// <summary>A session instant in seconds, to the nanosecond, as a person reads it.</summary>
    private static string Seconds(long nanoseconds) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", CultureInfo.CurrentCulture) + " s";

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
        ConsoleUi.Line("icat workspace align <workspace> <session>@<seconds> <reference>@<seconds> --within <duration>");
        ConsoleUi.Line("                     [--drift-ppm <rate>] [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> <reference> --same-boot [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> <reference> --wall-clock --sync <duration>");
        ConsoleUi.Line("                     --drift-ppm <rate> [--note <text>] [--json]");
        ConsoleUi.Line("icat workspace align <workspace> <session> --withdraw [--json]");
        ConsoleUi.Line("icat workspace compare <workspace> <session>@<seconds> <session>@<seconds> [--json]");
        ConsoleUi.Line("icat workspace correlate <workspace> [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("An investigation over separately captured sessions (workspace-v2, ADR-038): one file that names each");
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
        ConsoleUi.Line("           anchor is unknown. Seconds are session time; a duration takes ns, us, ms or s.");
        ConsoleUi.Line("           --same-boot aligns two captures that recorded one boot exactly: they read one counter.");
        ConsoleUi.Line("           --wall-clock anchors on the two captures' recorded wall-clock samples; --sync states how");
        ConsoleUi.Line("           closely their wall clocks agreed, which no sample can measure, and --drift-ppm bounds");
        ConsoleUi.Line("           their counters' drift, over the time between the samples and away from them.");
        ConsoleUi.Line("  compare  places two instants in the workspace's time and states their order only beyond");
        ConsoleUi.Line("           their combined uncertainty; nothing when an instant has no time or its uncertainty");
        ConsoleUi.Line("           is unknown. Two instants of one member are ordered exactly.");
        ConsoleUi.Line("  correlate proposes candidate joins: a connection one session holds one end of, and another");
        ConsoleUi.Line("           session its mirrored end, where their lifetimes can overlap in the investigation's");
        ConsoleUi.Line("           time. A candidate is never established; its evidence and alternatives are listed.");
    }
}
