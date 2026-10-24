using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Where a row's records over the compared interval fall among the records it held in each window of its baseline
/// (`contracts/outliers-v1.md` §3): an observation of counts, never a judgement of why.
/// </summary>
public enum OutlierSide
{
    /// <summary>As many records as some window held, or between the fewest and the most any one held.</summary>
    Within = 1,

    /// <summary>More records than every one of its baseline's windows held.</summary>
    Above = 2,

    /// <summary>Fewer records than every one of its baseline's windows held.</summary>
    Below = 3,
}

/// <summary>
/// A row of a comparison judged against its baseline's windows (`outliers-v1` §3): its records in each window, in the
/// windows' order, and where its records over the compared interval fall among them.
/// </summary>
/// <param name="Row">The comparison's row, with its records over the whole baseline and over the compared interval.</param>
/// <param name="Windows">Its records in each of the baseline's windows, in order: none in a window is 0 there.</param>
/// <param name="Side">Where its records over the compared interval fall among the windows'.</param>
public sealed record OutlierJudgement(ComparedRelationship Row, IReadOnlyList<long> Windows, OutlierSide Side)
{
    /// <summary>The fewest records any one window held.</summary>
    public long Fewest => Windows.Min();

    /// <summary>The most records any one window held.</summary>
    public long Most => Windows.Max();

    /// <summary>
    /// The median window's records, the lower of the two middle ones where the windows are even in number: one window's
    /// own count, never an interpolation.
    /// </summary>
    public long Median => Windows.Order().ElementAt((Windows.Count - 1) / 2);

    /// <summary>
    /// How far the compared interval's records are outside the windows' range: how many more than the most, or fewer than
    /// the fewest; none within it.
    /// </summary>
    public long Beyond => Side switch
    {
        OutlierSide.Above => Row.Compared.Records - Most,
        OutlierSide.Below => Fewest - Row.Compared.Records,
        _ => 0,
    };
}

/// <summary>
/// A comparison's rows judged against the baseline's windows (`contracts/outliers-v1.md`, plan M12's optional outlier
/// detection): the baseline cut into windows as long as the compared interval, spread evenly across it, and each row whose
/// two intervals were covered and collected alike judged by where its records over the compared interval fall among those
/// it held in each window. Were the compared interval like the windows - its records arriving as theirs do - chance alone
/// would put a row's above every window's at most 1 time in <see cref="Chance"/>, and below every one at most as often,
/// whatever the records' distribution: no other assumption is made, so records' bursts widen nothing.
/// </summary>
/// <param name="Comparison">The compared interval compared with the whole baseline, by records, at one generation.</param>
/// <param name="Windows">The baseline's windows, the earliest first, each as long as the compared interval.</param>
/// <param name="Judged">
/// The rows judged: those above or below every window first, the most records outside the windows' range first; then those
/// within it; each tie in the comparison's order.
/// </param>
public sealed record OutlierReading(
    IntervalComparison Comparison,
    IReadOnlyList<TimeRange> Windows,
    IReadOnlyList<OutlierJudgement> Judged)
{
    /// <summary>The rows above or below every window, in <see cref="Judged"/>'s order.</summary>
    public IReadOnlyList<OutlierJudgement> Outliers => [.. Judged.Where(judged => judged.Side != OutlierSide.Within)];

    /// <summary>The rows not judged, whose two intervals cannot be compared, each with why (`comparison-v1` §5).</summary>
    public IReadOnlyList<ComparedRelationship> NotJudged =>
        [.. Comparison.Relationships.Where(row => row.Change == ComparisonChange.NotComparable)];

    /// <summary>
    /// The N of "at most 1 time in N": one more than the windows, as the compared interval is one more interval among them.
    /// </summary>
    public int Chance => Windows.Count + 1;
}

/// <summary>Judges a compared interval's rows against its baseline's windows (`outliers-v1`, plan M12).</summary>
public static class ComparisonOutliers
{
    /// <summary>
    /// The fewest windows a baseline is judged by: with 19, chance alone puts a row above every one of them at most 1 time
    /// in 20, and below every one at most as often.
    /// </summary>
    public const int FewestWindows = 19;

    /// <summary>The most windows a baseline is cut into: each is read as an interval of its own, as a timeline's columns are.</summary>
    public const int MostWindows = 199;

    /// <summary>Why a baseline that overlaps the compared interval is refused.</summary>
    public const string Overlapping = "The baseline overlaps the compared interval, and a row is judged against a baseline "
        + "that holds none of the records it is judged by: give a baseline before or after it.";

