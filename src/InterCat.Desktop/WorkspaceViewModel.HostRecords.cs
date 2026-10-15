using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A host Other hosts was opened into, chosen (§6.3, §6.4, I5): its records are those of every connection to its address,
/// whichever process held it, as its scope reads them (<see cref="HostScopeKeys"/>). The timeline highlights them, the
/// inspector's card counts them and what they measured over the scope the rows count, and E lists them. Who is at the
/// address is not observed (P7).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private HostRecordsLoad? hostRecords;

    /// <summary>The host drawn on its own that is chosen in the graph; null for any other selection.</summary>
    private GraphDisplayNode? SelectedHost => SelectedCluster is { Kind: GraphNodeKind.Host } host ? host : null;

    /// <summary>Whether this workspace reads a chosen host's records: a real session with the directory to read them from.</summary>
    private bool ReadsHostRecords => realOverview && evidenceSource is not null;

    /// <summary>Completes when the latest read of a chosen host's connections has applied, been superseded or failed.</summary>
    public Task HostRecordsReady { get; private set; } = Task.CompletedTask;

    /// <summary>A host's records as E reads them and the timeline highlights them: its scope's key, named by its address.</summary>
    private static LadderTarget HostRecordsTarget(string address) =>
        new(DetailLevel.Channel, HostScopeKeys.Of(address), "Connections to " + address);

    /// <summary>
    /// The card's count of the chosen host's records over the scope the rows count, and what they measured: "11 records of
    /// 2 TCP connections from 2 processes · 1,210 B sent, no receive recorded". Until they are read, it says so.
    /// </summary>
    private string HostEvidence(GraphDisplayNode host)
    {
        if (hostRecords is not { } load || load.Address != host.Label || load.Scope != CountedScope || load.List is null && load.Problem is null)
        {
            return "Reading the records of its connections…";
        }

        if (load.Problem is { } problem)
        {
            return "Its records could not be read: " + problem;
        }

        HostConnectionList list = load.List!;
        return list.Connections.Count == 0
            ? "No record of a connection to it" + (load.Scope is null ? string.Empty : " in this interval")
            : Spoken.Count(list.Records, "record") + " of " + HostLinkText.Reached(list.Host) + " · "
                + list.Transfers(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Reads the chosen host's connections for the scope the rows count when none are read for it, so the card counts what
    /// E lists; any other selection lets a read go.
    /// </summary>
    private void FollowHostRecords()
    {
        if (disposed || !ReadsHostRecords || SelectedHost is not { } host)
        {
            CancelHostRecords();
            return;
        }

        if (hostRecords is { } known && known.Address == host.Label && known.Scope == CountedScope)
        {
            return;
        }

        CancelHostRecords();
        var load = new HostRecordsLoad(host.Label, CountedScope);
        hostRecords = load;
        HostRecordsReady = LoadHostRecordsAsync(load);
    }

    private async Task LoadHostRecordsAsync(HostRecordsLoad load)
    {
        try
        {
            HostConnectionList list = await evidenceSource!.HostConnectionsAsync(load.Address, load.Scope, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(hostRecords, load)) return;
            load.List = list;
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(hostRecords, load)) return;
            load.Problem = exception.Message;
        }

        OnPropertyChanged(nameof(EvidenceSummary));
    }

    private void CancelHostRecords()
    {
        if (hostRecords is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        hostRecords = null;
    }

    /// <summary>One host's connections as read for the card, over the scope the rows counted when the read began.</summary>
    private sealed class HostRecordsLoad(string address, TimeRange? scope)
    {
        public string Address { get; } = address;

        /// <summary>The interval the connections are counted in, or the whole session (null).</summary>
        public TimeRange? Scope { get; } = scope;

        public HostConnectionList? List { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
