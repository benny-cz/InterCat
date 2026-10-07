using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// A size on disk or a quota as a person reads it wherever InterCat states one, in binary units, as a capture's limits are
/// set and §12.1's tiers are drawn: "512 B", "1 GiB", "214.3 MiB". The exact byte count is for the tables that list files.
/// </summary>
public static class ByteSizeText
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    /// <summary>
    /// <paramref name="bytes"/> in the largest unit it fills, to a tenth where it is not whole; in the reader's culture
    /// unless another is given.
    /// </summary>
    public static string Of(long bytes, CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        culture ??= CultureInfo.CurrentCulture;
        double scaled = bytes;
        int unit = 0;
        while (scaled >= 1024 && unit < Units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", culture)} B"
            : $"{scaled.ToString(scaled % 1 == 0 ? "0" : "0.#", culture)} {Units[unit]}";
    }
}
