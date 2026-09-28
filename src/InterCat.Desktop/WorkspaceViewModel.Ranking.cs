using System.Collections.ObjectModel;
using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A choice of what the machine and group rungs rank their rows by (§5.2's metrics, §6.1's basis and metric selector). The
/// choices are listed by the basis they are on, and the first of each basis heads its group with the basis's name.
/// </summary>
public sealed record RankingOption(RankingMetric Metric, string Label) : IAccessibleRow
{
    /// <summary>What a combo box reads as its value when this option is chosen: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    /// <summary>Whether this is the first choice on its basis, which the list heads with the basis's name.</summary>
    public bool OpensBasis { get; init; }

    /// <summary>The heading above the first choice on a basis (§5.3's `EN-Basis`).</summary>
    public string BasisHeading => RankingMetrics.BasisOf(Metric) == AnalysisBasis.LogicalOperations
        ? "LOGICAL OPERATIONS · RPC CALLS"
        : "SOURCE OBSERVATIONS";

    public string AccessibleName => Metric switch
    {
        RankingMetric.BytesSent => "Bytes sent: rank by the transport-observed bytes of each process's own send records",
        RankingMetric.BytesReceived => "Bytes received: rank by the transport-observed bytes of each process's own receive records",
        RankingMetric.RpcCallsMade => "RPC calls made: rank by the RPC client calls each process completed",
        RankingMetric.RpcCallsServed => "RPC calls served: rank by the RPC server calls each process completed",
        RankingMetric.EndpointBytes =>
            "Bytes sent and received: rank by the transport-observed bytes of all of each process's own records, counting a local transfer at both ends",
        RankingMetric.RpcErrors => "RPC errors: rank by the completed RPC calls each process made or served that failed",
        RankingMetric.RpcCallTime => "RPC call time: rank by the median time each process's completed client calls took, slowest first",
        RankingMetric.RpcServeTime => "RPC serve time: rank by the median time each process took to serve its completed calls, slowest first",
        RankingMetric.ActivePeers => "Peers: rank by the distinct processes at the other end of each process's records",
        _ => "Records: rank by each process's own records",
    } + (RankingMetrics.BasisOf(Metric) == AnalysisBasis.LogicalOperations
        ? ", on logical operations" : ", on source observations");
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
    // By basis (§5.3): what each record measured, then what each RPC call, paired from its records, came to.
    private static readonly ReadOnlyCollection<RankingOption> RankingChoices = Array.AsReadOnly(
    [
        new RankingOption(RankingMetric.Records, "Records") { OpensBasis = true },
        new RankingOption(RankingMetric.BytesSent, "Bytes sent"),
        new RankingOption(RankingMetric.BytesReceived, "Bytes received"),
        new RankingOption(RankingMetric.EndpointBytes, "Bytes sent and received"),
        new RankingOption(RankingMetric.ActivePeers, "Peers"),
        new RankingOption(RankingMetric.RpcCallsMade, "RPC calls made") { OpensBasis = true },
        new RankingOption(RankingMetric.RpcCallsServed, "RPC calls served"),
        new RankingOption(RankingMetric.RpcErrors, "RPC errors"),
        new RankingOption(RankingMetric.RpcCallTime, "RPC call time (median)"),
        new RankingOption(RankingMetric.RpcServeTime, "RPC serve time (median)"),
    ]);

    private RankingMetric rankBy = RankingMetric.Records;
    private bool perSecond;
    private readonly RankingReads<SessionByteMeasures> byteReads = new((source, scope, cancellation) =>
        source.ByteMeasuresAsync(scope, cancellation));
    private readonly RankingReads<SessionCallMeasures> callReads = new((source, scope, cancellation) =>
        source.CallMeasuresAsync(scope, cancellation));
    private readonly RankingReads<SessionPeerMeasures> peerReads = new((source, scope, cancellation) =>
        source.PeerMeasuresAsync(scope, cancellation));

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
            if (Family != RankingFamily.Peers) peerReads.Cancel();
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
    /// Whether the ranked table states each row's value per second over the interval the rows count: §5.2's rate, the value
    /// over the whole interval divided by that interval (metrics-v1 §7). It orders the rows as the value does, so it is a
    /// way of reading the ranking rather than a ranking of its own. A whole session states no interval to divide by, and
    /// its rows keep their totals until one is brushed or zoomed to.
    /// </summary>
    public bool PerSecond
    {
        get => perSecond;
        set
        {
            if (perSecond == value || disposed) return;
            perSecond = value;
            OnPropertyChanged();
            Rerank();
        }
    }

