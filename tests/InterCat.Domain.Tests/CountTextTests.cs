using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class CountTextTests
{
    [Fact(DisplayName = "§6.8: a count reads with its noun and verb in the number it takes, and its digits in the reader's culture")]
    public void ACountAgreesWithItsNounAndVerb()
    {
        Assert.Equal(("1 record", "0 records", "2 records"),
            (CountText.Of(1, "record"), CountText.Of(0, "record"), CountText.Of(2, "record")));
        Assert.Equal(("1 process", "3 processes", "1 batch", "2 batches"),
            (CountText.Of(1, "process", "processes"), CountText.Of(3, "process", "processes"),
                CountText.Of(1, "batch", "batches"), CountText.Of(2, "batch", "batches")));
        Assert.Equal(("is", "are", "are"), (CountText.Agree(1, "is", "are"), CountText.Agree(0, "is", "are"), CountText.Agree(7, "is", "are")));

        // A large count is grouped as the reader's culture groups digits.
        CultureInfo shown = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.234 records", CountText.Of(1_234, "record"));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("1,234 records", CountText.Of(1_234, "record"));
        }
        finally
        {
            CultureInfo.CurrentCulture = shown;
        }

        Assert.Throws<ArgumentNullException>(() => CountText.Of(1, null!));
    }
}
