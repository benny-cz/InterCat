using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop.Presentation;

/// <summary>One member of an investigation as its window lists it: where it stands, its host and its time (ADR-038).</summary>
public sealed record InvestigationMemberRow(
    Guid SessionId,
    string Title,
    string Detail,
    string? Reason,
    string Time,
    string FullPath,
    WorkspaceMemberState State,
    bool HoldsItsCapture,
    bool IsTimeReference = false,
    bool IsAligned = false) : IAccessibleRow
{
    public string AccessibleName => $"{Title}. {Detail}. {Time}." + (Reason is null ? string.Empty : $" {Reason}")
        + (HoldsItsCapture ? " Press Enter to open it." : " Relink it to open it.");
}

/// <summary>An investigation as its window shows it: the file, a summary, its members and what it cannot say.</summary>
public sealed record InvestigationView(
    string Path,
    string Summary,
    string Time,
    IReadOnlyList<InvestigationMemberRow> Members,
    IReadOnlyList<string> Caveats,
    Guid? TimeReference = null);

/// <summary>One candidate join as the investigation window lists it (ADR-041): what matched, each end, and its evidence.</summary>
public sealed record InvestigationCandidateRow(
    string Title,
    string First,
    string Second,
    string Evidence,
    bool Ambiguous) : IAccessibleRow
{
    public string AccessibleName => $"Candidate join: {Title}. {First}. {Second}. {Evidence}";
}

/// <summary>What looking for candidate joins found: its rows and, in words, what was not proposed or not compared.</summary>
public sealed record InvestigationCandidates(IReadOnlyList<InvestigationCandidateRow> Rows, string Summary, IReadOnlyList<string> Notes);

/// <summary>
/// Reads an investigation for its window: the workspace file, each member resolved where it was last found, its host and
/// its alignment in words. Resolving opens each session as a viewer does and writes nothing (ADR-038 decision 1).
/// </summary>
public static class InvestigationRows
{
    public static InvestigationView Describe(string path, CultureInfo culture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(workspace);
        IReadOnlyList<WorkspaceMemberResolution> resolutions = InvestigationWorkspace.Resolve(path, workspace, cancellationToken);
        InvestigationMemberRow[] rows =
        [
            .. resolutions.Select(resolution =>
            {
                WorkspaceMember member = resolution.Member;
                string host = hosts.First(known => known.HostId == member.HostId).Alias ?? "host " + Short(member.HostId);
                string state = resolution.State switch
                {
                    WorkspaceMemberState.Advanced => string.Create(culture, $"advanced to generation {resolution.CurrentGeneration:N0}"),
                    WorkspaceMemberState.Replaced => string.Create(culture, $"replaced by generation {resolution.CurrentGeneration:N0}"),
                    _ => resolution.State.ToString().ToLowerInvariant(),
                };
                return new InvestigationMemberRow(
                    member.SessionId,
                    $"Session {Short(member.SessionId)}, {state}",
                    $"{host} · {Shown(member.Path, resolution.FullPath)}",
                    resolution.Reason,
                    TimeOf(workspace, member.SessionId, culture),
                    resolution.FullPath,
                    resolution.State,
                    resolution.HoldsItsCapture,
                    member.SessionId == workspace.TimeReference,
                    InvestigationWorkspace.ActiveAlignment(workspace, member.SessionId) is not null);
            }),
        ];

        string summary = rows.Length == 0
            ? "No session yet: add the sessions this investigation covers."
            : string.Create(culture, $"{rows.Length:N0} {(rows.Length == 1 ? "session" : "sessions")}: ")
                + string.Join(", ", rows.GroupBy(row => row.State).OrderBy(group => group.Key).Select(group =>
                    string.Create(culture, $"{group.Count():N0} {group.Key.ToString().ToLowerInvariant()}")))
                + string.Create(culture, $" · {hosts.Count:N0} {(hosts.Count == 1 ? "host" : "hosts")}");
        int others = rows.Length - 1;
        string time = workspace.TimeReference is { } reference
            ? $"Time: session {Short(reference)}'s clock; " + string.Create(culture,
                $"{rows.Count(row => InvestigationWorkspace.ActiveAlignment(workspace, row.SessionId) is not null):N0} of {others:N0} other {(others == 1 ? "session is" : "sessions are")} aligned to it.")
            : "Time: none. No session is aligned to another, so no order, latency or pairing across sessions is stated.";
        return new(path, summary, time, rows,
        [
            "Each session is named by its identity and the capture its journal records; one capture is one member, and "
                + "showing an investigation writes to no session.",
            "Hosts are grouped by identity, which is evidence of one host and never proof; no name or address makes two one.",
        ], workspace.TimeReference);
    }