    /// <summary>
    /// The windows <paramref name="baseline"/> is cut into to judge <paramref name="compared"/> (`outliers-v1` §2): as many
    /// as long as the compared interval as fit in it, at most <see cref="MostWindows"/>, spread evenly from its start to its
    /// end, none overlapping another. Null, with <paramref name="refusal"/> saying why in words, where the two intervals
    /// overlap or fewer than <see cref="FewestWindows"/> fit.
    /// </summary>
    public static IReadOnlyList<TimeRange>? Windows(
        TimeRange baseline, TimeRange compared, out string? refusal, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        refusal = null;
        if (baseline.StartTicks < compared.EndTicks && compared.StartTicks < baseline.EndTicks)
        {
            refusal = Overlapping;
            return null;
        }

        long span = compared.SpanTicks;
        long fit = baseline.SpanTicks / span;
        if (fit < FewestWindows)
        {
            Int128 needed = (Int128)span * FewestWindows;
            refusal = (fit == 0 ? "The baseline holds no window as long as the compared interval"
                    : "The baseline holds only " + CountText.Of(fit, "window") + " as long as the compared interval")
                + string.Create(culture, $", and at least {FewestWindows} are needed, so that chance alone puts a row above every one of them "
                    + $"no more than 1 time in {FewestWindows + 1}: ")
                + (needed <= long.MaxValue
                    ? "give a baseline at least " + WorkspaceTime.FormatDuration((long)needed, culture) + " long, or compare a shorter interval."
                    : "compare a shorter interval.");
            return null;
        }

        // The first window starts where the baseline does and the last ends where it does. Where the windows do not fill it,
        // the gaps between them are as even as whole ticks allow; no two overlap, as no more are cut than fit end to end.
        int count = (int)Math.Min(fit, MostWindows);
        Int128 free = baseline.SpanTicks - span;
        return [.. Enumerable.Range(0, count).Select(window =>
        {
            long start = baseline.StartTicks + (long)(free * window / (count - 1));
            return new TimeRange(start, start + span);
        })];
    }

    /// <summary>
    /// The baseline a compared interval is judged against where none is named (`outliers-v1` §2): all the session keeps
    /// before it, from the session's start, or from where it keeps every record after an interval release; null where
    /// nothing is before it.
    /// </summary>
    public static TimeRange? Before(SessionStore store, TimeRange compared)
    {
        ArgumentNullException.ThrowIfNull(store);
        using EvidenceLease lease = store.AcquireLease();
        long start = Math.Max(0, SessionRecording.RetainedFromTicks(lease.Manifest) ?? 0);
        return compared.StartTicks > start ? new TimeRange(start, compared.StartTicks) : null;
    }

    /// <summary>
    /// Judges <paramref name="compared"/>'s rows against <paramref name="baseline"/>'s windows, under
    /// <paramref name="policy"/>, a row per process or, by <paramref name="grouping"/>, per executable: the comparison of
    /// the two intervals by records and each row's records in every window, all read at one generation. Refuses in words
    /// a baseline that overlaps the compared interval or holds too few windows (<see cref="Windows"/>).
    /// </summary>
    public static OutlierReading Read(
        SessionStore store,
        TimeRange baseline,
        TimeRange compared,
        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated,
        ComparisonGrouping grouping = ComparisonGrouping.Process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
        IReadOnlyList<TimeRange> windows = Windows(baseline, compared, out string? refusal, CultureInfo.CurrentCulture)
            ?? throw new ArgumentException(refusal, nameof(baseline));
        (IntervalComparison whole, IReadOnlyDictionary<string, long[]> counts) =
            SessionComparison.Windowed(store, baseline, compared, windows, policy, grouping, cancellationToken);
        return Of(whole, windows, counts);
    }

    /// <summary>
    /// A comparison's rows judged against the windows: each row whose two intervals compare, by its records in each window
    /// (<paramref name="counts"/>, by its key); the rows outside their windows' range first, the most records outside it
    /// first, and each tie in the comparison's order.
    /// </summary>
    internal static OutlierReading Of(
        IntervalComparison whole, IReadOnlyList<TimeRange> windows, IReadOnlyDictionary<string, long[]> counts)
    {
        var judged = new List<OutlierJudgement>();
        foreach (ComparedRelationship row in whole.Relationships)
        {
            if (row.Change == ComparisonChange.NotComparable)
            {
                continue;
            }

            long[] held = counts[row.Key];
            long records = row.Compared.Records;
            OutlierSide side = records > held.Max() ? OutlierSide.Above : records < held.Min() ? OutlierSide.Below : OutlierSide.Within;
            judged.Add(new(row, Array.AsReadOnly(held), side));
        }

        return new(whole, windows, [.. judged
            .OrderBy(row => row.Side == OutlierSide.Within)
            .ThenByDescending(row => row.Beyond)]);
    }
}
