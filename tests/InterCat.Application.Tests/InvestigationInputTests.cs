using System.Globalization;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// What a person types for an investigation's time, written back: a dialog filled with a stated duration, instant or drift
/// reads it back as stated, so aligning a session again from what it was aligned with changes nothing it did not mean to.
/// </summary>
public sealed class InvestigationInputTests
{
    [Fact(DisplayName = "§8.2: what an investigation's dialog writes of a stated duration, instant or drift reads back as stated, in any culture")]
    public void WhatIsWrittenReadsBackAsStated()
    {
        foreach (CultureInfo culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("en-US") })
        {
            foreach (long nanoseconds in new long[] { 0, 1, 999, 1_000, 1_500, 2_100_000, 10_000_000, 999_999_999, 1_000_000_000, 1_234_567_891 })
            {
                Assert.Equal(nanoseconds, InvestigationInput.Duration(InvestigationInput.WriteDuration(nanoseconds, culture)));
            }

            foreach (long nanoseconds in new long[] { 0, 1, -1, 750_000_000, -2_000_000_000, 13_000_100_000, 8_999_999_999_999_999_999 / 1_000 })
            {
                Assert.Equal(nanoseconds, InvestigationInput.Seconds(InvestigationInput.WriteSeconds(nanoseconds, culture)));
            }

            foreach (double rate in new[] { 0, 0.5, 50, 12.345, 1e-6 })
            {
                Assert.Equal(rate, InvestigationInput.PartsPerMillion(InvestigationInput.WritePartsPerMillion(rate, culture)));
            }
        }

        // A duration is written in the largest unit it fills, with no more digits than it has.
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal(["0 ns", "999 ns", "1.5 µs", "2.1 ms", "10 ms", "1.234567891 s"],
            new long[] { 0, 999, 1_500, 2_100_000, 10_000_000, 1_234_567_891 }.Select(value => InvestigationInput.WriteDuration(value, invariant)));
        Assert.Equal(("13.0001", "-0.75", "0,5"), (InvestigationInput.WriteSeconds(13_000_100_000, invariant),
            InvestigationInput.WriteSeconds(-750_000_000, invariant), InvestigationInput.WritePartsPerMillion(0.5, CultureInfo.GetCultureInfo("de-DE"))));
    }
}
