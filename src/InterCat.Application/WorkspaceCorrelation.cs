using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>Where two candidate connections' lifetimes stand against each other in an investigation's time.</summary>
public enum CandidateTiming
{
    /// <summary>Their lifetimes, each widened by its uncertainty in the investigation's time, overlap.</summary>
    Overlapping = 1,

    /// <summary>An end of either lifetime has no investigation time, or no known uncertainty: they cannot be compared.</summary>
    Unknown = 2,
}

/// <summary>One member's one-sided connection, as an investigation compares it with another member's.</summary>
public sealed record WorkspaceConnection(Guid SessionId, Guid HostId, HeldConnection Connection);

/// <summary>
/// A candidate join (§8.3): one member's one-sided connection and another's whose endpoints mirror it, of one protocol,
/// whose lifetimes can overlap in the investigation's time. It is never an established join: it says what matches, with
/// the evidence, and how many other candidates either connection has.
/// </summary>
public sealed record ConnectionCandidate(
    WorkspaceConnection First,
    WorkspaceConnection Second,
    CandidateTiming Timing,
    int Alternatives,
    IReadOnlyList<string> Evidence,
    WorkspaceJoin? Decision = null,
    bool DecisionCurrent = true)
{
    /// <summary>The address translations a person stated that its endpoints mirror through; empty when they mirror as seen.</summary>
    public IReadOnlyList<WorkspaceAddressTranslation> Translations { get; init; } = [];

    /// <summary>Whether either connection has another candidate too, so this one is not the only match of it.</summary>
    public bool Ambiguous => Alternatives > 0;

    /// <summary>The two ends as a join names them.</summary>
    public (WorkspaceJoinEnd First, WorkspaceJoinEnd Second) Ends =>
        (new(First.SessionId, First.Connection.Summary.Key), new(Second.SessionId, Second.Connection.Summary.Key));
}

/// <summary>A person's decision in force whose two connections are not a candidate now, and why.</summary>
public sealed record UnmatchedDecision(WorkspaceJoin Join, string Why);

/// <summary>A member an investigation could not compare, and why.</summary>
public sealed record UnreadMember(Guid SessionId, string Reason);

/// <summary>What comparing an investigation's members' one-sided connections found (`contracts/workspace-v8.md` §6).</summary>
public sealed record WorkspaceCorrelationResult(
    string Rule,
    IReadOnlyList<ConnectionCandidate> Candidates,
    int DisjointMirrors,
    int LoopbackAcrossHosts,
    IReadOnlyList<UnreadMember> Unread,
    IReadOnlyList<string> Caveats,
    IReadOnlyList<UnmatchedDecision>? DecidedElsewhere = null);

/// <summary>
/// Proposes candidate joins between an investigation's captures (§8.3, M4): a connection one capture holds only one end of
/// is compared with every other member's by its endpoints, mirrored, and one protocol, and kept where their lifetimes can
/// overlap in the investigation's time. Nothing is joined by time alone, by an address alone or by a name, and a
/// candidate is never established (P6, R22): mirrored endpoints can be two connections behind a translation, and a port
/// used again is another connection.
/// </summary>
public static class WorkspaceCorrelation
{
    public const string Rule = "cross-capture-connection-candidate-v2";

