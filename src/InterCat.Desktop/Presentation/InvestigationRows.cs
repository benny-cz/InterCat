using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// One member of an investigation as its window lists it: where it stands, its host, its time and what the investigation
/// keeps of its view (ADR-038, §26.3).
/// </summary>
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
    bool IsAligned = false,
    IReadOnlyList<Guid>? AlignedThrough = null,
    Guid HostId = default,
    string? Kept = null) : IAccessibleRow
{
    /// <summary>Whether it has a place in the investigation's time: it is its clock, or aligned to a session that has one.</summary>
    public bool IsPlaced => IsTimeReference || IsAligned;

    public string AccessibleName => $"{Title}. {Detail}. {Time}." + (Kept is null ? string.Empty : $" {Kept}")
        + (Reason is null ? string.Empty : $" {Reason}") + (HoldsItsCapture ? " Press Enter to open it." : " Relink it to open it.");
}

/// <summary>An investigation as its window shows it: the file, a summary, its members and what it cannot say.</summary>
public sealed record InvestigationView(
    string Path,
    string Summary,
    string Time,
    IReadOnlyList<InvestigationMemberRow> Members,
    IReadOnlyList<string> Caveats,
    Guid? TimeReference = null,
    IReadOnlyList<string>? Overlaps = null)
{
    /// <summary>The investigation's notes in force, each with where it is pinned (§8.4).</summary>
    public IReadOnlyList<InvestigationNoteRow> Notes { get; init; } = [];

    /// <summary>What opening any of its sessions puts back of the window's panes, in words; null when it keeps none (§26.3).</summary>
    public string? Panes { get; init; }
}

/// <summary>A note as the investigation window lists it: its words, and where it is pinned in words.</summary>
public sealed record InvestigationNoteRow(Guid NoteId, string Text, string Where, WorkspaceNoteAnchor? At) : IAccessibleRow
{
    public string Title => Text;

    public string AccessibleName => $"Note {NoteId.ToString("N")[..8]}: {Text}. {Where}.";
}

