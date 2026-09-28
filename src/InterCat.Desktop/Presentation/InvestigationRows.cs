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
    bool HoldsItsCapture) : IAccessibleRow
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
    IReadOnlyList<string> Caveats);

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
                    resolution.HoldsItsCapture);
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
        ]);
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
