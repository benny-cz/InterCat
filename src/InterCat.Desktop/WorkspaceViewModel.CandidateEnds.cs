using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A session's other hosts' candidate other ends (§8.3, M4, P6), for a session opened from an investigation it is a
/// member of: read in the background from the investigation's candidate joins, under the evidence policy the session is
/// counted by, and said where a host is described - its row in Other hosts' list, its card and its description. A
/// candidate is never an established join. Candidate joins compare every member's connections, so a later publication of
/// the session takes over the read, done or still running, and reads again only when it is counted under another policy.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private IReadOnlyDictionary<string, CandidateHost> candidateHosts = new Dictionary<string, CandidateHost>();
    private CandidateHostsRead? candidateRead;

    /// <summary>Completes when the latest read of the investigation's candidate other ends has applied, been superseded or failed.</summary>
    public Task CandidateHostsReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads, in the background, the candidate other ends <paramref name="investigation"/> proposes for this session's
    /// hosts, under the policy this workspace counts by. An investigation that cannot be read proposes none.
    /// </summary>
    public void ReadCandidateHosts(string investigation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigation);
        if (disposed || evidenceSource is not { } source)
        {
            return;
        }

        Follow(new CandidateHostsRead(investigation, source.SessionId, EvidencePolicy));
    }

    /// <summary>
    /// This workspace's read of its candidate other ends, done or still running, for the next publication of the session to
    /// take over: given up here, so this workspace's end does not cancel it. Null when it reads none.
    /// </summary>
    public CandidateHostsRead? CarryCandidateHosts()
    {
        CandidateHostsRead? read = candidateRead;
        candidateRead = null;
        return read;
    }

    /// <summary>
    /// Takes over an earlier publication's read of this session's candidate other ends: shown with this workspace's first
    /// rows when it is done, as soon as it is otherwise, and read again from its investigation when this publication is
    /// counted under another policy. A read for another session is let go.
    /// </summary>
    public void AdoptCandidateHosts(CandidateHostsRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (disposed || evidenceSource is not { } source || read.SessionId != source.SessionId)
        {
            read.Dispose();
            return;
        }

        if (read.Policy != EvidencePolicy)
        {
            read.Dispose();
            ReadCandidateHosts(read.Investigation);
            return;
        }

        Follow(read);
    }

    /// <summary>The candidate other ends proposed for the connections to <paramref name="address"/>; null where none is.</summary>
    private CandidateHost? CandidatesAt(string address) => candidateHosts.GetValueOrDefault(address);

    private void Follow(CandidateHostsRead read)
    {
        CancelCandidateRead();
        candidateRead = read;
        if (read.Hosts.IsCompletedSuccessfully)
        {
            ShowCandidateHosts(read.Hosts.Result);
            CandidateHostsReady = Task.CompletedTask;
            return;
        }

        CandidateHostsReady = ShowWhenReadAsync(read);
    }

    private async Task ShowWhenReadAsync(CandidateHostsRead read)
    {
        IReadOnlyList<CandidateHost> hosts;
        try
        {
            hosts = await read.Hosts.AnsweredLater();
        }
        catch (OperationCanceledException) when (read.Cancelled)
        {
            // A newer read, or the view's end, superseded this one.
            return;
        }

        if (disposed || !ReferenceEquals(candidateRead, read)) return;
        ShowCandidateHosts(hosts);
    }

    private void ShowCandidateHosts(IReadOnlyList<CandidateHost> hosts)
    {
        candidateHosts = hosts.ToDictionary(host => host.Address, StringComparer.OrdinalIgnoreCase);
        RaiseHostListChanged();
        OnPropertyChanged(nameof(SelectionSubtitle));
    }

    private void CancelCandidateRead()
    {
        candidateRead?.Dispose();
        candidateRead = null;
    }
}

/// <summary>
/// One read of the candidate other ends an investigation proposes for one member's other hosts (§8.3), under one evidence
/// policy, running in the background from the moment it is made; a later publication of the session takes it over, and
/// disposing it lets it go.
/// </summary>
public sealed class CandidateHostsRead : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();

    internal CandidateHostsRead(string investigation, Guid sessionId, EvidencePolicy policy)
    {
        Investigation = investigation;
        SessionId = sessionId;
        Policy = policy;
        CancellationToken token = cancellation.Token;
        Hosts = Task.Run(() => Find(investigation, sessionId, policy, token), token);
    }

    /// <summary>The investigation the candidates are read from.</summary>
    public string Investigation { get; }

    /// <summary>The member whose hosts they are proposed for.</summary>
    public Guid SessionId { get; }

    /// <summary>The policy the member's connections are counted by, as its view counts them.</summary>
    public EvidencePolicy Policy { get; }

    /// <summary>The hosts with candidate other ends, once read; none where the investigation could not be read.</summary>
    internal Task<IReadOnlyList<CandidateHost>> Hosts { get; }

    /// <summary>Whether the read was let go.</summary>
    internal bool Cancelled => cancellation.IsCancellationRequested;

    /// <summary>Lets the read go; what it finds afterwards is shown nowhere.</summary>
    public void Dispose()
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static IReadOnlyList<CandidateHost> Find(string investigation, Guid sessionId, EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            return InvestigationHosts.Of(InvestigationWorkspace.Read(investigation),
                WorkspaceCorrelation.Candidates(investigation, policy, cancellationToken), sessionId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            // An investigation that cannot be read proposes no other end, and nothing is said of the ends it would have.
            return [];
        }
    }
}
