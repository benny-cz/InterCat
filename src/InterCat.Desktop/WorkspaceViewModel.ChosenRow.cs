using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// A row chosen among a process's rows is the selection wherever the selection is read (§6.4, I5): the inspector describes
/// it, so the evidence card counts its records, the timeline highlights them and E lists them - a paired channel's, a
/// one-sided connection's, an RPC channel's calls' or the process's HTTP exchanges' - rather than the process's own.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// The row the inspector describes among a published session's process rows, and the scope that names its records, which
    /// Enter on it opens or leads to; null for any other selection, and in the tour, whose rows illustrate.
    /// </summary>
    private (RungRow Row, LadderTarget Records)? ChosenRow =>
        realOverview && ladder.Current.Level == DetailLevel.ProcessInstance && DescribedRow is { } row
        && (row.Source.DescendsTo == DetailLevel.Channel || TransportConnection.IsKey(row.Key))
            ? (row, new(DetailLevel.Channel, row.Key, row.Source.Label))
            : null;

    /// <summary>What a chosen row is called where the card heads its records and E's filter says why it applies.</summary>
    private static string ChosenRowNoun(RungRow row) => RpcChannelKeys.IsRpc(row.Key) ? "RPC channel"
        : HttpExchangeKeys.IsHttp(row.Key) ? "HTTP exchanges"
        : TransportConnection.IsKey(row.Key) ? "connection"
        : "channel";

    /// <summary>
    /// The chosen row's records as a timeline focus: an RPC channel's or HTTP exchanges' by their own key, a channel's or a
    /// one-sided connection's by its key, which is what E reads for it.
    /// </summary>
    private static TimelineFocus ChosenRowFocus(RungRow row) =>
        RpcChannelKeys.IsRpc(row.Key) || HttpExchangeKeys.IsHttp(row.Key) ? new(null, [], row.Key) : new(row.Key, []);

    /// <summary>
    /// The card's count of the chosen row's records, as its rung row counts them over the scope the rows count, and what
    /// they measured: a paired channel's records at its two ends and the bytes sent across it, as its own rung states
    /// them; an RPC channel's call records, which carry no size; a connection's or the HTTP exchanges' bytes as their row
    /// states them.
    /// </summary>
    private string ChosenRowEvidence(RungRow row)
    {
        if (RpcChannelKeys.IsRpc(row.Key))
        {
            return Spoken.Count(row.Source.ObservationCount, "call record") + " · an RPC call carries no size";
        }

        if (HttpExchangeKeys.IsHttp(row.Key))
        {
            return Spoken.Count(row.Source.ObservationCount, "buffer record") + " · " + row.KnownBytes;
        }

        if (TransportConnection.IsKey(row.Key))
        {
            return Spoken.Count(row.Source.ObservationCount, "record") + " of one end, whose other end no record holds · "
                + row.KnownBytes;
        }

        return Snapshot.Channels.FirstOrDefault(channel => string.Equals(channel.Key, row.Key, StringComparison.Ordinal))
            is { } paired
            ? Spoken.Count(paired.ObservationCount, "observed record") + " at its two ends · " + FocusedChannelBytes(paired)
            : Spoken.Count(row.Source.ObservationCount, "record");
    }
}