    /// <summary>Whether the chosen ranking has a rate to state: a count or a sum, not a median or a distinct count.</summary>
    public bool OffersPerSecond => ShowsRankingChoice && RankingMetrics.IsAdditive(rankBy);

    /// <summary>
    /// The seconds each row's value is divided by when it is stated per second: the whole interval the rows count. Null
    /// when rows are not stated per second, or count the whole session, which states no interval (metrics-v1 §7 refuses
    /// the span between the first and last record as one).
    /// </summary>
    private double? RateSeconds => perSecond && OffersPerSecond && RankingMetrics.IsAdditive(AppliedRanking)
        && CountedScope is { } interval
            ? interval.SpanTicks / (double)WorkspaceTime.TicksPerSecond
            : null;

    /// <summary>What the per-second choice adds to the note: the interval its rates divide by, or why there are none.</summary>
    private string PerSecondNote => !perSecond || !OffersPerSecond ? string.Empty
        : CountedScope is { } interval
            ? " · per second over " + OperationText.Duration(interval.SpanTicks * 100, CultureInfo.CurrentCulture)
        : " · per second needs an interval: brush one or zoom";

    /// <summary>The per-second choice in full, for the note's tooltip and a screen reader.</summary>
    private string PerSecondDetail => !perSecond || !OffersPerSecond ? string.Empty
        : CountedScope is { } interval
            ? " Per second: each row's value over the whole " + OperationText.Duration(interval.SpanTicks * 100, CultureInfo.CurrentCulture)
                + " the rows count, divided by it (metrics-v1 §7). It is an observed rate, never corrected for coverage, and "
                + "the rows keep the order of their totals, which the second line of each still states."
        : " Per second needs an interval: a rate divides a value by the whole interval it counts, and the whole session "
            + "states none, since the span between its first and last record is not one. Brush an interval or zoom to see rates.";

    /// <summary>
    /// Whether the chosen ranking orders this rung's rows: every ranking orders groups and processes; at a process's rung
    /// only bytes do, since its RPC channels already list their calls and no TCP channel carries one.
    /// </summary>
    private bool RanksThisRung => ladder.Current.Level is DetailLevel.Machine or DetailLevel.Group
        || (ladder.Current.Level == DetailLevel.ProcessInstance && Family == RankingFamily.Bytes);

    /// <summary>
    /// The basis the chosen ranking is on, beside the selector (§3.2: basis and metric stay on screen): "observations" for
    /// records, bytes and peers, "operations" for RPC calls paired from their records.
    /// </summary>
    public string RankingBasis => RankingMetrics.BasisOf(rankBy) == AnalysisBasis.LogicalOperations ? "operations" : "observations";

    /// <summary>The basis in full, for the caption's tooltip and a screen reader.</summary>
    public string RankingBasisDetail => RankingMetrics.BasisOf(rankBy) == AnalysisBasis.LogicalOperations
        ? "Basis: logical operations. Each RPC call is paired from its start and stop records (rpc-call-operation-v1) and "
            + "counted by the record that puts it in scope; the records beneath a call add no operation."
        : "Basis: source observations. Each record counts as the capture recorded it, as the rung's own totals do.";