/// <summary>One candidate join as the investigation window lists it (ADR-041): what matched, each end, and its evidence.</summary>
public sealed record InvestigationCandidateRow(
    string Title,
    string First,
    string Second,
    string Evidence,
    bool Ambiguous,
    WorkspaceJoinEnd? FirstEnd = null,
    WorkspaceJoinEnd? SecondEnd = null,
    WorkspaceJoinDecision? Decision = null) : IAccessibleRow
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
                string host = HostLabel(hosts, member.HostId);
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
                    InvestigationWorkspace.ActiveAlignment(workspace, member.SessionId) is not null,
                    InvestigationWorkspace.ChainOf(workspace, member.SessionId) is { } chain
                        ? [.. chain.Links.Skip(1).Select(link => link.Clock)]
                        : [],
                    member.HostId,
                    Kept(InvestigationWorkspace.LayoutOf(workspace, member.SessionId), culture));
            }),
        ];

        string summary = rows.Length == 0
            ? "No session yet: add the sessions this investigation covers."
            : string.Create(culture, $"{rows.Length:N0} {(rows.Length == 1 ? "session" : "sessions")}: ")
                + string.Join(", ", rows.GroupBy(row => row.State).OrderBy(group => group.Key).Select(group =>
                    string.Create(culture, $"{group.Count():N0} {group.Key.ToString().ToLowerInvariant()}")))
                + HostCount(workspace, hosts, culture);
        int others = rows.Length - 1;
        string time = workspace.TimeReference is { } reference
            ? $"Time: session {Short(reference)}'s clock; " + string.Create(culture,
                $"{rows.Count(row => InvestigationWorkspace.ActiveAlignment(workspace, row.SessionId) is not null):N0} of {others:N0} other {(others == 1 ? "session is" : "sessions are")} aligned to it")
                + (rows.Any(row => row.AlignedThrough is { Count: > 0 }) ? ", directly or through another." : ".")
            : "Time: none. No session is aligned to another, so no order, latency or pairing across sessions is stated.";
        return new(path, summary, time, rows,
        [
            "Each session is named by its identity and the capture its journal records; one capture is one member, and "
                + "showing an investigation writes to no session.",
            "Hosts are grouped by identity, which is evidence of one host and never proof; no name or address makes two one.",
        ], workspace.TimeReference,
        [.. InvestigationTimeline.Overlaps(path, cancellationToken).Select(overlap => overlap.Statement(culture))])
        {
            Notes = [.. InvestigationWorkspace.NotesInForce(workspace).Select(note => new InvestigationNoteRow(
                note.NoteId, note.Text!, NotePlace(workspace, note, culture), note.At))],
            Panes = PanesKept(workspace.Panes, culture),
        };
    }

    /// <summary>
    /// What opening a member from its investigation puts back of its view (§26.3), in the words `icat workspace show` says
    /// it in: "Opens with 1 node pinned on its graph, as it was left here."; null when the investigation keeps nothing of it.
    /// </summary>
    public static string? Kept(WorkspaceLayout? layout, CultureInfo culture) =>
        layout?.Describe(culture) is { Length: > 0 } kept ? $"Opens with {kept}, as it was left here." : null;

    /// <summary>
    /// What opening any of an investigation's sessions puts back of the window's panes (§6.1, §26.3), in the words `icat
    /// workspace show` says them in: "Its sessions open with the timeline filling the column, as the panes were left
    /// here."; null when it keeps none, and a session opened from it leaves the panes as they are.
    /// </summary>
    public static string? PanesKept(WorkspacePanes? panes, CultureInfo culture) =>
        panes?.Describe(culture) is { Length: > 0 } kept ? $"Its sessions open with {kept}, as the panes were left here." : null;

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
                string decided = candidate.Decision?.Decision switch
                {
                    WorkspaceJoinDecision.Accepted => " · accepted by a person",
                    WorkspaceJoinDecision.Rejected => " · rejected by a person",
                    _ => string.Empty,
                } + (candidate.Decision is not null && !candidate.DecisionCurrent ? ", to review" : string.Empty);
                return new InvestigationCandidateRow(
                    $"{protocol} {first.LocalEndpoint} ⇄ {first.RemoteEndpoint} · {timing}{alternatives}{decided}",
                    End(candidate.First, culture),
                    End(candidate.Second, culture),
                    string.Join(" ", candidate.Evidence),
                    candidate.Ambiguous,
                    candidate.Ends.First,
                    candidate.Ends.Second,
                    candidate.Decision?.Decision);
            }),
        ];
        string summary = rows.Length == 0
            ? "No candidate join: no connection one session holds one end of has its mirrored end in another."
            : string.Create(culture, $"{rows.Length:N0} candidate {(rows.Length == 1 ? "join" : "joins")}, none established by evidence; ")
                + (rows.Any(row => row.Decision is not null)
                    ? string.Create(culture, $"{rows.Count(row => row.Decision == WorkspaceJoinDecision.Accepted):N0} accepted and ")
                        + string.Create(culture, $"{rows.Count(row => row.Decision == WorkspaceJoinDecision.Rejected):N0} rejected by a person; ")
                    : string.Empty)
                + string.Create(culture, $"{rows.Count(row => row.Ambiguous):N0} not the only match of a connection.");
        List<string> notes = [];
        if (result.Snapshot.Count > 0)
        {
            // What the candidates answer (I16): a session that records on can hold more by the next comparison.
            IReadOnlyList<WorkspaceMember> members = InvestigationWorkspace.Read(path).Members;
            notes.Add("Compared " + string.Join(", ", result.Snapshot.Select(entry => string.Create(culture,
                $"session {Short(members.First(member => member.CaptureId == entry.CaptureId.Value).SessionId)} at generation {entry.Generation:N0}")))
                + ".");
        }

        if (result.DisjointMirrors > 0 || result.LoopbackAcrossHosts > 0)
        {
            notes.Add(string.Create(culture, $"Not proposed: {result.DisjointMirrors:N0} mirrored pairs whose lifetimes lie apart "
                + $"beyond their uncertainty, and {result.LoopbackAcrossHosts:N0} loopback pairs of two hosts."));
        }

        notes.AddRange(result.Unread.Select(unread => $"Not compared: session {Short(unread.SessionId)}, because {unread.Reason}"));
        notes.AddRange((result.DecidedElsewhere ?? []).Select(unmatched => string.Create(culture,
            $"Join revision {unmatched.Join.Revision} is {unmatched.Join.Decision.ToString().ToLowerInvariant()} by a person, but {unmatched.Why}.")));
        notes.AddRange(result.Caveats);
        return new(rows, summary, notes);
    }

    /// <summary>
    /// The investigation's merged time (§8.2): its timeline over <paramref name="columns"/> columns, a label for each lane,
    /// and each lane in words - where its records fall in the investigation's time, how sure that placement is, or why a
    /// session has no place.
    /// </summary>
    public static (InvestigationTimelineView View, IReadOnlyList<string> Labels, IReadOnlyList<string> Sentences) Timeline(
        string path,
        CultureInfo culture,
        int columns,
        TimeRange? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(path);
        IReadOnlyList<WorkspaceHost> hosts = InvestigationWorkspace.Hosts(workspace);
        InvestigationTimelineView view = InvestigationTimeline.Read(path, columns, interval, cancellationToken);
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
        return (view, labels, sentences);
    }

    /// <summary>
    /// What a placed lane's capture covered over its columns, as its lane is hatched (R21): how many of them hold each
    /// coverage state, so a lane of few records is not heard as a quiet session where its capture saw nothing.
    /// </summary>
    private static string Coverage(InvestigationLane lane, CultureInfo culture)
    {
        IGrouping<CoverageState, TimelineBucket>[] states = [.. lane.Buckets.GroupBy(bucket => bucket.Coverage).OrderBy(state => state.Key)];
        return string.Create(culture, $" Coverage over its {lane.Buckets.Count:N0} columns: ") + (states.Length == 1
            ? "all " + CoverageStateText.Value(states[0].Key)
            : string.Join("; ", states.Select(state => string.Create(culture, $"{state.Count():N0} {CoverageStateText.Value(state.Key)}")))) + ".";
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

        // Aligned to another aligned session, it is placed through that session's alignment too.
        bool direct = alignment.ReferenceSessionId == workspace.TimeReference;
        string whose = direct ? "the reference's" : $"session {Short(alignment.ReferenceSessionId!.Value)}'s";
        string to = direct ? string.Empty : $" to session {Short(alignment.ReferenceSessionId!.Value)}, itself aligned";
        string at = $"its {Seconds(alignment.SessionNanoseconds!.Value, culture)} is {whose} "
            + Seconds(alignment.ReferenceNanoseconds!.Value, culture);
        string within = alignment.WithinNanoseconds == 0
            ? "exactly"
            : "within ±" + OperationText.DurationAtLeast(alignment.WithinNanoseconds!.Value, culture);
        string drift = alignment.DriftPartsPerMillion is { } rate
            ? string.Create(culture, $", drifting at most {rate:0.###} ppm")
            : ", its drift not stated, so unknown away from that instant";
        if (alignment is { SecondSessionNanoseconds: { } second, SecondReferenceNanoseconds: { } secondReference })
        {
            double measured = InvestigationWorkspace.MeasuredPartsPerMillion(alignment);
            string wander = alignment.DriftPartsPerMillion is { } bound
                ? string.Create(culture, $", its rate wandering at most {bound:0.###} ppm")
                : ", its rate's wander not stated, so unknown away from those instants";
            return $"Aligned by a person at two instants{to}: its {Seconds(alignment.SessionNanoseconds!.Value, culture)} and "
                + $"{Seconds(second, culture)} are {whose} {Seconds(alignment.ReferenceNanoseconds!.Value, culture)} and "
                + $"{Seconds(secondReference, culture)}, {within}, so its clock runs "
                + (measured >= 0 ? "+" : "−") + Math.Abs(measured).ToString("0.###", culture) + $" ppm against {(direct ? "the reference's" : "that session's")}{wander}";
        }

        return alignment.Mode switch
        {
            WorkspaceAlignmentMode.SameBoot => $"Aligned by one boot's counter{to}: {at}, {within}",
            WorkspaceAlignmentMode.WallClock => $"Aligned by the wall clocks{to}: {at}, {within}{drift}",
            _ => $"Aligned by a person{to}: {at}, {within}{drift}",
        };
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

    /// <summary>
    /// Where an instant falls in the investigation's time, in words: exactly, within its uncertainty, with an unknown one,
    /// or nowhere - and why.
    /// </summary>
    public static string Placed(string label, WorkspaceInstant instant, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(instant);
        ArgumentNullException.ThrowIfNull(culture);
        string session = $"session {Short(instant.SessionId)}";
        string at = $"{label} instant, {session}'s {Seconds(instant.SessionNanoseconds, culture)},";
        return instant switch
        {
            { WorkspaceNanoseconds: null } => $"{at} has no place in the investigation's time: {instant.Why(session, culture)}.",
            { Uncertainty: null } => $"{at} falls at {Seconds(instant.WorkspaceNanoseconds.Value, culture)} of the investigation's "
                + $"time, but how surely is unknown: {instant.Why(session, culture)}.",
            { Uncertainty.HalfWidthNanoseconds: 0 } => $"{at} is the investigation's {Seconds(instant.WorkspaceNanoseconds.Value, culture)}, exactly.",
            _ => $"{at} is the investigation's {Seconds(instant.WorkspaceNanoseconds.Value, culture)}, within ±"
                + OperationText.DurationAtLeast(instant.Uncertainty!.Value.HalfWidthNanoseconds, culture) + ".",
        };
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

    /// <summary>How many hosts, counting identities a person confirmed are one host as one, and how many identities they are.</summary>
    private static string HostCount(InvestigationWorkspaceFile workspace, IReadOnlyList<WorkspaceHost> hosts, CultureInfo culture)
    {
        int count = hosts.Select(host => InvestigationWorkspace.HostKey(workspace, host.HostId)).Distinct().Count();
        return string.Create(culture, $" · {count:N0} {(count == 1 ? "host" : "hosts")}")
            + (count == hosts.Count ? string.Empty : string.Create(culture, $" ({hosts.Count:N0} identities)"));
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static string Seconds(long nanoseconds, CultureInfo culture) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", culture) + " s";

    private static string Shown(string stored, string full) =>
        System.IO.Path.IsPathRooted(stored) ? full : stored.Replace('/', System.IO.Path.DirectorySeparatorChar);
}
