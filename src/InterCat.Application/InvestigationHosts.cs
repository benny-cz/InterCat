using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A process of another member's capture that candidate joins propose as the other end of one member's connections to
/// one other host (§8.3, P6): the capture and host it was recorded on, the process, how many of the connections it
/// mirrors, and whether a person accepted every one of those joins. A candidate is never an established join, and an
/// accepted one is a person's manual join, never evidence.
/// </summary>
public sealed record CandidateEnd(
    Guid SessionId,
    Guid HostId,
    string? HostAlias,
    string? Process,
    int ProcessId,
    int Connections,
    bool Accepted)
{
    /// <summary>The host it was recorded on, by the name a person gave it, else by its capture's session: "demo server".</summary>
    public string Host => HostAlias
        ?? "capture " + SessionId.ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <summary>The process in words: "api.exe · PID 5100", and that a person accepted its joins where they did.</summary>
    public string ProcessWords => string.Create(CultureInfo.CurrentCulture, $"{Process ?? "a process"} · PID {ProcessId}")
        + (Accepted ? ", accepted" : string.Empty);

    /// <summary>
    /// Candidate other ends in words, host by host: "demo server: api.exe · PID 5100 and files.exe · PID 5300; capture
    /// 1a2b3c4d: updater.exe · PID 310".
    /// </summary>
    public static string Listed(IEnumerable<CandidateEnd> ends)
    {
        ArgumentNullException.ThrowIfNull(ends);
        return string.Join("; ", ends
            .GroupBy(end => end.Host, StringComparer.Ordinal)
            .Select(host => host.Key + ": " + WorkspaceLayout.Series([.. host.Select(end => end.ProcessWords)])));
    }
}

/// <summary>One of a member's other hosts, with the candidate other ends its investigation proposes for its connections.</summary>
public sealed record CandidateHost(string Address, IReadOnlyList<CandidateEnd> Ends)
{
    /// <summary>
    /// Its candidate other ends in words, host by host: "demo server: api.exe · PID 5100 and files.exe · PID 5300".
    /// </summary>
    public string Words => CandidateEnd.Listed(Ends);
}

/// <summary>
/// The candidate other ends an investigation proposes for one member's connections to other hosts (§8.3, M4, P6): where a
/// candidate join mirrors such a connection in another member's capture, that capture's process is a candidate for the
/// host's other end. Its address names no one (P7), and a candidate is never established: mirrored endpoints can be two
/// connections behind an address translation, and a port used again is another connection.
/// </summary>
public static class InvestigationHosts
{
    /// <summary>
    /// The other hosts of member <paramref name="sessionId"/> whose connections a candidate join mirrors in another member's
    /// capture, each with its candidate other ends: the most candidate connections first, then by address; a host's ends the
    /// most connections first, then by host and process. A candidate a person rejected proposes no end.
    /// </summary>
    public static IReadOnlyList<CandidateHost> Of(
        InvestigationWorkspaceFile workspace,
        WorkspaceCorrelationResult correlation,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(correlation);
        Dictionary<Guid, string> aliases = workspace.HostAliases.ToDictionary(alias => alias.HostId, alias => alias.Alias);
        var ends = new List<(string Address, WorkspaceConnection Other, bool Accepted)>();
        foreach (ConnectionCandidate candidate in correlation.Candidates)
        {
            if (candidate.Decision?.Decision == WorkspaceJoinDecision.Rejected)
            {
                continue;
            }

            (WorkspaceConnection Own, WorkspaceConnection Other)? pair = candidate.First.SessionId == sessionId
                ? (candidate.First, candidate.Second)
                : candidate.Second.SessionId == sessionId ? (candidate.Second, candidate.First) : null;
            if (pair is { } found)
            {
                ends.Add((HostScopeKeys.EndpointAddress(found.Own.Connection.Summary.RemoteEndpoint), found.Other,
                    candidate.Decision?.Decision == WorkspaceJoinDecision.Accepted));
            }
        }

        return [.. ends
            .GroupBy(end => end.Address, StringComparer.OrdinalIgnoreCase)
            .Select(host => new CandidateHost(host.Key, Ends(host.Select(end => (end.Other, end.Accepted)), aliases)))
            .OrderByDescending(host => host.Ends.Sum(end => end.Connections))
            .ThenBy(host => host.Address, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The candidate other ends an investigation proposes for the connections member <paramref name="sessionId"/>'s
    /// processes accepted, listener by listener: for each process and port of its own a connection a candidate join
    /// mirrors was accepted at, the processes of other members' captures whose connections mirror those - which process on
    /// another machine connected to which listener here, as far as the captures' endpoints say - the most connections
    /// first, then by host and process. A candidate a person rejected proposes no end.
    /// </summary>
    public static IReadOnlyDictionary<(ProcessInstanceId Process, ushort Port), IReadOnlyList<CandidateEnd>> ForListeners(
        InvestigationWorkspaceFile workspace,
        WorkspaceCorrelationResult correlation,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(correlation);
        Dictionary<Guid, string> aliases = workspace.HostAliases.ToDictionary(alias => alias.HostId, alias => alias.Alias);
        var ends = new List<((ProcessInstanceId Process, ushort Port) Listener, WorkspaceConnection Other, bool Accepted)>();
        foreach (ConnectionCandidate candidate in correlation.Candidates)
        {
            if (candidate.Decision?.Decision == WorkspaceJoinDecision.Rejected)
            {
                continue;
            }

            (WorkspaceConnection Own, WorkspaceConnection Other)? pair = candidate.First.SessionId == sessionId
                ? (candidate.First, candidate.Second)
                : candidate.Second.SessionId == sessionId ? (candidate.Second, candidate.First) : null;
            if (pair is { } found && found.Own.Connection.Summary.Opening == ConnectionOpening.Accepted
                && ConnectionOpens.PortOf(found.Own.Connection.Summary.LocalEndpoint) is { } port)
            {
                ends.Add(((found.Own.Connection.Holder.Id, port), found.Other,
                    candidate.Decision?.Decision == WorkspaceJoinDecision.Accepted));
            }
        }

        return ends
            .GroupBy(end => end.Listener)
            .ToDictionary(listener => listener.Key, listener => Ends(listener.Select(end => (end.Other, end.Accepted)), aliases));
    }

    /// <summary>
    /// Mirroring connections' processes as candidate other ends, each process once with how many of them it holds, the most
    /// first, then by host and process.
    /// </summary>
    private static IReadOnlyList<CandidateEnd> Ends(IEnumerable<(WorkspaceConnection Other, bool Accepted)> ends,
        Dictionary<Guid, string> aliases) =>
        [.. ends
            .GroupBy(end => (end.Other.SessionId, end.Other.HostId, end.Other.Connection.Holder.Id))
            .Select(process =>
            {
                WorkspaceConnection other = process.First().Other;
                return new CandidateEnd(other.SessionId, other.HostId, aliases.GetValueOrDefault(other.HostId),
                    other.Connection.Holder.ImageName, other.Connection.Holder.ProcessId, process.Count(),
                    process.All(end => end.Accepted));
            })
            .OrderByDescending(end => end.Connections)
            .ThenBy(end => end.Host, StringComparer.Ordinal)
            .ThenBy(end => end.Process, StringComparer.OrdinalIgnoreCase)
            .ThenBy(end => end.ProcessId)];
}
