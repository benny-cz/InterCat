using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The time base a view reads a session's instants in (§6.2): session time, counted from the moment its capture began, or
/// the wall clock the capture's machine read (<see cref="SessionWallClock"/>), in a zone whose offset from UTC is stated
/// with every wall-clock time. One mapping for every instant, range and record time a view states (R5), so the axis, a
/// card, the inspector and the evidence never read in two bases at once.
/// </summary>
public sealed class SessionClock
{
    /// <summary>What the wall clock is called wherever a view names its time base.</summary>
    public const string WallClockWords = "wall clock";

    /// <summary>Why a session's instants cannot be read on the wall clock: its capture recorded none.</summary>
    public const string NotRecorded = "This session recorded no wall clock - imports, redacted packages and older captures "
        + "record none - so its instants are read in session time alone.";

    private readonly bool spansDays;

    private SessionClock(SessionWallClock? wallClock, TimeZoneInfo zone, bool spansDays)
    {
        WallClock = wallClock;
        Zone = zone;
        this.spansDays = spansDays;
    }

    /// <summary>Session time, which a view reads by default, its start said in <paramref name="zone"/>.</summary>
    public static SessionClock Session(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return new(null, zone, spansDays: false);
    }

    /// <summary>
    /// The wall clock <paramref name="wallClock"/> places, in <paramref name="zone"/>. A record's time names its date too
    /// when the session's <paramref name="extent"/> spans more than one day there, so no two records read alike.
    /// </summary>
    public static SessionClock Wall(SessionWallClock wallClock, TimeZoneInfo zone, TimeRange extent)
    {
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset first = TimeZoneInfo.ConvertTime(wallClock.At(extent.StartTicks), zone);
        DateTimeOffset last = TimeZoneInfo.ConvertTime(wallClock.At(Math.Max(extent.StartTicks, extent.EndTicks - 1)), zone);
        return new(wallClock, zone, first.Date != last.Date);
    }

    /// <summary>The wall clock this base reads, or null for session time.</summary>
    public SessionWallClock? WallClock { get; }

    /// <summary>The zone a wall-clock time, and session time's start, is said in.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Whether instants are read on the wall clock.</summary>
    public bool IsWallClock => WallClock is not null;

    /// <summary>The time base's own name: "session time" or "wall clock".</summary>
    public string Name => IsWallClock ? WallClockWords : WorkspaceTime.SessionTimeWords;

    /// <summary>
    /// One instant, to the digits a visible span of <paramref name="span"/> ticks needs, as an axis labels its edges:
    /// "312.5 s", or on the wall clock "14:32:05.1", whose offset the axis states once.
    /// </summary>
    public string Instant(long ticks, long span, IFormatProvider? culture = null) => WallClock is null
        ? WorkspaceTime.FormatInstant(ticks, span, culture)
        : TimeOfDay(Local(ticks), Digits(span), culture);

    /// <summary>
    /// The round instants a visible span may be ticked at, a ladder width apart (§6.2), each with its label to exactly the
    /// digits the width needs, so a label names the instant its tick is drawn at: in session time the multiples of the
    /// width, "15 s"; on the wall clock the times of day on multiples of it, "09:00:10", each placed at the instant of
    /// session time the wall clock read it. Only instants strictly inside the span are given, in order, into
    /// <paramref name="ticks"/>, which is emptied first.
    /// </summary>
    public void AxisTicks(TimeRange visible, long width, ICollection<(long Ticks, string Label)> ticks, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ticks.Clear();
        if (WallClock is not { } wall)
        {
            for (long instant = (Math.DivRem(visible.StartTicks, width, out long past) + (past > 0 ? 1 : 0)) * width;
                instant < visible.EndTicks; instant += width)
            {
                if (instant > visible.StartTicks)
                {
                    ticks.Add((instant, WorkspaceTime.FormatTick(instant, width, visible.SpanTicks, culture)));
                }
            }

            return;
        }

        // Times of day on multiples of the width from the day's start, in the zone the view reads; each placed where the
        // wall clock read it, and named from the round time itself, never from the instant it maps back to.
        DateTimeOffset start = Local(visible.StartTicks);
        long sinceMidnight = start.TimeOfDay.Ticks;
        long first = (sinceMidnight / width + (sinceMidnight % width > 0 ? 1 : 0)) * width;
        int digits = 0;
        for (long fraction = width % TimeSpan.TicksPerSecond; fraction != 0 && digits < 7; fraction = fraction * 10 % TimeSpan.TicksPerSecond)
        {
            digits++;
        }

        // A wall clock that read no later for a later instant would never leave the span: no axis needs more than its pixels.
        DateTimeOffset round = new DateTimeOffset(start.Date, start.Offset).AddTicks(first).ToUniversalTime();
        for (int count = 0; count < MostAxisTicks; count++, round = round.AddTicks(width))
        {
            long instant = wall.TicksAt(round);
            if (instant >= visible.EndTicks)
            {
                return;
            }

            if (instant > visible.StartTicks)
            {
                ticks.Add((instant, TimeOfDay(TimeZoneInfo.ConvertTime(round, Zone), digits, culture)));
            }
        }
    }