    /// <summary>
    /// The investigation's candidate joins (ADR-041), in words: each one's endpoints and timing, its two ends with their
    /// process and bytes, and its evidence; with what was not proposed and which sessions were not compared.
    /// </summary>
    public static InvestigationCandidates Candidates(string path, CultureInfo culture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);
        WorkspaceCorrelationResult result = WorkspaceCorrelation.Candidates(path, cancellationToken: cancellationToken);
        InvestigationCandidateRow[] rows =
        [
            .. result.Candidates.Select(candidate =>
            {
                ConnectionSummary first = candidate.First.Connection.Summary;
                string protocol = first.Mechanism == InterCat.Domain.Mechanism.Udp ? "UDP" : "TCP";
                string timing = candidate.Timing == CandidateTiming.Overlapping ? "lifetimes overlap" : "lifetimes not comparable";
                string alternatives = candidate.Alternatives == 0
                    ? string.Empty
                    : string.Create(culture, $" · not the only match: {candidate.Alternatives:N0} other {(candidate.Alternatives == 1 ? "candidate" : "candidates")}");
                return new InvestigationCandidateRow(
                    $"{protocol} {first.LocalEndpoint} ⇄ {first.RemoteEndpoint} · {timing}{alternatives}",
                    End(candidate.First, culture),
                    End(candidate.Second, culture),
                    string.Join(" ", candidate.Evidence),
                    candidate.Ambiguous);
            }),
        ];
        string summary = rows.Length == 0
            ? "No candidate join: no connection one session holds one end of has its mirrored end in another."
            : string.Create(culture, $"{rows.Length:N0} candidate {(rows.Length == 1 ? "join" : "joins")}, none established; ")
                + string.Create(culture, $"{rows.Count(row => row.Ambiguous):N0} not the only match of a connection.");
        List<string> notes = [];
        if (result.DisjointMirrors > 0 || result.LoopbackAcrossHosts > 0)
        {
            notes.Add(string.Create(culture, $"Not proposed: {result.DisjointMirrors:N0} mirrored pairs whose lifetimes lie apart "
                + $"beyond their uncertainty, and {result.LoopbackAcrossHosts:N0} loopback pairs of two hosts."));
        }

        notes.AddRange(result.Unread.Select(unread => $"Not compared: session {Short(unread.SessionId)}, because {unread.Reason}"));
        notes.AddRange(result.Caveats);
        return new(rows, summary, notes);
    }

    private static string End(WorkspaceConnection end, CultureInfo culture)
    {
        ConnectionSummary summary = end.Connection.Summary;
        string image = end.Connection.Holder.ImagePath is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "a process of no recorded image";
        return $"Session {Short(end.SessionId)}: {image} (PID {end.Connection.Holder.ProcessId.ToString(culture)}), "
            + summary.Transfers(culture) + ", " + summary.Lifetime;
    }

    /// <summary>A member's place in the investigation's time, in words.</summary>
    public static string TimeOf(InvestigationWorkspaceFile workspace, Guid sessionId, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (sessionId == workspace.TimeReference)
        {
            return "Its clock is the investigation's time";
        }

        if (InvestigationWorkspace.ActiveAlignment(workspace, sessionId) is not { } alignment)
        {
            return "Not aligned: no order against the other sessions is stated";
        }

        string at = $"its {Seconds(alignment.SessionNanoseconds!.Value, culture)} is the reference's "
            + Seconds(alignment.ReferenceNanoseconds!.Value, culture);
        string within = alignment.WithinNanoseconds == 0
            ? "exactly"
            : "within ±" + OperationText.DurationAtLeast(alignment.WithinNanoseconds!.Value, culture);
        string drift = alignment.DriftPartsPerMillion is { } rate
            ? string.Create(culture, $", drifting at most {rate:0.###} ppm")
            : ", its drift not stated, so unknown away from that instant";
        return alignment.Mode switch
        {
            WorkspaceAlignmentMode.SameBoot => $"Aligned by one boot's counter: {at}, {within}",
            WorkspaceAlignmentMode.WallClock => $"Aligned by the wall clocks: {at}, {within}{drift}",
            _ => $"Aligned by a person: {at}, {within}{drift}",
        };
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static string Seconds(long nanoseconds, CultureInfo culture) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", culture) + " s";

    private static string Shown(string stored, string full) =>
        System.IO.Path.IsPathRooted(stored) ? full : stored.Replace('/', System.IO.Path.DirectorySeparatorChar);
}