    public static WorkspaceCorrelationResult Candidates(
        string workspacePath,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(workspacePath);
        InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(full);
        var unread = new List<UnreadMember>();
        var members = new List<(WorkspaceMember Member, SessionConnectionIndex Index)>();
        foreach (WorkspaceMemberResolution resolution in InvestigationWorkspace.Resolve(full, workspace, cancellationToken))
        {
            if (!resolution.HoldsItsCapture)
            {
                unread.Add(new(resolution.Member.SessionId, $"it is {resolution.State.ToString().ToLowerInvariant()}: {resolution.Reason}"));
                continue;
            }

            try
            {
                SessionStore store = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(resolution.FullPath));
                members.Add((resolution.Member, SessionConnections.All(store, policy, cancellationToken)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                unread.Add(new(resolution.Member.SessionId, "it could not be read: " + exception.Message));
            }
        }

        int disjoint = 0;
        int loopback = 0;
        IReadOnlyList<WorkspaceAddressTranslation> translations = InvestigationWorkspace.TranslationsInForce(workspace);
        var found = new List<(WorkspaceConnection First, WorkspaceConnection Second, CandidateTiming Timing, string? Why, bool Confirmed,
            WorkspaceAddressTranslation[] Through)>();
        for (int first = 0; first < members.Count; first++)
        {
            for (int second = first + 1; second < members.Count; second++)
            {
                ILookup<(Mechanism, string, string), HeldConnection> mirrors = members[second].Index.Connections.ToLookup(
                    connection => (connection.Summary.Mechanism, connection.Summary.LocalEndpoint, connection.Summary.RemoteEndpoint));
                foreach (HeldConnection a in members[first].Index.Connections)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The other end's local endpoint is this one's remote, and its remote this one's local - as each is seen,
                    // or as a person stated a translation makes it (§8.3).
                    (string Endpoint, WorkspaceAddressTranslation? By)[] remotes =
                        [(a.Summary.RemoteEndpoint, null), .. InvestigationWorkspace.Translated(translations, a.Summary.RemoteEndpoint).Select(to => (to.Endpoint, (WorkspaceAddressTranslation?)to.By))];
                    (string Endpoint, WorkspaceAddressTranslation? By)[] locals =
                        [(a.Summary.LocalEndpoint, null), .. InvestigationWorkspace.Translated(translations, a.Summary.LocalEndpoint).Select(to => (to.Endpoint, (WorkspaceAddressTranslation?)to.By))];
                    var matched = new HashSet<HeldConnection>(ReferenceEqualityComparer.Instance);
                    foreach ((HeldConnection b, WorkspaceAddressTranslation[] through) in remotes.SelectMany(remote => locals.SelectMany(local =>
                        mirrors[(a.Summary.Mechanism, remote.Endpoint, local.Endpoint)].Select(b =>
                            (b, new[] { remote.By, local.By }.OfType<WorkspaceAddressTranslation>().ToArray())))))
                    {
                        if (!matched.Add(b))
                        {
                            continue;
                        }

                        bool looped = IsLoopback(a.Summary.LocalEndpoint) || IsLoopback(a.Summary.RemoteEndpoint);
                        if (looped && !InvestigationWorkspace.OneHost(workspace, members[first].Member.HostId, members[second].Member.HostId))
                        {
                            // A loopback address means the host it is on: two hosts' loopback connections are never one.
                            loopback++;
                            continue;
                        }

                        (CandidateTiming? timing, string? why) = Timing(workspace, members[first].Member.SessionId, a,
                            members[second].Member.SessionId, b);
                        if (timing is not { } known)
                        {
                            disjoint++;
                            continue;
                        }

                        found.Add((new(members[first].Member.SessionId, members[first].Member.HostId, a),
                            new(members[second].Member.SessionId, members[second].Member.HostId, b), known, why,
                            looped && members[first].Member.HostId != members[second].Member.HostId, through));
                    }
                }
            }
        }

        var uses = new Dictionary<(Guid, string), int>();
        foreach ((WorkspaceConnection first, WorkspaceConnection second, _, _, _, _) in found)
        {
            foreach (WorkspaceConnection end in new[] { first, second })
            {
                (Guid, string) key = (end.SessionId, end.Connection.Summary.Key);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        ConnectionCandidate[] candidates =
        [
            .. found.Select(pair =>
                {
                    WorkspaceJoin? decision = InvestigationWorkspace.DecisionFor(workspace,
                        new(pair.First.SessionId, pair.First.Connection.Summary.Key), new(pair.Second.SessionId, pair.Second.Connection.Summary.Key));
                    bool current = decision is null || InvestigationWorkspace.DecidedUnderCurrentTime(workspace, decision);
                    return new ConnectionCandidate(
                        pair.First,
                        pair.Second,
                        pair.Timing,
                        uses[(pair.First.SessionId, pair.First.Connection.Summary.Key)] - 1 + uses[(pair.Second.SessionId, pair.Second.Connection.Summary.Key)] - 1,
                        [.. Evidence(pair.First.Connection, pair.Second.Connection, pair.Timing, pair.Why, pair.Through),
                            .. (pair.Confirmed ? new[] { OneHostByConfirmation } : []), .. Decided(decision, current)],
                        decision,
                        current)
                    {
                        Translations = pair.Through,
                    };
                })
                .OrderBy(candidate => candidate.Timing)
                .ThenBy(candidate => candidate.Ambiguous)
                .ThenByDescending(candidate => candidate.First.Connection.Summary.Records + candidate.Second.Connection.Summary.Records),
        ];

        // A decision whose pair is no candidate now - its lifetimes moved apart, a session is not read - is said, not lost.
        HashSet<(Guid, string, Guid, string)> proposed = [.. candidates.Select(candidate => Key(candidate.Ends.First, candidate.Ends.Second))];
        HashSet<Guid> read = [.. members.Select(member => member.Member.SessionId)];
        UnmatchedDecision[] elsewhere =
        [
            .. InvestigationWorkspace.DecisionsInForce(workspace)
                .Where(join => !proposed.Contains(Key(join.First, join.Second)))
                .Select(join => new UnmatchedDecision(join,
                    !read.Contains(join.First.SessionId) || !read.Contains(join.Second.SessionId)
                        ? "a session of it was not compared"
                        : "its two connections are not a candidate now: their lifetimes lie apart, or one of them is gone")),
        ];
        return new(Rule, candidates, disjoint, loopback, unread,
        [
            "A candidate is never an established join: mirrored endpoints can be two connections behind an address "
                + "translation or a proxy, and a port used again later is another connection. It says what matches and why.",
            "Only connections whose other end their own capture does not hold are compared; one both of whose ends a capture "
                + "holds is that capture's own channel.",
            "Lifetimes are where each capture saw the connection, from its first record to its last, compared in the "
                + "investigation's time within their alignment's uncertainty; without an alignment they cannot be compared.",
        ], elsewhere);
    }

    /// <summary>What a person decided of a candidate, in words, and whether the investigation's time has changed since.</summary>
    private static string[] Decided(WorkspaceJoin? decision, bool current) => decision is null
        ? []
        :
        [
            (decision.Decision == WorkspaceJoinDecision.Accepted ? "Accepted as one connection by a person" : "Rejected by a person")
                + string.Create(CultureInfo.CurrentCulture, $" (join revision {decision.Revision})")
                + (current ? "." : ", under alignments since changed: review it."),
        ];

    private static (Guid, string, Guid, string) Key(WorkspaceJoinEnd a, WorkspaceJoinEnd b) =>
        (a.SessionId, a.Key).CompareTo((b.SessionId, b.Key)) <= 0 ? (a.SessionId, a.Key, b.SessionId, b.Key) : (b.SessionId, b.Key, a.SessionId, a.Key);

    /// <summary>
    /// Where two lifetimes stand: overlapping once each end is widened by its uncertainty, unknown when an end has no time
    /// or no known uncertainty, or null when they lie apart beyond it - which proposes nothing.
    /// </summary>
    private static (CandidateTiming? Timing, string? Why) Timing(
        InvestigationWorkspaceFile workspace,
        Guid firstSession,
        HeldConnection first,
        Guid secondSession,
        HeldConnection second)
    {
        WorkspaceInstant[] ends =
        [
            InvestigationWorkspace.Place(workspace, firstSession, first.FirstNanoseconds),
            InvestigationWorkspace.Place(workspace, firstSession, first.LastNanoseconds),
            InvestigationWorkspace.Place(workspace, secondSession, second.FirstNanoseconds),
            InvestigationWorkspace.Place(workspace, secondSession, second.LastNanoseconds),
        ];
        if (ends.FirstOrDefault(end => end.WorkspaceNanoseconds is null || end.Uncertainty is null) is { } unplaced)
        {
            string whose = unplaced.SessionId == firstSession ? "the first" : "the second";
            return (CandidateTiming.Unknown, unplaced.Why($"{whose} connection's session", CultureInfo.CurrentCulture) ?? "their time is unknown");
        }

        double Low(WorkspaceInstant end) => end.WorkspaceNanoseconds!.Value - end.Uncertainty!.Value.HalfWidthNanoseconds;
        double High(WorkspaceInstant end) => end.WorkspaceNanoseconds!.Value + end.Uncertainty!.Value.HalfWidthNanoseconds;
        bool overlap = Low(ends[0]) <= High(ends[3]) && Low(ends[2]) <= High(ends[1]);
        return overlap ? (CandidateTiming.Overlapping, null) : (null, null);
    }

    private static string[] Evidence(
        HeldConnection first,
        HeldConnection second,
        CandidateTiming timing,
        string? why,
        WorkspaceAddressTranslation[] through)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConnectionSummary a = first.Summary;
        ConnectionSummary b = second.Summary;
        string protocol = a.Mechanism == Mechanism.Udp ? "UDP" : "TCP";
        return
        [
            through.Length == 0
                ? $"Endpoints mirror: {a.LocalEndpoint} and {a.RemoteEndpoint}, {protocol}, each capture holding one end."
                : $"Endpoints mirror through a known translation: the first holds {a.LocalEndpoint} to {a.RemoteEndpoint}, and the "
                    + $"second {b.LocalEndpoint} to {b.RemoteEndpoint}, {protocol}.",
            .. through.Select(translation => string.Create(culture,
                $"A person stated that {translation.Seen} is {translation.Is} (translation revision {translation.Revision}); ")
                + "the join rests on that statement."),
            timing == CandidateTiming.Overlapping
                ? "Their lifetimes overlap in the investigation's time, within their uncertainty."
                : $"Their lifetimes cannot be compared: {why}.",
            Bytes("first", a.SentBytes, a.Sends - a.UnmeasuredSends, "second", b.ReceivedBytes, b.Receives - b.UnmeasuredReceives, culture),
            Bytes("second", b.SentBytes, b.Sends - b.UnmeasuredSends, "first", a.ReceivedBytes, a.Receives - a.UnmeasuredReceives, culture),
        ];
    }

    private static string Bytes(string sender, long sent, long sends, string receiver, long received, long receives, CultureInfo culture) =>
        sends == 0 && receives == 0
            ? $"Neither records a measured transfer from the {sender} to the {receiver}."
            : receives == 0
                ? string.Create(culture, $"The {sender} sent {sent:N0} B in {sends:N0} measured {(sends == 1 ? "transfer" : "transfers")}; ")
                    + $"the {receiver} recorded no measured receive of them."
            : sends == 0
                ? string.Create(culture, $"The {receiver} received {received:N0} B in {receives:N0} measured {(receives == 1 ? "transfer" : "transfers")}; ")
                    + $"the {sender} recorded no measured send of them."
            : string.Create(culture, $"The {sender} sent {sent:N0} B in {sends:N0} measured {(sends == 1 ? "transfer" : "transfers")}, ")
                + string.Create(culture, $"and the {receiver} received {received:N0} B in {receives:N0}")
                + (sent == received ? ": the same bytes." : ": not the same - one side lost or never recorded some.");

    /// <summary>The evidence line of a loopback candidate between two identities that are one host only by a person's word.</summary>
    public const string OneHostByConfirmation = "Its loopback endpoints name one host only because a person confirmed its two "
        + "captures' host identities are one host; the identities themselves differ.";

    private static bool IsLoopback(string endpoint) =>
        endpoint.StartsWith("127.", StringComparison.Ordinal) || endpoint.StartsWith("[::1]", StringComparison.Ordinal);
}