    /// <summary>
    /// A range in the unit or digits its span needs: "0.120 – 0.250 s", or on the wall clock
    /// "14:32:05.120 – 14:32:05.250 UTC+02:00", dated where it crosses a day and each end offset where the zone's offset
    /// changes between them.
    /// </summary>
    public string Range(TimeRange range, IFormatProvider? culture = null)
    {
        if (WallClock is null)
        {
            return WorkspaceTime.FormatRange(range, culture);
        }

        (string from, string to, string? fromOffset, string offset) = Bounds(range, culture);
        return fromOffset is null ? $"{from} – {to} {offset}" : $"{from} {fromOffset} – {to} {offset}";
    }

    /// <summary>
    /// The same range with its half-open boundary made visible, where inclusion at an edge matters: "[0.120, 0.250) s",
    /// or on the wall clock "[14:32:05.120, 14:32:05.250) UTC+02:00".
    /// </summary>
    public string HalfOpenRange(TimeRange range, IFormatProvider? culture = null)
    {
        if (WallClock is null)
        {
            return WorkspaceTime.FormatHalfOpenRange(range, culture);
        }

        (string from, string to, string? fromOffset, string offset) = Bounds(range, culture);
        string separator = WorkspaceTime.BoundSeparator(culture ?? CultureInfo.CurrentCulture);
        return fromOffset is null
            ? $"[{from}{separator} {to}) {offset}"
            : $"[{from} {fromOffset}{separator} {to}) {offset}";
    }

    /// <summary>
    /// A record's time as the evidence states it: "+312.513000 s" since its capture began, or on the wall clock
    /// "14:32:05.120371", to the microsecond, dated when the session spans days or <paramref name="dated"/> asks, whose
    /// offset the evidence states once; or that it has none.
    /// </summary>
    public string Record(long? sessionNanoseconds, IFormatProvider? culture = null, bool dated = false)
    {
        if (sessionNanoseconds is not { } nanoseconds)
        {
            return "time unavailable";
        }

        if (WallClock is null)
        {
            return string.Create(culture ?? CultureInfo.CurrentCulture,
                $"{(nanoseconds < 0 ? "−" : "+")}{Math.Abs((decimal)nanoseconds) / 1_000_000_000m:0.000000} s");
        }

        DateTimeOffset local = Local(FloorTicks(nanoseconds));
        string time = TimeOfDay(local, 6, culture);
        return spansDays || dated ? local.ToString("d", culture ?? CultureInfo.CurrentCulture) + " " + time : time;
    }

    /// <summary>
    /// One record's time stated on its own, as the inspector states a selected record's: its time as a list states it, and
    /// on the wall clock its date and offset too, since nothing beside it says them.
    /// </summary>
    public string Moment(long? sessionNanoseconds, IFormatProvider? culture = null)
    {
        if (WallClock is null || sessionNanoseconds is not { } nanoseconds)
        {
            return Record(sessionNanoseconds, culture);
        }

        DateTimeOffset local = Local(FloorTicks(nanoseconds));
        return local.ToString("d", culture ?? CultureInfo.CurrentCulture) + " " + TimeOfDay(local, 6, culture) + " "
            + OffsetText(local.Offset);
    }

    /// <summary>
    /// The time base the axis states under the instants of <paramref name="visible"/>, at all times (§6.2): session time
    /// since the moment its capture <paramref name="began"/>, or the wall clock with the date it reads - both dates where
    /// the view crosses a day - and its offset from UTC: "wall clock on 10/06/2026, UTC+02:00".
    /// </summary>
    public string Base(TimeRange visible, DateTimeOffset? began, IFormatProvider? culture = null)
    {
        if (WallClock is null)
        {
            return WorkspaceTime.TimeBase(began, Zone, culture);
        }

        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        DateTimeOffset first = Local(visible.StartTicks);
        DateTimeOffset last = Local(Math.Max(visible.StartTicks, visible.EndTicks - 1));
        string offsets = first.Offset == last.Offset
            ? OffsetText(first.Offset)
            : OffsetText(first.Offset) + " to " + OffsetText(last.Offset);
        return first.Date == last.Date
            ? string.Create(provider, $"{WallClockWords} on {first.DateTime:d}, {offsets}")
            : string.Create(provider, $"{WallClockWords} from {first.DateTime:d} to {last.DateTime:d}, {offsets}");
    }

