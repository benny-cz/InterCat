using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// A row chosen among a process's rows is the selection wherever the selection is read (§6.4, I5): the inspector describes
/// it, so the evidence card counts its records, the timeline highlights them and E lists them - a paired channel's, a
/// one-sided connection's, an RPC channel's calls' or the process's HTTP exchanges' - rather than the process's own. Opened,
/// its own rung's card counts it alike, since E lists its records from there too.
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
            return CallRecords(row.Source.ObservationCount);
        }

        if (HttpExchangeKeys.IsHttp(row.Key))
        {
            return BufferRecords(row.Source.ObservationCount, row.KnownBytes);
        }

        if (TransportConnection.IsKey(row.Key))
        {
            return Spoken.Count(row.Source.ObservationCount, "record") + " of one end, whose other end no record holds · "
                + row.KnownBytes;
        }

        return Snapshot.Channels.FirstOrDefault(channel => string.Equals(channel.Key, row.Key, StringComparison.Ordinal))
            is { } paired
            ? PairedRecords(paired)
            : Spoken.Count(row.Source.ObservationCount, "record");
    }

    /// <summary>
    /// On a channel's own rung, the channel it opened, as the card heads and counts it: the records E lists from there and
    /// the level line counts (§6.4, I5), in the words its row used where it was chosen, so opening a channel does not turn
    /// the card back to the process it was opened from. Null at any other rung, in the tour, and while several processes
    /// are chosen there, which the card then counts and E lists, as it does a chosen relationship before either.
    /// </summary>
    private (string Heading, string Summary)? OpenedChannel =>
        !realOverview || HasMultiSelection ? null
        : IsRpcChannelRung
            ? ("This RPC channel", rpcCalls?.Channel is { } calls ? CallRecords(calls.Records) : RpcCallSummary(brief: true))
        : IsHttpChannelRung
            ? ("These HTTP exchanges", httpExchanges?.Channel is { } exchanges
                ? BufferRecords(exchanges.Records, HttpBytes(exchanges))
                : HttpExchangeSummary(brief: true))
        : FocusedRealChannel is { } paired ? ("This channel", PairedRecords(paired))
        : null;

    /// <summary>An RPC channel's call records, which carry no size.</summary>
    private static string CallRecords(long records) => Spoken.Count(records, "call record") + " · an RPC call carries no size";

    /// <summary>HTTP exchanges' buffer records, with what their messages held.</summary>
    private static string BufferRecords(long records, string bytes) => Spoken.Count(records, "buffer record") + " · " + bytes;

    /// <summary>A paired channel's records at its two ends, and the bytes sent across it, as its own rung states them.</summary>
    private string PairedRecords(Channel paired) =>
        Spoken.Count(paired.ObservationCount, "observed record") + " at its two ends · " + FocusedChannelBytes(paired);
}
