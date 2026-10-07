using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// An investigation's merged time in words (§8.2), said once for the window beside its chart and for <c>icat workspace
/// timeline</c> (R18, R15): each lane's label and sentence - where its records fall in the investigation's time, how sure
/// that placement is, what its capture covered over its columns, or why a session has no place - then the overlaps of
/// one host's captures and the notes in force, each where it is pinned.
/// </summary>
public static class InvestigationTimelineText
{
    public static (IReadOnlyList<string> Labels, IReadOnlyList<string> Sentences) Describe(
        InvestigationWorkspaceFile workspace,
        InvestigationTimelineView view,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(culture);
        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(workspace);
        var labels = new List<string>();
        var sentences = new List<string>();
        foreach (InvestigationLane lane in view.Lanes)
        {
            WorkspaceMember member = workspace.Members.First(known => known.SessionId == lane.SessionId);
            string host = HostLabel(hosts, member.HostId);
            string place = lane switch
            {
                { Unread: { } unread } => "not placed: " + unread,
                { Placed: false, Gap: WorkspaceTimeGap.NoTimeReference } => "not placed: no session is aligned yet",
                { Placed: false } => "not placed: not aligned to the investigation's time",
                _ when lane.SessionId == workspace.TimeReference => "the investigation's own clock, exactly",
                { Uncertainty.HalfWidthNanoseconds: 0 } => "placed exactly",
                { Uncertainty: { } uncertainty } => "placed within ±" + OperationText.DurationAtLeast(uncertainty.HalfWidthNanoseconds, culture),
                _ => "placed, its uncertainty unknown at its ends: its drift is not stated",
            };
            labels.Add($"Session {Short(lane.SessionId)} · {host}\n{place}");

            // The generation its columns were counted from (I16); a session that records on holds more at its next read.
            string read = view.Snapshot.FirstOrDefault(entry => entry.CaptureId.Value == member.CaptureId) is { } entry
                ? string.Create(culture, $" Read at its generation {entry.Generation:N0}.")
                : string.Empty;
            sentences.Add((lane.Placed
                ? string.Create(culture, $"Session {Short(lane.SessionId)} ({host}): {lane.Records:N0} {(lane.Records == 1 ? "record" : "records")}, from ")
                    + Seconds(lane.Extent!.Value.StartTicks * 100, culture) + " to " + Seconds(lane.Extent.Value.EndTicks * 100, culture)
                    + $" of the investigation's time, {place}." + Coverage(lane, culture)
                : $"Session {Short(lane.SessionId)} ({host}): {place}.") + read);
        }

        sentences.AddRange(view.Overlaps.Select(overlap => overlap.Statement(culture)));
        sentences.AddRange(InvestigationWorkspace.NotesInForce(workspace).Select(note =>
            $"Note {Short(note.NoteId)}, {NotePlace(workspace, note, culture)}: {note.Text}"));
        return (labels, sentences);
    }

    /// <summary>
    /// What a placed lane's capture covered over its columns, as its lane is hatched (R21): how many of them hold each
    /// coverage state, so a lane of few records is not heard as a quiet session where its capture saw nothing.
    /// </summary>
    public static string Coverage(InvestigationLane lane, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(lane);
        IGrouping<CoverageState, TimelineBucket>[] states = [.. lane.Buckets.GroupBy(bucket => bucket.Coverage).OrderBy(state => state.Key)];
        return string.Create(culture, $" Coverage over its {lane.Buckets.Count:N0} columns: ") + (states.Length == 1
            ? "all " + CoverageStateText.Value(states[0].Key)
            : string.Join("; ", states.Select(state => string.Create(culture, $"{state.Count():N0} {CoverageStateText.Value(state.Key)}")))) + ".";
    }

    /// <summary>
    /// A host as a person reads it: its name, or its identity's start; with the other identities a person confirmed are one
    /// host with it, which only that confirmation makes one.
    /// </summary>
    public static string HostLabel(IReadOnlyList<WorkspaceHost> hosts, Guid hostId)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        WorkspaceHost host = hosts.First(known => known.HostId == hostId);
        string Name(WorkspaceHost known) => known.Alias ?? "host " + Short(known.HostId);
        return host.OneHostWith.Count == 0
            ? Name(host)
            : $"{Name(host)}, one host with "
                + string.Join(" and ", host.OneHostWith.Select(other => Name(hosts.First(known => known.HostId == other))))
                + " by a person's confirmation";
    }

    /// <summary>Where a note is pinned, in words: about the whole investigation, or at an instant of a session and its place.</summary>
    public static string NotePlace(InvestigationWorkspaceFile workspace, WorkspaceNote note, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(note);
        if (note.At is not { } at)
        {
            return "about the whole investigation";
        }

        WorkspaceInstant instant = InvestigationWorkspace.Place(workspace, at.SessionId, at.Nanoseconds);
        string pinned = $"pinned at session {Short(at.SessionId)}'s {Seconds(at.Nanoseconds, culture)}";
        return instant switch
        {
            { WorkspaceNanoseconds: null } => pinned + ", which has no place in the investigation's time",
            { Uncertainty: null } => $"{pinned}, the investigation's {Seconds(instant.WorkspaceNanoseconds.Value, culture)}, how surely unknown",
            { Uncertainty.HalfWidthNanoseconds: 0 } => $"{pinned}, the investigation's {Seconds(instant.WorkspaceNanoseconds.Value, culture)}",
            _ => $"{pinned}, the investigation's {Seconds(instant.WorkspaceNanoseconds.Value, culture)} within ±"
                + OperationText.DurationAtLeast(instant.Uncertainty!.Value.HalfWidthNanoseconds, culture),
        };
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static string Seconds(long nanoseconds, CultureInfo culture) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", culture) + " s";
}
