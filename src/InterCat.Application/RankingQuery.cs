using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The <c>icat metric</c> query a ranked export's rows answer, named as the command line names it (R18, I16): its identity
/// over the generation the rows were read from, its canonical specification and the command that asks it; or, where no one
/// request's groups are the rows, why none is, and what lists them instead.
/// </summary>
/// <param name="Identity">The identity <c>icat metric</c> prints for <paramref name="Command"/> over that generation.</param>
/// <param name="Command">The command that asks the query, the session's directory left for a person to name.</param>
/// <param name="Unnamed">Why no request's identity names the rows; null when one does.</param>
public sealed record RankingQueryName(QueryIdentity? Identity, string? Command, string? Unnamed)
{
    /// <summary>No query named, for a stated reason.</summary>
    public static RankingQueryName None(string reason) => new(null, null, reason);
}

/// <summary>
/// The machine rung's rows are groups of a whole session's processes, by executable or by terminal session, ranked by one
/// measure; a metric ranking's are then exactly the groups <c>icat metric</c> answers for one request, value for value, and
/// the export names that request. A records ranking is the observations metric. Calls made or served alone and connections
/// made or accepted are measures no request counts, and the export says so and names what lists them.
/// </summary>
public static class RankingQuery
{
    /// <summary>
    /// The request whose groups a machine rung's rows are when ranked by <paramref name="ranking"/>, grouped by
    /// <paramref name="grouping"/>, over <paramref name="nativeInterval"/> or the whole capture, its records bound under
    /// <paramref name="policy"/>; null for a ranking no request answers (<see cref="Unanswered"/>).
    /// </summary>
    public static MetricRequest? Request(
        RankingMetric ranking, LaneGrouping grouping, TimeRange? nativeInterval, EvidencePolicy policy)
    {
        if (grouping is not (LaneGrouping.Executable or LaneGrouping.UserSession))
        {
            throw new ArgumentOutOfRangeException(nameof(grouping), grouping, "A view groups by executable or terminal session.");
        }

        MetricRequest? request = ranking switch
        {
            RankingMetric.Records => Observed(Metric.Observations),
            RankingMetric.BytesSent => Observed(Metric.BytesSent) with
            {
                ByteDomain = ByteDomain.TransportObserved, AccountingSide = AccountingSide.SendSide,
            },
            RankingMetric.BytesReceived => Observed(Metric.BytesReceived) with
            {
                ByteDomain = ByteDomain.TransportObserved, AccountingSide = AccountingSide.ReceiveSide,
            },
            RankingMetric.EndpointBytes => Observed(Metric.EndpointActivityBytes) with { ByteDomain = ByteDomain.TransportObserved },
            RankingMetric.ActivePeers => Observed(Metric.ActivePeers),
            RankingMetric.RpcErrors => new() { Basis = AnalysisBasis.LogicalOperations, Metric = Metric.Errors },
            RankingMetric.RpcCallTime => Median(DurationInterval.ClientCall),
            RankingMetric.RpcServeTime => Median(DurationInterval.ServerExecution),
            _ => null,
        };
        return request is null ? null : request with { Grouping = grouping, Interval = nativeInterval, EvidencePolicy = policy };
    }

    /// <summary>Why no request answers <paramref name="ranking"/>, and what lists it; null for a ranking one answers.</summary>
    public static string? Unanswered(RankingMetric ranking) => ranking switch
    {
        RankingMetric.RpcCallsMade or RankingMetric.RpcCallsServed =>
            "icat metric counts the RPC calls a process completed made and served together (--basis logical-operations "
            + "--metric operations-completed), and no request counts either side alone, so no query names these rows.",
        RankingMetric.ConnectionsMade =>
            "No icat metric request counts the TCP connections a process made, so no query names these rows; icat processes "
            + "--rank-by connections-made lists each process's.",
        RankingMetric.ConnectionsAccepted =>
            "No icat metric request counts the TCP connections a process accepted, so no query names these rows; icat "
            + "processes --rank-by connections-accepted lists each process's.",
        _ => null,
    };

