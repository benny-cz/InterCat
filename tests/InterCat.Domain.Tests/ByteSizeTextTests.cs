using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class ByteSizeTextTests
{
    [Fact(DisplayName = "§12.1: a size on disk reads in binary units, whole or to a tenth, with its digits in the reader's culture")]
    public void ASizeOnDiskReadsInBinaryUnits()
    {
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal(["0 B", "1,023 B", "1 KiB", "1.5 KiB", "214.3 MiB", "1 GiB", "2 GiB", "100 GiB", "1.5 TiB"],
            new long[]
            {
                0, 1_023, 1_024, 1_536, 224_712_294, 1L << 30, 2L << 30, 100L << 30, 3L << 39,
            }.Select(bytes => ByteSizeText.Of(bytes, english)));

        // A tenth is written as the reader's culture writes one, and so is a byte count's grouping.
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal(("1,5 KiB", "1.023 B"), (ByteSizeText.Of(1_536, german), ByteSizeText.Of(1_023, german)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSizeText.Of(-1));
    }
}
