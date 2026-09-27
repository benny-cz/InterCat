using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The interval table's bytes on a real session (§6.2's table equivalent, R15, R21). The timeline counts records and sums
/// no bytes (`overview-index-v1` §4), so once the table is shown the bytes of the rows it lists are read: what each
/// interval's records sent and received, over exactly the records its count counts, those that recorded no size stated
/// apart. Until they arrive each row says they are being read; a table that is not shown reads nothing.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The bytes of the rows as last read, the read in flight for the rows listed now, and the listing whose read failed.
    private SessionIntervalByteMeasures? intervalBytes;
    private (IntervalBytesRequest Request, CancellationTokenSource Cancellation)? intervalBytesRead;
    private (IntervalBytesRequest Request, string Problem)? intervalBytesFailure;

    // What the table lists now, and whether a focus's count stands beside each row's own.
    private IntervalBytesRequest? listedIntervalBytes;
    private bool intervalRowsBesideFocus;

    /// <summary>Completes when the most recent read of the interval table's bytes has applied, been superseded or failed.</summary>
    public Task IntervalBytesReady { get; private set; } = Task.CompletedTask;

    /// <summary>The records one listing of the interval table counts, in the columns it lists them in.</summary>
    private readonly record struct IntervalBytesRequest(TimeRange Interval, int Columns, IntervalByteScope Scope)
    {
        public bool IsAnsweredBy(SessionIntervalByteMeasures measures) =>
            measures.Interval == Interval && measures.Columns.Count == Columns && measures.Scope == Scope;
    }

    /// <summary>
    /// The records a lane lists, as a byte scope. The rung's timeline focus names the process a direction row splits and the
    /// channel an end lane is one end of.
    /// </summary>
    private IntervalByteScope ScopeOfLane(
        Mechanism? mechanism, ProcessInstanceId? ownerLane, Direction? direction, ChannelEndTimelineLane? end)
    {
        if (end is not null && timelineFocus is { ChannelKey: { } key, OwnerProcesses.Count: 0 })
        {
            return new() { ChannelKey = key, End = end.End };
        }

        if (direction is { } sourceDirection && timelineFocus is { ChannelKey: null, OwnerProcesses.Count: 1 } focus)
        {
            return new() { Owner = focus.OwnerProcesses[0], Direction = sourceDirection };
        }

        return ownerLane is { } owner ? new() { Owner = owner }
            : mechanism is { } lane ? new() { Mechanism = lane }
            : IntervalByteScope.Whole;
    }

    /// <summary>
    /// Starts reading the bytes of the rows the table lists when it is shown and they are not known, and cancels a read of
    /// rows it no longer lists. Called when the table's rows are rebuilt, before their texts are.
    /// </summary>
    private void FollowIntervalBytes(IntervalBytesRequest? listed)
    {
        listedIntervalBytes = listed;
        if (intervalBytesRead is { } running && (running.Request != listed || !showTables || disposed))
        {
            CancelIntervalBytes();
        }

        if (!ReadsBytes || !showTables || disposed || listed is not { } request || evidenceSource is not { } source
            || (intervalBytes is { } known && known.SessionId == source.SessionId && request.IsAnsweredBy(known))
            || intervalBytesRead is not null
            || intervalBytesFailure?.Request == request)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        intervalBytesRead = (request, cancellation);
        IntervalBytesReady = ReadIntervalBytesAsync(source, request, cancellation);
    }

    private async Task ReadIntervalBytesAsync(
        SessionEvidenceSource source, IntervalBytesRequest request, CancellationTokenSource cancellation)
    {
        try
        {
            SessionIntervalByteMeasures measured = await source.IntervalBytesAsync(
                request.Interval, request.Columns, request.Scope, cancellation.Token);
            if (disposed || intervalBytesRead?.Cancellation != cancellation)
            {
                return;
            }

            intervalBytesRead = null;
            cancellation.Dispose();
            intervalBytes = measured.SessionId == source.SessionId ? measured : null;
            intervalBytesFailure = measured.SessionId == source.SessionId ? null : (request, "the session on disk is another one");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Rows listed since, a hidden table or a closed workspace superseded this read.
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || intervalBytesRead?.Cancellation != cancellation)
            {
                return;
            }

            intervalBytesRead = null;
            cancellation.Dispose();
            intervalBytesFailure = (request, exception.Message);
        }

        RefreshIntervalRows(timelineFocusBuckets);
    }

    private void CancelIntervalBytes()
    {
        if (intervalBytesRead is { } running)
        {
            running.Cancellation.Cancel();
            running.Cancellation.Dispose();
            intervalBytesRead = null;
        }
    }

    /// <summary>
    /// A listed row's bytes in words: what its records sent and received once read, or that they are being read, could not
    /// be, or have not been, since the table is not shown.
    /// </summary>
    private string IntervalBytesText(IntervalBytesRequest request, TimelineBucket bucket)
    {
        if (intervalBytes is { } known && request.IsAnsweredBy(known) && known.For(bucket.Interval) is { } bytes)
        {
            return WorkspaceRowBuilder.DescribeTransfers(bytes);
        }

        return intervalBytesFailure?.Request == request ? "bytes could not be read"
            : intervalBytesRead?.Request == request ? "reading bytes…"
            : "bytes not read";
    }

    /// <summary>
    /// What the interval table's caption says of its bytes on a real session: that they are what each interval's records
    /// sent and received - every record's where a focus's count stands beside - that they are being read, or why not.
    /// </summary>
    private string IntervalBytesCaption =>
        listedIntervalBytes is not { } listed ? "bytes not summed per interval"
        : intervalBytes is { } known && listed.IsAnsweredBy(known)
            ? (intervalRowsBesideFocus
                ? "bytes each interval's records sent and received, every record's and not only the focus's"
                : "bytes each interval's records sent and received")
        : intervalBytesFailure is { } failed && failed.Request == listed ? "bytes could not be read: " + failed.Problem
        : intervalBytesRead?.Request == listed ? "reading each interval's bytes…"
        : "bytes not read";

    /// <summary>
    /// A timeline bucket's bytes for its hover card, when the interval table has read them for the same records over the
    /// same interval; null otherwise.
    /// </summary>
    private TransportBytes? ReadBytesOf(IntervalByteScope scope, TimeRange interval) =>
        intervalBytes is { } known && known.Scope == scope ? known.For(interval) : null;
}