    /// <summary>Whether the rail states what a ranking measures, or why it does not rank yet, or what a rate divides by.</summary>
    public bool ShowsRankingNote => ShowsRankingChoice && (rankBy != RankingMetric.Records || (perSecond && OffersPerSecond));

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
            if (rankBy == RankingMetric.Records)
            {
                return CountedScope is not null ? "Records" + PerSecondNote : Capitalized(PerSecondNote[3..]);
            }

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
            if (rankBy == RankingMetric.ActivePeers)
            {
                note = ShownPeers() is { } peers
                    ? string.Create(CultureInfo.CurrentCulture, $"{peers:N0} {(peers == 1 ? "process has" : "processes have")} a peer")
                    : "No peer resolved";
                if (unmeasured > 0)
                {
                    note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} {(unmeasured == 1 ? "record" : "records")} unresolved");
                }
            }
            else if (RankingMetrics.IsDuration(rankBy))
            {
                note = ShownMedian() is { } median
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"Median {OperationText.Duration(median, CultureInfo.CurrentCulture)} over {measured:N0} {(measured == 1 ? "call" : "calls")}")
                    : unmeasured > 0 ? "No call timed" : "No calls in scope";
                if (unmeasured > 0) note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} unpaired");
            }
            else if (rankBy == RankingMetric.RpcErrors)
            {
                note = string.Create(CultureInfo.CurrentCulture, $"{value:N0} failed of {measured:N0} {(measured == 1 ? "call" : "calls")}");
                if (unmeasured > 0) note += string.Create(CultureInfo.CurrentCulture, $" · {unmeasured:N0} without status");
            }
            else if (rankBy == RankingMetric.EndpointBytes)
            {
                note = measured > 0
                    ? $"{WorkspaceRowBuilder.DescribeSize(value)} on {Spoken.Count(measured, "record")} · both ends"
                    : unmeasured > 0
                        ? Spoken.Count(unmeasured, "record") + (unmeasured == 1 ? ", with no size" : ", none with a size")
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

            return (MeasuresStandIn ? note + " · updating" : note) + PerSecondNote;
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
            if (rankBy == RankingMetric.Records)
            {
                return "Records: each process's own records." + PerSecondDetail;
            }

            if (!RanksThisRung)
            {
                return "RPC call rankings rank groups and processes. At a process's rung its RPC channels list their calls, "
                    + "with each channel's median time, no TCP channel carries one, and the channels rank by records.";
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
            if (shown is SessionPeerMeasures peerMeasures)
            {
                // A record between two rows names a peer for each, so the rows' records are never added up here.
                detail = ShownPeers() is { } peers
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"{definition} {peers:N0} {(peers == 1 ? "process has" : "processes have")} a peer among the rows shown.")
                    : $"{definition} No record of the rows shown resolved a peer.";
                if (unmeasured > 0)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {unmeasured:N0} {(unmeasured == 1 ? "record's" : "records'")} other end is unresolved, or bound more weakly than the evidence policy admits, so each count is a lower bound; a row none of whose records resolved a peer ranks after every row with one.");
                }

                if (ladder.Current.Level == DetailLevel.Machine && peerMeasures.Unattributed > 0)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {Spoken.Count(peerMeasures.Unattributed, "record")} {(peerMeasures.Unattributed == 1 ? "belongs" : "belong")} to no process the evidence policy admits at either end.");
                }
            }
            else if (shown is SessionCallMeasures timed && RankingMetrics.IsDuration(rankBy))
            {
                detail = ShownMedian() is { } median
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"{definition} The rows shown timed {measured:N0} {(measured == 1 ? "call" : "calls")}, each with its start and its stop observed; the median of them all took {OperationText.Duration(median, CultureInfo.CurrentCulture)}.")
                    : $"{definition} The rows shown timed no call.";
                if (unmeasured > 0)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {Spoken.Count(unmeasured, "stop")} paired with no start {(unmeasured == 1 ? "is" : "are")} stated and never timed; a row with only such stops ranks after every row that timed a call.");
                }

                RankingMetric counted = rankBy == RankingMetric.RpcCallTime ? RankingMetric.RpcCallsMade : RankingMetric.RpcCallsServed;
                if (ladder.Current.Level == DetailLevel.Machine && timed.Unattributed.Of(counted) is { Holds: true } unheld)
                {
                    detail += string.Create(CultureInfo.CurrentCulture,
                        $" {unheld.Measured:N0} more completed calls belong to no process the evidence policy admits.");
                }

                if (timed.Coverage.State != CoverageState.Covered)
                {
                    detail += $" RPC's capture coverage over this scope is {timed.Coverage.State}: {timed.Coverage.Reason}.";
                }
            }
            else if (shown is SessionCallMeasures errors && rankBy == RankingMetric.RpcErrors)
            {
                string failedOf = measured == 1
                    ? (value == 1 ? "it failed." : "it did not fail.")
                    : string.Create(CultureInfo.CurrentCulture, $"{value:N0} of them failed.");
                detail = $"{definition} The rows shown completed {Spoken.Count(measured, "call")} whose stop carried a status; {failedOf}";
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
                        $" {Spoken.Count(unmeasured, "stop")} paired with no start {(unmeasured == 1 ? "is" : "are")} stated and not counted; a row with only such stops ranks after every row that completed a call.");
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

            return (MeasuresStandIn
                ? detail + " These are the previous publication's measures, shown until this one's are read."
                : detail) + PerSecondDetail;
        }
    }

    /// <summary>What a ranking's selector explains on hover: the definition behind each choice.</summary>
    public static string RankingDefinition =>
        "Records: each process's own records. Bytes sent and received: transport-observed bytes on each process's own "
        + "send or receive records, sender- or receiver-accounted as icat metric answers them; a row whose records recorded "
        + "no size is unmeasured and ranks after every measured row, never as zero. RPC calls made and served: the client "
        + "or server calls each process completed, counted by their stop; a local call is made by one process and served by "
        + "another. RPC call and serve time: the median time those calls took, slowest first; a stop paired with no start is "
        + "never timed, and a group's median is its members' calls together. Peers: the distinct processes at the other end "
        + "of each process's records; two processes are each other's peer, so the rows overlap.";

    private RankingFamily Family => RankingMetrics.FamilyOf(rankBy);

    /// <summary>The scope the rows count: the interval the shown counts answer, or the whole session (null).</summary>
    private TimeRange? CountedScope => scopedSnapshot is null ? null : displayedCountsInterval;

    /// <summary>The active family's measures for the scope the rows count, this generation's or a stand-in, usable or not.</summary>
    private IRankingMeasures? CurrentMeasures => evidenceSource is null ? null : Family switch
    {
        RankingFamily.Bytes => byteReads.For(CountedScope),
        RankingFamily.Calls => callReads.For(CountedScope),
        RankingFamily.Peers => peerReads.For(CountedScope),
        _ => null,
    };

    /// <summary>The measures ranking the rows: the current ones, unless the capture did not collect what they count.</summary>
    private IRankingMeasures? ShownMeasures => CurrentMeasures is SessionCallMeasures { Unavailable: not null } ? null : CurrentMeasures;

    /// <summary>Whether the current measures are an earlier publication's, which an export waits past.</summary>
    private bool MeasuresStandIn => CurrentMeasures switch
    {
        SessionByteMeasures bytes => byteReads.IsCarried(bytes),
        SessionCallMeasures calls => callReads.IsCarried(calls),
        SessionPeerMeasures peers => peerReads.IsCarried(peers),
        _ => false,
    };

    /// <summary>Why the active family's last read failed; null when it did not.</summary>
    private string? ActiveProblem => Family switch
    {
        RankingFamily.Bytes => byteReads.Problem,
        RankingFamily.Calls => callReads.Problem,
        RankingFamily.Peers => peerReads.Problem,
        _ => null,
    };

    /// <summary>The current rung's rows, ranked by the shown measures when a byte or call ranking has them.</summary>
    private LadderView ProjectLadder() => ShownMeasures switch
    {
        SessionByteMeasures bytes => LadderProjection.Project(OverviewWorkspace.WithBytes(Snapshot, bytes), ladder.Current, rankBy),
        SessionCallMeasures calls => LadderProjection.Project(OverviewWorkspace.WithCalls(Snapshot, calls), ladder.Current, rankBy),
        SessionPeerMeasures peers => LadderProjection.Project(OverviewWorkspace.WithPeers(Snapshot, peers), ladder.Current, rankBy),
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
        RankingMetric.RpcCallTime => "RPC call times",
        RankingMetric.RpcServeTime => "RPC serve times",
        RankingMetric.ActivePeers => "peers",
        _ => "records",
    };

    /// <summary>
    /// How many processes have a peer among the rows shown: every process with one at the machine rung, and at a group's
    /// rung the group's own count, its members' peers together. Peers overlap, so this is never the rows' sum.
    /// </summary>
    private long? ShownPeers()
    {
        if (ShownMeasures is not SessionPeerMeasures peers) return null;
        long? shown = ladder.Current.Level == DetailLevel.Group && ladder.Current.Focus is { } group
            ? peers.ByGroup.GetValueOrDefault(group.Key)?.Peers
            : peers.WithPeers;
        return shown is > 0 ? shown : null;
    }

    /// <summary>
    /// The median over the rows shown, their calls taken together: every process's at the machine rung, the group's at its
    /// rung. A median does not add, so it is never made from the rows' own medians.
    /// </summary>
    private long? ShownMedian()
    {
        if (ShownMeasures is not SessionCallMeasures calls) return null;
        CallTimes times = ladder.Current.Level == DetailLevel.Group && ladder.Current.Focus is { } group
            ? calls.TimesByGroup.GetValueOrDefault(group.Key) ?? CallTimes.None
            : calls.TimesAttributed;
        return rankBy == RankingMetric.RpcCallTime ? times.MadeMedian : times.ServedMedian;
    }

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
        RankingMetric.RpcCallTime => "RPC call time is how long each process's completed client calls took, from a call's "
            + "start to its stop in the calling process (metrics-v1 §8a, ClientCall); a row ranks by the median of its calls, "
            + "slowest first, and a group's median is its members' calls together, never a sum of medians.",
        RankingMetric.ActivePeers => "Peers are the distinct process instances at the other end of each process's records, "
            + "under relations-v1 (metrics-v1 §6.1); a process connected to itself is its own peer, and a group's peers are its "
            + "members' together. A record whose other end is unresolved names no peer, so each count is a lower bound.",
        RankingMetric.RpcServeTime => "RPC serve time is how long each process took to serve its completed server calls, from "
            + "a served call's start to its stop (metrics-v1 §8a, ServerExecution); a row ranks by the median of its calls, "
            + "slowest first, and a group's median is its members' calls together, never a sum of medians.",
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
        OnPropertyChanged(nameof(RankingBasis));
        OnPropertyChanged(nameof(RankingBasisDetail));
        OnPropertyChanged(nameof(AppliedRanking));
        OnPropertyChanged(nameof(ShowsRankingChoice));
        OnPropertyChanged(nameof(OffersPerSecond));
        OnPropertyChanged(nameof(PerSecond));
        OnPropertyChanged(nameof(ShowsRankingNote));
        OnPropertyChanged(nameof(RankingNote));
        OnPropertyChanged(nameof(RankingNoteDetail));
    }

    /// <summary>Reads what the chosen ranking needs for the scope the rows count, unless this generation's is known.</summary>
    private Task FollowRankedMeasuresAsync() => Family switch
    {
        RankingFamily.Bytes => FollowAsync(byteReads),
        RankingFamily.Calls => FollowAsync(callReads),
        RankingFamily.Peers => FollowAsync(peerReads),
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
            RankingFamily.Peers => await BesideCountsAsync(peerReads, source, interval),
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
            case RankingFamily.Peers:
                peerReads.KeepInterval(measured as SessionPeerMeasures);
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