    /// <summary>
    /// What a reading on the wall clock rests on, for the choice's tooltip and the inspector: which readings it was placed
    /// through and how far apart each pair was taken, and that how right that clock was is not known.
    /// </summary>
    public string Basis(IFormatProvider? culture = null)
    {
        if (WallClock is not { } wall)
        {
            return "Session time counts from the moment the capture began.";
        }

        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        string placed = wall.PartsPerMillion is { } rate
            ? string.Create(provider, $"placed on the line through its readings at the capture's start and stop, which ran {rate:+0.0;-0.0;0.0} ppm against the capture's own clock")
            : "placed from its reading at the capture's start, at the capture's own clock's rate, since no stop was recorded";
        return "The wall clock the capture's machine read, " + placed + ", each reading taken within ±"
            + OperationText.DurationAtLeast(wall.UncertaintyNanoseconds, provider) + " of the capture's clock. How "
            + "right that clock was is not known from it.";
    }

    /// <summary>
    /// The offsets from UTC the wall clock is read in at these instants of session time, in order, each once: "UTC+02:00",
    /// or "UTC+02:00, then UTC+01:00" across a change of the zone's offset. Empty for session time or no instant.
    /// </summary>
    public string OffsetsAt(IEnumerable<long> ticks)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (WallClock is null)
        {
            return string.Empty;
        }

        var offsets = new List<TimeSpan>();
        foreach (long instant in ticks)
        {
            TimeSpan offset = Local(instant).Offset;
            if (offsets.Count == 0 || offsets[^1] != offset)
            {
                offsets.Add(offset);
            }
        }

        return string.Join(", then ", offsets.Select(OffsetText));
    }

    /// <summary>A wall-clock time's offset from UTC, as it is always stated with one: "UTC" or "UTC+02:00".</summary>
    public static string OffsetText(TimeSpan offset) => offset == TimeSpan.Zero ? "UTC"
        : string.Create(CultureInfo.InvariantCulture, $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset:hh\\:mm}");

    /// <summary>A record's session time in presentation ticks, toward the earlier tick where it falls between two.</summary>
    private static long FloorTicks(long nanoseconds) => Math.DivRem(nanoseconds, 100L) is var (quotient, remainder) && remainder < 0
        ? quotient - 1
        : quotient;

    /// <summary>The wall clock's reading at an instant of session time, in the zone.</summary>
    private DateTimeOffset Local(long ticks) => TimeZoneInfo.ConvertTime(WallClock!.At(ticks), Zone);

    /// <summary>
    /// A range's two ends in the digits its span needs, dated where they fall on different days, with the offset each is
    /// said in: the first end's only where it differs from the second's.
    /// </summary>
    private (string From, string To, string? FromOffset, string Offset) Bounds(TimeRange range, IFormatProvider? culture)
    {
        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        DateTimeOffset start = Local(range.StartTicks);
        DateTimeOffset end = Local(range.EndTicks);
        int digits = Digits(range.SpanTicks);
        bool dated = start.Date != end.Date;
        string from = (dated ? start.ToString("d", provider) + " " : string.Empty) + TimeOfDay(start, digits, provider);
        string to = (dated ? end.ToString("d", provider) + " " : string.Empty) + TimeOfDay(end, digits, provider);
        return (from, to, start.Offset == end.Offset ? null : OffsetText(start.Offset), OffsetText(end.Offset));
    }

    /// <summary>
    /// A time of day on a 24-hour clock, its seconds to <paramref name="digits"/> decimal places, cut rather than rounded so
    /// no time reads as the next second: "14:32:05.120".
    /// </summary>
    private static string TimeOfDay(DateTimeOffset local, int digits, IFormatProvider? culture)
    {
        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        string separator = DateTimeFormatInfo.GetInstance(provider).TimeSeparator;
        long fraction = local.Ticks % TimeSpan.TicksPerSecond / Pow10(7 - digits);
        string seconds = string.Create(CultureInfo.InvariantCulture,
            $"{local.Hour:D2}{separator}{local.Minute:D2}{separator}{local.Second:D2}");
        return digits == 0
            ? seconds
            : seconds + NumberFormatInfo.GetInstance(provider).NumberDecimalSeparator
                + fraction.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>More ticks than any axis draws: a bound on the times of day a span is searched for.</summary>
    private const int MostAxisTicks = 10_000;

    /// <summary>The decimal places a span needs, as session time escalates its unit (§6.2): tenths, ms, µs, 100 ns.</summary>
    private static int Digits(long span) => span switch
    {
        >= 10 * WorkspaceTime.TicksPerSecond => 1,
        >= WorkspaceTime.TicksPerSecond / 100 => 3,
        >= WorkspaceTime.TicksPerSecond / 100_000 => 6,
        _ => 7,
    };

    private static long Pow10(int power)
    {
        long value = 1;
        for (int step = 0; step < power; step++)
        {
            value *= 10;
        }

        return value;
    }
}
