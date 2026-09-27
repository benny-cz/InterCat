using System.Collections.ObjectModel;
using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>A choice of what the machine and group rungs rank their rows by (§5.2's metrics, §6.1's metric selector).</summary>
public sealed record RankingOption(RankingMetric Metric, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    public string AccessibleName => Metric switch
    {
        RankingMetric.BytesSent => "Bytes sent: rank by the transport-observed bytes of each process's own send records",
        RankingMetric.BytesReceived => "Bytes received: rank by the transport-observed bytes of each process's own receive records",
        _ => "Records: rank by each process's own records",
    };
}

/// <summary>
/// The ranked table's metric selector (§5.2, §6.1). The machine and group rungs rank by records unless a byte ranking is
/// chosen; the bytes are read off the UI thread for the scope the rows count, the whole session or the applied interval,
/// and rank the rows only while they answer that scope. Until they do, the rows keep their records ranking and the note
/// beneath the selector says the bytes are being read. A live publication shows the previous one's bytes, marked, until
/// its own arrive, as it does its interval counts (§6.4).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private static readonly ReadOnlyCollection<RankingOption> RankingChoices = Array.AsReadOnly(
    [
        new RankingOption(RankingMetric.Records, "Records"),
        new RankingOption(RankingMetric.BytesSent, "Bytes sent"),
        new RankingOption(RankingMetric.BytesReceived, "Bytes received"),
    ]);

    private RankingMetric rankBy = RankingMetric.Records;

    // This generation's bytes over the whole session and over one interval once read, and an earlier publication's
    // standing in until they are. Bytes rank the rows only while their interval is the scope the rows count.
    private SessionByteMeasures? wholeBytes;
    private SessionByteMeasures? intervalBytes;
    private SessionByteMeasures? carriedWholeBytes;
    private SessionByteMeasures? carriedIntervalBytes;
    private (TimeRange? Scope, Task<SessionByteMeasures> Read, CancellationTokenSource Cancellation)? byteRead;
    private string? bytesProblem;

    /// <summary>What the machine and group rungs can rank by: records alone without a session to read bytes from.</summary>
    public IReadOnlyList<RankingOption> RankingOptions => evidenceSource is null ? [RankingChoices[0]] : RankingChoices;

    /// <summary>The chosen ranking as the selector shows it.</summary>
    public RankingOption SelectedRanking
    {
        get => RankingChoices.First(option => option.Metric == rankBy);
        set
        {
            if (value is not null) RankBy = value.Metric;
        }
    }

    /// <summary>
    /// What the machine and group rungs rank by. A byte ranking reads each process's bytes for the scope the rows count;
    /// until they arrive, the rows keep ranking by records.
    /// </summary>
    public RankingMetric RankBy
    {
        get => rankBy;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == rankBy || disposed) return;
            rankBy = value;
            if (value == RankingMetric.Records)
            {
                CancelByteRead();
                bytesProblem = null;
            }

            Rerank();
            BytesReady = FollowRankedBytesAsync();
        }
    }

    /// <summary>Completes when the most recent byte read for the ranking has applied, been superseded or failed.</summary>
    public Task BytesReady { get; private set; } = Task.CompletedTask;

    /// <summary>Whether this rung offers the selector: a session's machine and group rungs, whose rows are groups and processes.</summary>
    public bool ShowsRankingChoice => evidenceSource is not null
        && ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group;

    /// <summary>What the rows are ranked by now: the chosen byte ranking once its bytes answer the rows' scope, else records.</summary>
    public RankingMetric AppliedRanking => ShownBytes is null ? RankingMetric.Records : rankBy;

    /// <summary>Whether the rail states what a byte ranking measures, or why it does not rank yet.</summary>
    public bool ShowsRankingNote => ShowsRankingChoice && rankBy != RankingMetric.Records;

    /// <summary>
    /// What a byte ranking measured over the rows shown, in one line the narrow rail keeps short: the bytes, the records
    /// that measured them, and those that recorded no size. While the bytes are read, or when they could not be, it says
    /// so; <see cref="RankingNoteDetail"/> says the rest.
    /// </summary>
    public string RankingNote
    {
        get
        {
            if (!ShowsRankingNote) return string.Empty;
            bool sent = rankBy == RankingMetric.BytesSent;
            if (ShownBytes is null)
            {
                return bytesProblem is { } problem
                    ? $"{(sent ? "Bytes sent" : "Bytes received")} unavailable: {problem}"
                    : $"Reading {(sent ? "bytes sent" : "bytes received")}…";
            }

            (long value, long measured, long unmeasured) = RankedTotals();
            string records = sent ? "sends" : "receives";
            string note = measured > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{WorkspaceRowBuilder.DescribeSize(value)} on {measured:N0} {records}")
                : unmeasured > 0
                    ? string.Create(CultureInfo.CurrentCulture, $"{unmeasured:N0} {records}, none with a size")
                    : $"No {records} in scope";
            if (measured > 0 && unmeasured > 0)
            {
                note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} unmeasured");
            }

            return BytesStandIn ? note + " · updating" : note;
        }
    }

    /// <summary>
    /// The note in full, for its tooltip and a screen reader: what the ranking measures, how it ranks a row whose records
    /// recorded no size, the bytes no process holds at the machine rung, and whether an earlier publication's bytes stand
    /// in or why the rows still rank by records.
    /// </summary>
    public string RankingNoteDetail
    {
        get
        {
            if (!ShowsRankingNote) return string.Empty;
            bool sent = rankBy == RankingMetric.BytesSent;
            string metric = sent ? "bytes sent" : "bytes received";
            string definition = sent
                ? "Bytes sent are the transport-observed bytes of each process's own send records, sender-accounted."
                : "Bytes received are the transport-observed bytes of each process's own receive records, receiver-accounted.";
            if (ShownBytes is not { } bytes)
            {
                return bytesProblem is { } problem
                    ? $"{definition} They could not be read ({problem}), so the rows rank by records."
                    : $"{definition} The rows rank by records until the {metric} are read.";
            }

            (long value, long measured, long unmeasured) = RankedTotals();
            string records = sent ? "sends" : "receives";
            string detail = string.Create(CultureInfo.CurrentCulture,
                $"{definition} The rows shown hold {WorkspaceRowBuilder.DescribeSize(value)} on {measured:N0} measured {records}");
            detail += unmeasured > 0
                ? string.Create(CultureInfo.CurrentCulture,
                    $"; {unmeasured:N0} more recorded no size. A row with none measured ranks after every measured row, never as zero.")
                : ".";
            if (ladder.Current.Level == DetailLevel.Machine && bytes.Unattributed.Of(rankBy).Value is { } unheld)
            {
                detail += $" {WorkspaceRowBuilder.DescribeSize(unheld)} more are on {records} no process holds.";
            }

            return BytesStandIn
                ? detail + " These are the previous publication's bytes, shown until this one's are read."
                : detail;
        }
    }

    /// <summary>The ranked bytes of the rows shown, the records that measured them, and those that recorded no size.</summary>
    private (long Value, long Measured, long Unmeasured) RankedTotals()
    {
        long value = 0, measured = 0, unmeasured = 0;
        foreach (LadderRow row in view.Rows)
        {
            if (row.Ranked is not { } ranked) continue;
            value = checked(value + (ranked.Value ?? 0));
            measured += ranked.Measured;
            unmeasured += ranked.Unmeasured;
        }

        return (value, measured, unmeasured);
    }
    /// <summary>What a byte ranking's selector explains on hover: the definition behind each choice.</summary>
    public static string RankingDefinition =>
        "Records: each process's own records. Bytes sent and received: transport-observed bytes on each process's own "
        + "send or receive records, sender- or receiver-accounted as icat metric answers them. A row whose records recorded "
        + "no size is unmeasured and ranks after every measured row, never as zero.";

    /// <summary>The scope the rows count: the interval the shown counts answer, or the whole session (null).</summary>
    private TimeRange? CountedScope => scopedSnapshot is null ? null : displayedCountsInterval;

    /// <summary>The bytes ranking the rows: this generation's or a stand-in, for exactly the scope the rows count.</summary>
    private SessionByteMeasures? ShownBytes => rankBy == RankingMetric.Records || evidenceSource is null ? null
        : CountedScope is { } scope
            ? (intervalBytes?.Interval == scope ? intervalBytes
                : carriedIntervalBytes?.Interval == scope ? carriedIntervalBytes : null)
            : wholeBytes ?? carriedWholeBytes;

    /// <summary>Whether the shown bytes are an earlier publication's, which an export waits past.</summary>
    private bool BytesStandIn => ShownBytes is { } bytes
        && (ReferenceEquals(bytes, carriedWholeBytes) || ReferenceEquals(bytes, carriedIntervalBytes));

    /// <summary>The current rung's rows, ranked by the shown bytes when a byte ranking has them.</summary>
    private LadderView ProjectLadder() => ShownBytes is { } bytes
        ? LadderProjection.Project(OverviewWorkspace.WithBytes(Snapshot, bytes), ladder.Current, rankBy)
        : LadderProjection.Project(Snapshot, ladder.Current);

    /// <summary>Re-projects the rows under the current ranking, keeping the selected row, and says what changed.</summary>
    private void Rerank()
    {
        string? rowKey = selectedRung?.Key;
        view = ProjectLadder();
        if (!IsEvidenceRung)
        {
            selectedRung = RungRows.FirstOrDefault(row => row.Key == rowKey);
        }

        RaiseRankingChanged();
        OnPropertyChanged(nameof(RungRows));
        OnPropertyChanged(nameof(SelectedRung));
        OnPropertyChanged(nameof(LevelSummary));
        OnPropertyChanged(nameof(LevelSummaryShort));
    }

    private void RaiseRankingChanged()
    {
        OnPropertyChanged(nameof(RankBy));
        OnPropertyChanged(nameof(SelectedRanking));
        OnPropertyChanged(nameof(AppliedRanking));
        OnPropertyChanged(nameof(ShowsRankingChoice));
        OnPropertyChanged(nameof(ShowsRankingNote));
        OnPropertyChanged(nameof(RankingNote));
        OnPropertyChanged(nameof(RankingNoteDetail));
    }

    /// <summary>
    /// Reads the bytes a byte ranking needs for the scope the rows count, unless this generation's are already shown.
    /// A newer scope or ranking supersedes the read; a failed one leaves the rows ranked by records and says why.
    /// </summary>
    private async Task FollowRankedBytesAsync()
    {
        if (rankBy == RankingMetric.Records || evidenceSource is not { } source || disposed)
        {
            return;
        }

        TimeRange? scope = CountedScope;
        if (ShownBytes is not null && !BytesStandIn)
        {
            return;
        }

        // A scope whose read just failed is not read again on its own; choosing the ranking again retries it.
        if (bytesProblem is not null && byteRead is { Read.IsFaulted: true } failed && failed.Scope == scope)
        {
            return;
        }

        Task<SessionByteMeasures> read = ReadBytes(source, scope);
        bytesProblem = null;
        RaiseRankingChanged();
        try
        {
            SessionByteMeasures measured = await read;
            if (disposed || rankBy == RankingMetric.Records || CountedScope != scope)
            {
                return;
            }

            if (measured.SessionId != source.SessionId)
            {
                bytesProblem = "this directory now holds another session";
                RaiseRankingChanged();
                return;
            }

            if (scope is null)
            {
                wholeBytes = measured;
                carriedWholeBytes = null;
            }
            else
            {
                intervalBytes = measured;
                carriedIntervalBytes = null;
            }

            Rerank();
        }
        catch (OperationCanceledException)
        {
            // A newer scope, a return to records or a closed workspace superseded this read; nothing is applied.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && byteRead?.Read == read)
            {
                bytesProblem = exception.Message;
                RaiseRankingChanged();
            }
        }
    }

    /// <summary>
    /// The bytes of an interval about to be applied, read beside its counts so the rows change once, in one step (§6.4).
    /// Null when the ranking is by records or the read failed, which leaves the rows ranked by records and says why; a
    /// failed read never holds the counts back.
    /// </summary>
    private async Task<SessionByteMeasures?> BytesBesideCountsAsync(SessionEvidenceSource source, TimeRange interval)
    {
        if (rankBy == RankingMetric.Records)
        {
            return null;
        }

        Task<SessionByteMeasures> read = ReadBytes(source, interval);
        try
        {
            SessionByteMeasures measured = await read;
            bytesProblem = measured.SessionId == source.SessionId ? null : "this directory now holds another session";
            return bytesProblem is null ? measured : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            bytesProblem = exception.Message;
            return null;
        }
    }

    /// <summary>One byte read per scope: a read of the same scope still running is shared, and one of another is cancelled.</summary>
    private Task<SessionByteMeasures> ReadBytes(SessionEvidenceSource source, TimeRange? scope)
    {
        if (byteRead is { } running && running.Scope == scope && !running.Read.IsFaulted && !running.Read.IsCanceled)
        {
            return running.Read;
        }

        CancelByteRead();
        var cancellation = new CancellationTokenSource();
        Task<SessionByteMeasures> read = source.ByteMeasuresAsync(scope, cancellation.Token);
        byteRead = (scope, read, cancellation);
        return read;
    }

    private void CancelByteRead()
    {
        if (byteRead is { } running)
        {
            running.Cancellation.Cancel();
            running.Cancellation.Dispose();
            byteRead = null;
        }
    }
}
