using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A moment a person typed, placed in a session (§6.2's time base, §6.7): a time of day on the wall clock its capture's
/// machine read, with its date or offset where they are needed, or session time with its unit - or why it cannot be.
/// </summary>
public sealed class SessionMomentTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(2), "east", "east");

    /// <summary>A session whose wall clock read noon UTC at its start and whose records span its first 2.5 s.</summary>
    private static readonly SessionWallClock Wall = new(0, Noon, null, 200);
    private static readonly TimeRange Extent = new(0, 25_000_000);

    [Fact(DisplayName = "§6.2: a time of day, or a number of seconds with a fraction or a unit, reads as a moment; a name, an endpoint or a bare number does not")]
    public void WhatReadsAsAMoment()
    {
        foreach (string moment in new[] { "14:32", "14:32:05", "14:32:05.120", "2026-09-29 14:32:05 UTC+02:00", "14:32Z",
                     "312.5 s", "312.5", "250 ms", "120 µs", "120 us", "800 ns", "+1.5 s", "-1.5 s" })
        {
            Assert.True(SessionMoment.IsMoment(moment, Invariant), moment);
        }

        foreach (string other in new[] { "", "312", "client.exe", "127.0.0.1:8080", "10.0.0.5", "1:443", "PID 100" })
        {
            Assert.False(SessionMoment.IsMoment(other, Invariant), other);
        }

        // A comma parts a fraction only where the culture writes one with it.
        Assert.True(SessionMoment.IsMoment("1,5 s", CultureInfo.GetCultureInfo("de-DE")));
        Assert.False(SessionMoment.IsMoment("1,5 s", Invariant));
    }

    [Fact(DisplayName = "§6.2: session time is placed in its unit, seconds unless it names another, and refused outside the session")]
    public void SessionTimeIsPlacedInItsUnit()
    {
        Assert.Equal(15_000_000, Place("1.5"));
        Assert.Equal(15_000_000, Place("+1.5 s"));
        Assert.Equal(2_500_000, Place("250 ms"));
        Assert.Equal(1_200, Place("120 µs"));
        Assert.Equal(1_200, Place("120 us"));
        Assert.Equal(8, Place("800 ns"));
        Assert.Equal(15_000_000, Place("1,5 s", CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("-1.5 s is outside this session, which runs 0.000 – 2.500 s in session time.", Refused("-1.5 s"));
        Assert.Equal("2.5 s is outside this session, which runs 0.000 – 2.500 s in session time.", Refused("2.5 s"));

        // Not a moment at all: nothing placed, and nothing to say.
        Assert.False(SessionMoment.TryPlace("client.exe", Wall, East, Extent, Invariant, out _, out string? none));
        Assert.Null(none);
    }

    [Fact(DisplayName = "§6.2: a time of day is placed on the wall clock in the reader's zone, or in the offset it names, on the day it names or the session's one day it falls on")]
    public void ATimeOfDayIsPlacedOnTheWallClock()
    {
        // 1.5 s into the session the wall clock read 12:00:01.5 UTC, 14:00:01.5 two hours east.
        Assert.Equal(15_000_000, Place("14:00:01.5"));
        Assert.Equal(15_000_000, Place("14:00:01,5"));
        Assert.Equal(15_000_000, Place("12:00:01.5 UTC"));
        Assert.Equal(15_000_000, Place("12:00:01.5Z"));
        Assert.Equal(15_000_000, Place("13:00:01.5 +01:00"));
        Assert.Equal(15_000_000, Place("13:00:01.5 UTC+01:00"));
        Assert.Equal(15_000_000, Place("2026-09-29 14:00:01.5"));
        Assert.Equal(15_000_000, Place("09/29/2026 14:00:01.5"));
        Assert.Equal(15_000_000, Place("29.09.2026 14:00:01,5", CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(0, Place("14:00"));

        // Outside the session, on another day, not a time, not a date, or with no wall clock to read it on: said why.
        Assert.Equal("15:00 is outside this session, which the wall clock read from 14:00:00.000 – 14:00:02.500 UTC+02:00.",
            Refused("15:00"));
        Assert.Equal("2026-09-30 14:00:01 is outside this session, which the wall clock read from 14:00:00.000 – "
            + "14:00:02.500 UTC+02:00.", Refused("2026-09-30 14:00:01"));
        Assert.StartsWith("25:00 is no time of day", Refused("25:00"), StringComparison.Ordinal);
        Assert.StartsWith("14:00 +15:00 is no time of day", Refused("14:00 +15:00"), StringComparison.Ordinal);
        Assert.StartsWith("2026-13-01 is no date: write it as ", Refused("2026-13-01 14:00"), StringComparison.Ordinal);
        Assert.False(SessionMoment.TryPlace("14:00:01", null, East, Extent, Invariant, out _, out string? unrecorded));
        Assert.Equal("This session recorded no wall clock, so 14:00:01 places nothing in it: type a session time, such as "
            + "312.5 s.", unrecorded);
    }

    [Fact(DisplayName = "§6.2: a time of day on two of a session's days asks for its date, and one its zone skipped or repeated says so")]
    public void DaysAndChangesOfOffsetAreSaid()
    {
        // A session of 26 hours reads 14:00:01 on both its days, and its date chooses one.
        var twoDays = new TimeRange(0, 26 * 36_000_000_000L);
        Assert.False(SessionMoment.TryPlace("14:00:01", Wall, East, twoDays, Invariant, out _, out string? both));
        Assert.Equal("14:00:01 falls on 2 of this session's days; add its date, as 09/29/2026 14:00:01.", both);
        Assert.True(SessionMoment.TryPlace("2026-09-30 14:00:01", Wall, East, twoDays, Invariant, out long second, out _));
        Assert.Equal(864_010_000_000, second);

        // Central European time leaves summer time at 03:00 on 25 October 2026, so 02:30 happened twice that night, and
        // entered it at 02:00 on 29 March, so 02:30 did not happen then.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        TimeZoneInfo central = TimeZoneInfo.CreateCustomTimeZone("central", TimeSpan.FromHours(1), "central", "central",
            "central summer", [rule]);
        var hours = new TimeRange(0, 4 * 36_000_000_000L);
        var autumn = new SessionWallClock(0, new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero), null, 200);
        Assert.False(SessionMoment.TryPlace("02:30", autumn, central, hours, Invariant, out _, out string? twice));
        Assert.Equal("02:30 happened twice on 10/25/2026 in this computer's zone; add its offset, UTC+02:00 or UTC+01:00.", twice);
        Assert.True(SessionMoment.TryPlace("02:30 +01:00", autumn, central, hours, Invariant, out long later, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), autumn.At(later));
        var spring = new SessionWallClock(0, new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), null, 200);
        Assert.False(SessionMoment.TryPlace("02:30", spring, central, hours, Invariant, out _, out string? skipped));
        Assert.Equal("02:30 did not happen on 03/29/2026 in this computer's zone: its clocks skipped it.", skipped);
    }

    [Fact(DisplayName = "§6.2: a wall-clock reading placed back in session time is where the wall clock read it, through the rate its calibration measured")]
    public void TheWallClockIsReadBack()
    {
        var fast = new SessionWallClock(500, Noon, 10.0, 200);
        foreach (long ticks in new long[] { 500, 12_500_250, 25_000_000, -1_000_000 })
        {
            Assert.Equal(ticks, fast.TicksAt(fast.At(ticks)));
        }

        Assert.Equal(500, Wall.TicksAt(Noon.AddTicks(500)));
    }

    private static long Place(string text, CultureInfo? culture = null)
    {
        Assert.True(SessionMoment.TryPlace(text, Wall, East, Extent, culture ?? Invariant, out long ticks, out string? problem),
            problem);
        return ticks;
    }

    private static string Refused(string text)
    {
        Assert.False(SessionMoment.TryPlace(text, Wall, East, Extent, Invariant, out _, out string? problem));
        return problem!;
    }
}
