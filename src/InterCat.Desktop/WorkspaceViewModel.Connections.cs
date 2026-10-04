using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A process's one-sided connections on its rung (§7.1): the TCP connections and UDP flows it held whose other end no
/// record of the capture holds - most often another host's - beside its paired channels, each opening its own records. A
/// real capture's busiest processes talk mostly to other hosts, and without these their rung said nothing of it.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private ConnectionsLoad? connections;

    /// <summary>How many one-sided connections the process rung lists.</summary>
    private int ConnectionCount => connections?.Connections.Count ?? 0;

    /// <summary>
    /// The connection rows as the rung shows them now: built when read, so each ranks by the metric and rate the rung ranks
    /// by at that moment.
    /// </summary>
    private IReadOnlyList<RungRow> ConnectionRows()
    {
        if (connections is not { } load || load.Connections.Count == 0)
        {
            return [];
        }

        ThemeMode mode = ThemeResources.CurrentMode;
        return [.. load.Connections.Select(connection => ConnectionRungRow(connection, mode))];
    }

    /// <summary>Completes when the latest read of a process's one-sided connections has applied or reported a problem.</summary>
    public Task ConnectionsReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts the read the process rung needs: its one-sided connections, when it holds transport records. Any other rung
    /// lets it go.
    /// </summary>
    private void SyncConnections()
    {
        if (evidenceSource is not null
            && ladder.Current.Level == DetailLevel.ProcessInstance
            && ladder.Current.Focus is { } focus
            && wholeSnapshot.Processes.FirstOrDefault(process => process.Id.ToString() == focus.Key) is { } process
            && process.Activity.Any(count => count.Mechanism is Mechanism.Tcp or Mechanism.Udp && count.Records > 0))
        {
            if (connections is { } known && known.Instance == process.Id && known.Scope == CountedScope)
            {
                return;
            }

            // The previous scope's rows stay on screen until this scope's are read, rather than blinking empty (§6.4).
            IReadOnlyList<ConnectionSummary> previous = connections is { } shown && shown.Instance == process.Id ? shown.Connections : [];
            CancelConnections();
            var load = new ConnectionsLoad(process.Id, CountedScope) { Connections = previous };
            connections = load;
            RebuildConnectionRows();
            ConnectionsReady = LoadConnectionsAsync(load);
            return;
        }

        CancelConnections();
    }

    /// <summary>Re-reads the process's connections when the scope the rung counts changed, so they count what the rows beside them count.</summary>
    private void SyncConnectionsScope()
    {
        if (ladder.Current.Level == DetailLevel.ProcessInstance && connections is { } load && load.Scope != CountedScope)
        {
            SyncConnections();
        }
    }

    private async Task LoadConnectionsAsync(ConnectionsLoad load)
    {
        load.Loading = true;
        RaiseRpcChanged();
        try
        {
            ConnectionList list = await evidenceSource!.ConnectionsAsync(load.Instance, load.Scope, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(connections, load)) return;
            load.Connections = list.Connections;
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(connections, load)) return;
            load.Problem = "This process's connections could not be read: " + exception.Message;
        }
        finally
        {
            load.Loading = false;
        }

        RebuildConnectionRows();
    }

    /// <summary>Announces the connection rows as read, keeping the selected row when it is still listed.</summary>
    private void RebuildConnectionRows()
    {
        if (disposed)
        {
            return;
        }

        string? selectedKey = selectedRung?.Key;
        RaiseRpcChanged();
        if (selectedKey is not null && RungRows.FirstOrDefault(row => row.Key == selectedKey) is { } kept)
        {
            selectedRung = kept;
            OnPropertyChanged(nameof(SelectedRung));
            RaiseSelectionDescribed();
        }
    }

    /// <summary>
    /// One connection's row. The rail is narrow, so it leads with the other end's endpoint; the source's own endpoint, how
    /// the capture saw it open and close, and its bytes follow. Enter opens its records: a transfer has no operation rung.
    /// </summary>
    private RungRow ConnectionRungRow(ConnectionSummary connection, ThemeMode mode)
    {
        FamilyTokens tokens = ThemePalette.TokensFor(mode, ThemePalette.FamilyOf(connection.Mechanism));
        string protocol = connection.Mechanism == Mechanism.Udp ? "UDP" : "TCP";
        string detail = $"{protocol} from {connection.LocalEndpoint} · {connection.Lifetime} · {connection.Transfers(CultureInfo.CurrentCulture)}";
        RankedValue? ranked = ShownMeasures is SessionByteMeasures ? connection.RankedBy(rankBy) : null;
        var source = new LadderRow(connection.Key, connection.Name, detail, connection.Records, null, connection.Mechanism,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner)
        {
            Ranked = ranked,
        };
        bool perSecond = RateSeconds is > 0 && ranked is not null;
        return new(connection.Key, "→ " + connection.RemoteEndpoint, detail, connection.Records.ToString("N0", CultureInfo.CurrentCulture),
            connection.Transfers(CultureInfo.CurrentCulture), tokens.Label, tokens.Glyph, string.Empty,
            NavigationState.Name(DetailLevel.Evidence), source)
        {
            SpokenLabel = connection.Name,
            SpokenName = $"{connection.Name}, {detail}, {Spoken.Count(connection.Records, "record")}. No record of this capture "
                + "holds its other end. Press Enter to open its records.",
            RankedFigure = ranked is null ? null
                : perSecond ? LadderRowBuilder.RateFigure(source, RateSeconds!.Value) : LadderRowBuilder.RankedFigure(ranked),
            RankedSpoken = ranked is null ? null
                : perSecond ? LadderRowBuilder.RateSpoken(source, RateSeconds!.Value) : LadderRowBuilder.RankedSpoken(ranked),
        };
    }

    private void CancelConnections()
    {
        if (connections is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        connections = null;
    }

    /// <summary>One process's one-sided connections as read for its rung.</summary>
    private sealed class ConnectionsLoad(ProcessInstanceId instance, TimeRange? scope)
    {
        public ProcessInstanceId Instance { get; } = instance;

        /// <summary>The interval the connections are counted in, or the whole session (null).</summary>
        public TimeRange? Scope { get; } = scope;

        public IReadOnlyList<ConnectionSummary> Connections { get; set; } = [];

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