    /// <summary>
    /// The command that asks <paramref name="request"/>, every member it was given written out as <c>icat metric</c> reads
    /// it, so the identity that command prints is the one the request has: the native ticks of its interval, and the
    /// evidence policy wherever it is not the default.
    /// </summary>
    public static string Command(MetricRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var words = new List<string> { "icat metric <directory>" };
        if (request.Basis != AnalysisBasis.SourceObservations) words.Add("--basis " + Kebab(request.Basis.ToString()));
        words.Add("--metric " + Kebab(request.Metric.ToString()));
        if (request.ByteDomain is { } domain) words.Add("--byte-domain " + Kebab(domain.ToString()));
        if (request.AccountingSide is { } side) words.Add("--side " + Kebab(side.ToString()));
        if (request.DurationInterval is { } duration) words.Add("--duration " + Kebab(duration.ToString()));
        if (request.Cohort is { } cohort) words.Add("--cohort " + Kebab(cohort.ToString()));
        if (request.Statistic is { } statistic) words.Add("--statistic " + Kebab(statistic.ToString()));
        if (request.Grouping is { } grouping)
        {
            words.Add("--group-by " + grouping switch
            {
                LaneGrouping.Executable => "executable",
                LaneGrouping.UserSession => "session",
                LaneGrouping.InstanceOnly => "process",
                _ => throw new ArgumentOutOfRangeException(nameof(request), grouping, "A grouping no command names."),
            });
        }

        if (request.Interval is { } interval)
        {
            words.Add(string.Create(CultureInfo.InvariantCulture, $"--interval {interval.StartTicks}:{interval.EndTicks}"));
        }

        if (request.EvidencePolicy != EvidencePolicy.IncludeCorrelated)
        {
            words.Add("--evidence-policy " + Kebab(request.EvidencePolicy.ToString()));
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// The query a ranked export's rows answer, over the generation it names: at the machine rung, under a ranking one
    /// request answers, with nothing set aside, the request's identity while that generation is still the session's current
    /// one; otherwise why no query is named. Reads the session's segments' declarations to name the captures it holds.
    /// </summary>
    public static RankingQueryName Name(
        SessionStore store, ExportContext context, EvidencePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Generation is not { } generation)
        {
            return RankingQueryName.None("This view is of no session, so no query names its rows.");
        }

        if (context.Rung != DetailLevel.Machine)
        {
            return RankingQueryName.None(
                "Only the machine's rows are the groups of one icat metric request; these rows are "
                + context.Rung switch
                {
                    DetailLevel.Group => "one group's processes, which icat metric --group-by process answers among every other process.",
                    DetailLevel.ProcessInstance => "a process's connections and channels, which icat channels and icat operations list.",
                    _ => "a channel's, which icat channels, icat operations and icat exchanges list.",
                });
        }

        if (Unanswered(context.RankedBy) is { } unanswered)
        {
            return RankingQueryName.None(unanswered);
        }

        if (context.SetAsideProcesses > 0)
        {
            return RankingQueryName.None(
                "The view sets InterCat's own processes aside, and icat metric counts them, so no query answers these rows exactly.");
        }

        if (store.Current is not { } manifest || manifest.Generation != generation)
        {
            return Moved;
        }

        TimeRange? native = null;
        if (context.Interval is { } presentation)
        {
            SourceClockDescriptor? clock = SessionSegments.SourceClock(store.Root, manifest);
            native = clock is { } described ? RankingScope.NativeInterval(described, presentation) : null;
            if (native is null)
            {
                return RankingQueryName.None(
                    "No reading of the capture's clock falls in the interval these rows answer, so no icat metric interval names it.");
            }
        }

        MetricRequest request = Request(context.RankedBy, context.GroupedBy, native, policy)!;
        QueryIdentity? identity;
        try
        {
            identity = SessionMetrics.Identify(store, request, generation, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The rows were read; naming their query reads the segments' declarations again, and an export is not refused
            // for what that read could not reach.
            return Unreadable(exception);
        }

        return identity is not null ? new(identity, Command(request), null)
            : store.Current?.Generation != generation ? Moved
            : RankingQueryName.None("This generation publishes no derived segment, so no icat metric answer names it.");
    }

    /// <summary>No query named, because the session could not be read to name it.</summary>
    public static RankingQueryName Unreadable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return RankingQueryName.None("The session could not be read to name the icat metric query these rows answer: "
            + exception.Message);
    }

    private static RankingQueryName Moved => RankingQueryName.None(
        "The session published a newer generation than these rows were read from; icat metric answers that one now.");

    private static MetricRequest Observed(Metric metric) => new() { Basis = AnalysisBasis.SourceObservations, Metric = metric };

    private static MetricRequest Median(DurationInterval interval) => new()
    {
        Basis = AnalysisBasis.LogicalOperations,
        Metric = Metric.Duration,
        DurationInterval = interval,
        Cohort = OperationCohort.CompletedInRange,
        Statistic = DurationStatistic.Median,
    };

    /// <summary>An enumeration's name as a person types it: "BytesSent" as "bytes-sent".</summary>
    private static string Kebab(string name) => string.Concat(name.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
