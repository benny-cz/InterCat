using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// Other hosts' list under a byte ranking (§6.3, R21): while the rail ranks by the bytes sent, received or both, the hosts
/// Other hosts lists are read for what their connections carried and ranked by it, as the processes beside them are, each
/// saying both directions' bytes. A host whose transfers stated no size follows those that did, never as a zero; until
/// the bytes are read, or where they cannot be, the list keeps the busiest first by records and says why. A live session's
/// next publication ranks it by what the last one read, said to be updating, until it has read its own, so its rows do not
/// move back to records and again at every publication.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private HostTrafficLoad? hostTraffic;

    // Other hosts' traffic as an earlier publication of the session read it, ranking the list while this one's is read.
    private HostTrafficCarry? carriedHostTraffic;

    /// <summary>Completes when the latest read of Other hosts' traffic has applied, been superseded or failed.</summary>
    public Task HostTrafficReady { get; private set; } = Task.CompletedTask;

    /// <summary>The byte ranking Other hosts' list follows: the rail's, while Other hosts is chosen in a session; null otherwise.</summary>
    private RankingMetric? HostByteRanking => evidenceSource is not null && SelectedCluster is { Kind: GraphNodeKind.OtherHosts }
        && RankingMetrics.FamilyOf(rankBy) == RankingFamily.Bytes
            ? rankBy
            : null;

    /// <summary>The traffic read for this drawing under the ranking the list follows; null while none is read for it.</summary>
    private HostTrafficLoad? CurrentHostTraffic => HostByteRanking is { } metric && hostTraffic is { } load
        && ReferenceEquals(load.Display, graphDisplay) && load.Metric == metric
            ? load
            : null;

    /// <summary>
    /// An earlier publication's traffic of Other hosts' hosts under the byte ranking the list follows, while this generation
    /// reads its own: it ranks the list meanwhile, said to be updating. None once this one's is in, while none is read, or
    /// once its read failed.
    /// </summary>
    private IReadOnlyList<HostTraffic>? StandInHostTraffic => HostByteRanking is { } metric
        && CurrentHostTraffic is { Hosts: null, Problem: null } && carriedHostTraffic is { } earlier && earlier.Metric == metric
            ? earlier.Hosts
            : null;

    /// <summary>
    /// The hosts Other hosts' list holds, in its order: by what their connections carried once a byte ranking's read is in,
    /// or while it is read, by what an earlier publication read, each with it; else the busiest first by records, as the
    /// census counts them. A host the traffic does not hold follows those it does.
    /// </summary>
    private IReadOnlyList<(OtherHost Host, HostTraffic? Traffic)> RankedHosts()
    {
        IReadOnlyList<OtherHost> hosts = ListedHosts();
        if ((CurrentHostTraffic?.Hosts ?? StandInHostTraffic) is not { } traffic)
        {
            return [.. hosts.Select(host => (host, (HostTraffic?)null))];
        }

        // Each host as the census counts it, beside its bytes; a host the read found nothing of keeps its place after them.
        Dictionary<string, OtherHost> census = hosts.ToDictionary(host => host.Address, StringComparer.OrdinalIgnoreCase);
        HashSet<string> read = new(traffic.Select(host => host.Host.Address), StringComparer.OrdinalIgnoreCase);
        return
        [
            .. traffic.Where(host => census.ContainsKey(host.Host.Address))
                .Select(host => (census[host.Host.Address], (HostTraffic?)host)),
            .. hosts.Where(host => !read.Contains(host.Address)).Select(host => (host, (HostTraffic?)null)),
        ];
    }

    /// <summary>
    /// What the list's heading adds of its ranking: under a byte ranking, that its hosts are ranked by it, that their bytes
    /// are being read, or that they could not be.
    /// </summary>
    private string HostRankingWords(int count)
    {
        string busiest = count <= HostsListed
            ? string.Create(CultureInfo.CurrentCulture, $"Its {count:N0} hosts, the busiest first")
            : string.Create(CultureInfo.CurrentCulture, $"Its busiest {HostsListed:N0} of {count:N0} hosts");
        if (HostByteRanking is not { } metric)
        {
            return busiest;
        }

        string phrase = RankingMetrics.Phrase(metric);
        string most = count <= HostsListed
            ? string.Create(CultureInfo.CurrentCulture, $"Its {count:N0} hosts, the most {phrase} first")
            : string.Create(CultureInfo.CurrentCulture, $"The {HostsListed:N0} of its {count:N0} hosts with the most {phrase}");
        return CurrentHostTraffic switch
        {
            { Hosts: not null } => most,
            { Problem: not null } => busiest + $"; their {phrase} could not be read",
            _ when StandInHostTraffic is not null => most + " · updating",
            _ => busiest + $", while their {phrase} are read",
        };
    }

    /// <summary>Why the list's bytes could not be read, beneath it; empty while they are read or were.</summary>
    private string HostTrafficProblem => CurrentHostTraffic?.Problem is { } problem
        ? $"Their {RankingMetrics.Phrase(HostByteRanking!.Value)} could not be read: {problem.TrimEnd('.')}."
        : string.Empty;

    /// <summary>
    /// Reads what Other hosts' hosts carried when the list follows a byte ranking and none is read for this drawing and
    /// ranking; anything else lets a read go.
    /// </summary>
    private void FollowHostTraffic()
    {
        if (disposed || HostByteRanking is not { } metric)
        {
            CancelHostTraffic();
            return;
        }

        if (hostTraffic is { } known && ReferenceEquals(known.Display, graphDisplay) && known.Metric == metric)
        {
            return;
        }

        CancelHostTraffic();
        var load = new HostTrafficLoad(graphDisplay, metric);
        hostTraffic = load;
        HostTrafficReady = LoadHostTrafficAsync(load, HubLinks(graphDisplay));
    }

    private async Task LoadHostTrafficAsync(HostTrafficLoad load, IReadOnlyCollection<HostLink> links)
    {
        try
        {
            IReadOnlyList<HostTraffic> hosts = await evidenceSource!.HostTrafficAsync(links, load.Metric, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(hostTraffic, load)) return;
            load.Hosts = hosts;
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(hostTraffic, load)) return;

            // This generation's answer replaces what an earlier publication read even when its read failed.
            load.Problem = exception.Message;
            carriedHostTraffic = null;
        }

        RaiseHostListChanged();
    }

    /// <summary>Other hosts' traffic as this generation read it for the list, or else as an earlier publication did.</summary>
    private HostTrafficCarry? CarryHostTraffic() => evidenceSource is { } source && hostTraffic is { Hosts: { } hosts } read
        ? new(source.SessionId, read.Metric, hosts)
        : carriedHostTraffic;

    /// <summary>Takes on what an earlier publication of this session read of Other hosts' hosts, ranking the list by it.</summary>
    private void AdoptHostTraffic(HostTrafficCarry? carry, Guid session)
    {
        carriedHostTraffic = carry is { } earlier && earlier.SessionId == session ? earlier : null;
        if (carriedHostTraffic is not null)
        {
            RaiseHostListChanged();
        }
    }

    private void CancelHostTraffic()
    {
        if (hostTraffic is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        hostTraffic = null;
    }

    /// <summary>Other hosts' traffic as read for one drawing under one byte ranking.</summary>
    private sealed class HostTrafficLoad(GraphDisplay display, RankingMetric metric)
    {
        public GraphDisplay Display { get; } = display;

        public RankingMetric Metric { get; } = metric;

        public IReadOnlyList<HostTraffic>? Hosts { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
