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
        RankingMetric.RpcCallsMade => "RPC calls made: rank by the RPC client calls each process completed",
        RankingMetric.RpcCallsServed => "RPC calls served: rank by the RPC server calls each process completed",
        RankingMetric.EndpointBytes =>
            "Bytes sent and received: rank by the transport-observed bytes of all of each process's own records, counting a local transfer at both ends",
        RankingMetric.RpcErrors => "RPC errors: rank by the completed RPC calls each process made or served that failed",
        _ => "Records: rank by each process's own records",
    };
}

/// <summary>
/// The ranked table's metric selector (§5.2, §6.1). The machine and group rungs rank by records unless a byte or RPC call
/// ranking is chosen. Its measures are read off the UI thread for the scope the rows count, the whole session or the
/// applied interval, and rank the rows only while they answer that scope. Until they do, the rows keep their records
/// ranking and the note beneath the selector says the measures are being read. A live publication shows the previous
/// one's measures, marked, until its own arrive, as it does its interval counts (§6.4).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private static readonly ReadOnlyCollection<RankingOption> RankingChoices = Array.AsReadOnly(
    [
        new RankingOption(RankingMetric.Records, "Records"),
        new RankingOption(RankingMetric.BytesSent, "Bytes sent"),
        new RankingOption(RankingMetric.BytesReceived, "Bytes received"),
        new RankingOption(RankingMetric.EndpointBytes, "Bytes sent and received"),
        new RankingOption(RankingMetric.RpcCallsMade, "RPC calls made"),
        new RankingOption(RankingMetric.RpcCallsServed, "RPC calls served"),
        new RankingOption(RankingMetric.RpcErrors, "RPC errors"),
    ]);

    private RankingMetric rankBy = RankingMetric.Records;
    private readonly RankingReads<SessionByteMeasures> byteReads = new((source, scope, cancellation) =>
        source.ByteMeasuresAsync(scope, cancellation));
    private readonly RankingReads<SessionCallMeasures> callReads = new((source, scope, cancellation) =>
        source.CallMeasuresAsync(scope, cancellation));

    /// <summary>What the machine and group rungs can rank by: records alone without a session to read measures from.</summary>
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
    /// What the machine and group rungs rank by. A byte or call ranking reads each process's measures for the scope the
    /// rows count; until they arrive, the rows keep ranking by records. Choosing a ranking again retries a failed read.
    /// </summary>
    public RankingMetric RankBy
    {
        get => rankBy;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == rankBy || disposed) return;
            rankBy = value;
            if (Family != RankingFamily.Bytes) byteReads.Cancel();
            if (Family != RankingFamily.Calls) callReads.Cancel();
            Rerank();
            RankingReady = FollowRankedMeasuresAsync();
        }
    }

    /// <summary>Completes when the most recent read for the ranking has applied, been superseded or failed.</summary>
    public Task RankingReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Whether this rung offers the selector: a session's machine and group rungs, whose rows are groups and processes, and a
    /// process's rung, whose TCP channels a byte ranking orders by the process's own bytes on each.
    /// </summary>
    public bool ShowsRankingChoice => evidenceSource is not null
        && ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group or DetailLevel.ProcessInstance;

    /// <summary>What the rows are ranked by now: the chosen ranking once its measures answer the rows' scope, else records.</summary>
    public RankingMetric AppliedRanking => ShownMeasures is null || !RanksThisRung ? RankingMetric.Records : rankBy;

    /// <summary>
    /// Whether the chosen ranking orders this rung's rows: every ranking orders groups and processes; at a process's rung
    /// only bytes do, since its RPC channels already list their calls and no TCP channel carries one.
    /// </summary>
    private bool RanksThisRung => ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group
        || (ladder.Current.Level == DetailLevel.ProcessInstance && Family == RankingFamily.Bytes);

    /// <summary>Whether the rail states what a ranking measures, or why it does not rank yet.</summary>
    public bool ShowsRankingNote => ShowsRankingChoice && rankBy != RankingMetric.Records;

    /// <summary>
    /// What a ranking measured over the rows shown, in one line the narrow rail keeps short: the bytes and the records
    /// that measured them, or the calls and their failures. While the measures are read, or when they could not be or
    /// the capture did not collect them, it says so; <see cref="RankingNoteDetail"/> says the rest.
    /// </summary>
    public string RankingNote
    {
        get
        {
            if (!ShowsRankingNote) return string.Empty;
            if (!RanksThisRung) return "Channels rank by records at a process's rung";
            string name = Capitalized(Phrase(rankBy));
            if (CurrentMeasures is SessionCallMeasures { Unavailable: not null })
            {
                return $"{name} unavailable: RPC was not collected";
            }

            if (ShownMeasures is null)
            {
                return ActiveProblem is { } problem ? $"{name} unavailable: {problem}" : $"Reading {Phrase(rankBy)}…";
            }

            (long value, long measured, long unmeasured, long failed) = RankedTotals();
            string note;
            if (rankBy == RankingMetric.RpcErrors)
            {
                note = string.Create(CultureInfo.CurrentCulture, $"{value:N0} failed of {measured:N0} {(measured == 1 ? "call" : "calls")}");
                if (unmeasured > 0) note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} without status");
            }
            else if (rankBy == RankingMetric.EndpointBytes)
            {
                note = measured > 0
                    ? string.Create(CultureInfo.CurrentCulture, $"{WorkspaceRowBuilder.DescribeSize(value)} on {measured:N0} records · both ends")
                    : unmeasured > 0
                        ? string.Create(CultureInfo.CurrentCulture, $"{unmeasured:N0} records, none with a size")
                        : "No transfers in scope";
                if (measured > 0 && unmeasured > 0)
                {
                    note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} unmeasured");
                }
            }
            else if (Family == RankingFamily.Calls)
            {
                note = string.Create(CultureInfo.CurrentCulture, $"{measured:N0} {(measured == 1 ? "call" : "calls")}");
                if (failed > 0) note += string.Create(CultureInfo.CurrentCulture, $" · {failed:N0} failed");
                if (unmeasured > 0) note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} unpaired");
            }
            else
            {
                string records = rankBy == RankingMetric.BytesSent ? "sends" : "receives";
                note = measured > 0
                    ? string.Create(CultureInfo.CurrentCulture, $"{WorkspaceRowBuilder.DescribeSize(value)} on {measured:N0} {records}")
                    : unmeasured > 0
                        ? string.Create(CultureInfo.CurrentCulture, $"{unmeasured:N0} {records}, none with a size")
                        : $"No {records} in scope";
                if (measured > 0 && unmeasured > 0)
                {
                    note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} unmeasured");
                }
            }

            return MeasuresStandIn ? note + " · updating" : note;
        }
    }

    /// <summary>
    /// The note in full, for its tooltip and a screen reader: what the ranking measures, how it ranks a row with nothing
    /// measured, what no process holds at the machine rung, the coverage RPC calls rest on, and whether an earlier
    /// publication's measures stand in or why the rows still rank by records.
    /// </summary>
    public string RankingNoteDetail
    {
        get
        {
            if (!ShowsRankingNote) return string.Empty;
            if (!RanksThisRung)
            {
                return "RPC calls made and served rank groups and processes. At a process's rung its RPC channels list their "
                    + "calls, no TCP channel carries one, and the channels rank by records.";
            }

            string definition = ladder.Current.Level == DetailLevel.ProcessInstance
                ? ChannelDefinition(rankBy)
                : Definition(rankBy);
            if (CurrentMeasures is SessionCallMeasures { Unavailable: { } unavailable })
            {
                return $"{definition} They cannot rank the rows: {unavailable}. The rows rank by records.";
            }

            if (ShownMeasures is not { } shown)
            {
                return ActiveProblem is { } problem
                    ? $"{definition} They could not be read ({problem}), so the rows rank by records."
                    : $"{definition} The rows rank by records until the {Phrase(rankBy)} are read.";
            }

            (long value, long measured, long unmeasured, long failed) = RankedTotals();
            string detail;
            if (shown is SessionCallMeasures errors && rankBy == RankingMetric.RpcErrors)
            {
                detail = string.Create(CultureInfo.CurrentCulture,
                    $"{definition} The rows shown completed {measured:N0} calls whose stop carried a status; {value:N0} of them failed.");
                if (unmeasured > 0)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {unmeasured:N0} more carried no status and are counted as neither; a row with only such calls ranks after every row that knows its outcomes.");
                }

                if (errors.Coverage.State != CoverageState.Covered)
                {
                    detail += $" RPC's capture coverage over this scope is {errors.Coverage.State}: {errors.Coverage.Reason}.";
                }
            }
            else if (shown is SessionCallMeasures calls)
            {
                detail = string.Create(CultureInfo.CurrentCulture,
                    $"{definition} The rows shown completed {measured:N0} {(measured == 1 ? "call" : "calls")}, {failed:N0} of them failed.");
                if (unmeasured > 0)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {unmeasured:N0} stops paired with no start are stated and not counted; a row with only such stops ranks after every row that completed a call.");
                }

                if (ladder.Current.Level == DetailLevel.Machine && calls.Unattributed.Of(rankBy) is { Holds: true } unheld)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {unheld.Measured:N0} more completed calls belong to no process the evidence policy admits.");
                }

                if (calls.Coverage.State != CoverageState.Covered)
                {
                    detail += $" RPC's capture coverage over this scope is {calls.Coverage.State}: {calls.Coverage.Reason}.";
                }
            }
            else
            {
                string records = rankBy switch
                {
                    RankingMetric.BytesSent => "sends",
                    RankingMetric.BytesReceived => "receives",
                    _ => "records",
                };
                detail = string.Create(CultureInfo.CurrentCulture,
                    $"{definition} The rows shown hold {WorkspaceRowBuilder.DescribeSize(value)} on {measured:N0} measured {records}");
                detail += unmeasured > 0
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"; {unmeasured:N0} more recorded no size. A row with none measured ranks after every measured row, never as zero.")
                    : ".";
                if (ladder.Current.Level == DetailLevel.Machine
                    && ((SessionByteMeasures)shown).Unattributed.Of(rankBy).Value is { } unheld)
                {
                    detail += $" {WorkspaceRowBuilder.DescribeSize(unheld)} more are on {records} no process holds.";
                }
            }

            return MeasuresStandIn
                ? detail + " These are the previous publication's measures, shown until this one's are read."
                : detail;
        }
    }

    /// <summary>What a ranking's selector explains on hover: the definition behind each choice.</summary>
    public static string RankingDefinition =>
        "Records: each process's own records. Bytes sent and received: transport-observed bytes on each process's own "
        + "send or receive records, sender- or receiver-accounted as icat metric answers them; a row whose records recorded "
        + "no size is unmeasured and ranks after every measured row, never as zero. RPC calls made and served: the client "
        + "or server calls each process completed, counted by their stop; a local call is made by one process and served by "
        + "another.";

    private RankingFamily Family => RankingMetrics.FamilyOf(rankBy);

    /// <summary>The scope the rows count: the interval the shown counts answer, or the whole session (null).</summary>
    private TimeRange? CountedScope => scopedSnapshot is null ? null : displayedCountsInterval;

    /// <summary>The active family's measures for the scope the rows count, this generation's or a stand-in, usable or not.</summary>
    private IRankingMeasures? CurrentMeasures => evidenceSource is null ? null : Family switch
    {
        RankingFamily.Bytes => byteReads.For(CountedScope),
        RankingFamily.Calls => callReads.For(CountedScope),
        _ => null,
    };

    /// <summary>The measures ranking the rows: the current ones, unless the capture did not collect what they count.</summary>
    private IRankingMeasures? ShownMeasures => CurrentMeasures is SessionCallMeasures { Unavailable: not null } ? null : CurrentMeasures;

    /// <summary>Whether the current measures are an earlier publication's, which an export waits past.</summary>
    private bool MeasuresStandIn => CurrentMeasures switch
    {
        SessionByteMeasures bytes => byteReads.IsCarried(bytes),
        SessionCallMeasures calls => callReads.IsCarried(calls),
        _ => false,
    };

    /// <summary>Why the active family's last read failed; null when it did not.</summary>
    private string? ActiveProblem => Family switch
    {
        RankingFamily.Bytes => byteReads.Problem,
        RankingFamily.Calls => callReads.Problem,
        _ => null,
    };

    /// <summary>The current rung's rows, ranked by the shown measures when a byte or call ranking has them.</summary>
    private LadderView ProjectLadder() => ShownMeasures switch
    {
        SessionByteMeasures bytes => LadderProjection.Project(OverviewWorkspace.WithBytes(Snapshot, bytes), ladder.Current, rankBy),
        SessionCallMeasures calls => LadderProjection.Project(OverviewWorkspace.WithCalls(Snapshot, calls), ladder.Current, rankBy),
        _ => LadderProjection.Project(Snapshot, ladder.Current),
    };

    /// <summary>What an export adds for a call ranking, as <c>icat export</c> does: why it could not rank, or its coverage.</summary>
    private string? RankingExportCaveat => ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group
        && CurrentMeasures is SessionCallMeasures calls
        ? SessionExport.CallCaveat(calls)
        : null;

    /// <summary>The ranked values of the rows shown, the records behind them, those not counted, and the failed calls.</summary>
    private (long Value, long Measured, long Unmeasured, long Failed) RankedTotals()
    {
        long value = 0, measured = 0, unmeasured = 0, failed = 0;
        foreach (LadderRow row in view.Rows)
        {
            if (row.Ranked is not { } ranked) continue;
            value = checked(value + (ranked.Value ?? 0));
            measured += ranked.Measured;
            unmeasured += ranked.Unmeasured;
            failed += ranked.Failed ?? 0;
        }

        return (value, measured, unmeasured, failed);
    }

    private static string Phrase(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "bytes sent",
        RankingMetric.BytesReceived => "bytes received",
        RankingMetric.RpcCallsMade => "RPC calls made",
        RankingMetric.RpcCallsServed => "RPC calls served",
        RankingMetric.EndpointBytes => "bytes sent and received",
        RankingMetric.RpcErrors => "RPC errors",
        _ => "records",
    };

    private static string Capitalized(string phrase) => char.ToUpperInvariant(phrase[0]) + phrase[1..];

    private static string ChannelDefinition(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "Bytes sent are the transport-observed bytes of this process's own send records on each "
            + "channel, sender-accounted; its RPC channels carry no size.",
        RankingMetric.BytesReceived => "Bytes received are the transport-observed bytes of this process's own receive records "
            + "on each channel, receiver-accounted; its RPC channels carry no size.",
        _ => "Bytes sent and received are the transport-observed bytes of every one of this process's own records on each "
            + "channel, both directions; its RPC channels carry no size.",
    };

    private static string Definition(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "Bytes sent are the transport-observed bytes of each process's own send records, sender-accounted.",
        RankingMetric.BytesReceived =>
            "Bytes received are the transport-observed bytes of each process's own receive records, receiver-accounted.",
        RankingMetric.RpcCallsMade => "RPC calls made are the client calls each process completed, counted by their stop; "
            + "a failed call's stop reported a status other than 0.",
        RankingMetric.RpcCallsServed => "RPC calls served are the server calls each process completed, counted by their stop; "
            + "a failed call's stop reported a status other than 0.",
        RankingMetric.EndpointBytes => "Bytes sent and received are the transport-observed bytes of every one of each "
            + "process's own records, both directions: endpoint activity, which counts a local transfer at both of its ends, so "
            + "the rows' sum is not a transfer total (metrics-v1 §5.1).",
        RankingMetric.RpcErrors => "RPC errors are the completed calls each process made or served whose stop reported a "
            + "status other than 0; a call whose stop carried no status is unmeasured, never a success.",
        _ => "Records are each process's own records.",
    };

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
        OnPropertyChanged(nameof(GraphDisplay));
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

    /// <summary>Reads what the chosen ranking needs for the scope the rows count, unless this generation's is known.</summary>
    private Task FollowRankedMeasuresAsync() => Family switch
    {
        RankingFamily.Bytes => FollowAsync(byteReads),
        RankingFamily.Calls => FollowAsync(callReads),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Reads one family's measures for the scope the rows count, unless this generation's are already known. A newer scope
    /// supersedes the read; a failed one leaves the rows ranked by records and says why, and is not read again on its own.
    /// Whole-session measures that arrive after the scope moved are kept for its return. A read for a description
    /// (<paramref name="ranks"/> false) only keeps its measures and refreshes what states bytes: it never re-ranks the rows,
    /// which its arrival must not move under a selection or a navigation in progress.
    /// </summary>
    private async Task FollowAsync<T>(RankingReads<T> reads, bool ranks = true)
        where T : class, IRankingMeasures
    {
        if (evidenceSource is not { } source || disposed)
        {
            return;
        }

        TimeRange? scope = CountedScope;
        if (reads.For(scope) is { } known && !reads.IsCarried(known) || reads.FailedFor(scope))
        {
            return;
        }

        RankingFamily family = Family;
        Task<T> read = reads.Read(source, scope);
        reads.Problem = null;
        RaiseRankingChanged();
        RefreshBytesState<T>();
        try
        {
            T measured = await read;
            if (disposed)
            {
                return;
            }

            if (measured.SessionId != source.SessionId)
            {
                reads.Problem = "this directory now holds another session";
                RaiseRankingChanged();
                return;
            }

            if (scope is null || CountedScope == scope)
            {
                reads.Keep(measured);
                if (measured is SessionByteMeasures)
                {
                    RefreshDescribedBytes();
                }
            }

            if (ranks && Family == family && CountedScope == scope)
            {
                Rerank();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer scope, another ranking or a closed workspace superseded this read; nothing is applied.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!disposed && reads.IsCurrent(read))
            {
                reads.Problem = exception.Message;
                RaiseRankingChanged();
                RefreshBytesState<T>();
            }
        }
    }

    /// <summary>
    /// Restates what says bytes when a read of them starts or fails, so the tables and inspector say "reading bytes…" or
    /// that they could not be read rather than what they said before.
    /// </summary>
    private void RefreshBytesState<T>()
    {
        if (typeof(T) == typeof(SessionByteMeasures) && ReadsBytes)
        {
            RefreshDescribedBytes();
        }
    }

    /// <summary>
    /// The measures of an interval about to be applied, read beside its counts so the rows change once, in one step
    /// (§6.4). Null when the ranking is by records or the read failed, which leaves the rows ranked by records and says
    /// why; a failed read never holds the counts back.
    /// </summary>
    private async Task<IRankingMeasures?> MeasuresBesideCountsAsync(SessionEvidenceSource source, TimeRange interval) =>
        Family switch
        {
            RankingFamily.Bytes => await BesideCountsAsync(byteReads, source, interval),
            RankingFamily.Calls => await BesideCountsAsync(callReads, source, interval),
            _ => null,
        };

    private static async Task<T?> BesideCountsAsync<T>(RankingReads<T> reads, SessionEvidenceSource source, TimeRange interval)
        where T : class, IRankingMeasures
    {
        Task<T> read = reads.Read(source, interval);
        try
        {
            T measured = await read;
            reads.Problem = measured.SessionId == source.SessionId ? null : "this directory now holds another session";
            return reads.Problem is null ? measured : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            reads.Problem = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Keeps the measures read beside an interval's counts for the family they were read for. This generation's answer
    /// replaces a stand-in even when its read failed: the rows then rank by records.
    /// </summary>
    private void KeepBesideCounts(RankingFamily family, IRankingMeasures? measured)
    {
        switch (family)
        {
            case RankingFamily.Bytes:
                byteReads.KeepInterval(measured as SessionByteMeasures);
                break;
            case RankingFamily.Calls:
                callReads.KeepInterval(measured as SessionCallMeasures);
                break;
        }
    }

    /// <summary>
    /// One family's ranking measures, bytes or calls: this generation's for the whole session and for one interval once
    /// read, an earlier publication's standing in until they are, the one read in flight, and why the last one failed.
    /// </summary>
    private sealed class RankingReads<T>(Func<SessionEvidenceSource, TimeRange?, CancellationToken, Task<T>> start)
        where T : class, IRankingMeasures
    {
        private (TimeRange? Scope, Task<T> Read, CancellationTokenSource Cancellation)? running;

        public T? Whole { get; private set; }

        public T? Interval { get; private set; }

        public T? CarriedWhole { get; private set; }

        public T? CarriedInterval { get; private set; }

        public string? Problem { get; set; }

        /// <summary>The measures that answer <paramref name="scope"/>: this generation's, or else a stand-in.</summary>
        public T? For(TimeRange? scope) => scope is { } interval
            ? Interval?.Interval == interval ? Interval : CarriedInterval?.Interval == interval ? CarriedInterval : null
            : Whole ?? CarriedWhole;

        public bool IsCarried(T measures) => ReferenceEquals(measures, CarriedWhole) || ReferenceEquals(measures, CarriedInterval);

        /// <summary>Keeps this generation's measures for their scope, replacing a stand-in.</summary>
        public void Keep(T measures)
        {
            if (measures.Interval is null)
            {
                Whole = measures;
                CarriedWhole = null;
            }
            else
            {
                KeepInterval(measures);
            }
        }

        /// <summary>Keeps this generation's answer for an interval, which is none when its read failed.</summary>
        public void KeepInterval(T? measures)
        {
            Interval = measures;
            CarriedInterval = null;
        }

        /// <summary>Takes on an earlier publication's measures where this generation has none of its own yet.</summary>
        public void Adopt(T? whole, T? interval, Guid session)
        {
            CarriedWhole = Whole is null && whole?.SessionId == session ? whole : null;
            CarriedInterval = Interval is null && interval?.SessionId == session ? interval : null;
        }

        /// <summary>Whether the last read of <paramref name="scope"/> failed, so it is not read again until chosen again.</summary>
        public bool FailedFor(TimeRange? scope) =>
            Problem is not null && running is { Read.IsFaulted: true } failed && failed.Scope == scope;

        public bool IsCurrent(Task<T> read) => running?.Read == read;

        /// <summary>
        /// Whether a read of <paramref name="scope"/> is in flight, or has answered and is about to be kept: until then a
        /// description of that scope's measures is still waiting for them.
        /// </summary>
        public bool Reads(TimeRange? scope) =>
            running is { } current && current.Scope == scope && !current.Read.IsFaulted && !current.Read.IsCanceled;

        /// <summary>One read per scope: a read of the same scope still running is shared, and one of another is cancelled.</summary>
        public Task<T> Read(SessionEvidenceSource source, TimeRange? scope)
        {
            if (running is { } current && current.Scope == scope && !current.Read.IsFaulted && !current.Read.IsCanceled)
            {
                return current.Read;
            }

            Cancel();
            var cancellation = new CancellationTokenSource();
            Task<T> read = start(source, scope, cancellation.Token);
            running = (scope, read, cancellation);
            return read;
        }

        /// <summary>Cancels the read in flight and forgets why the last one failed, so choosing the ranking again retries.</summary>
        public void Cancel()
        {
            Problem = null;
            if (running is { } current)
            {
                current.Cancellation.Cancel();
                current.Cancellation.Dispose();
                running = null;
            }
        }
    }
}
