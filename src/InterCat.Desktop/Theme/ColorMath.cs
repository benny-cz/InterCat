using System.Globalization;

namespace InterCat.Desktop.Theme;

/// <summary>A colour-vision deficiency the palette is verified under (§6.6).</summary>
public enum VisionModel
{
    Normal = 1,
    Protanopia = 2,
    Deuteranopia = 3,
    Tritanopia = 4,
    Greyscale = 5,
}

/// <summary>An 8-bit sRGB colour. Tokens are authored as hex and compared as numbers, never by eye.</summary>
public readonly record struct Srgb(byte R, byte G, byte B)
{
    public static Srgb Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        ReadOnlySpan<char> value = hex.AsSpan().TrimStart('#');
        if (value.Length != 6)
        {
            throw new FormatException($"'{hex}' is not a six-digit hexadecimal colour.");
        }

        return new(
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    public override string ToString() => ToHex();
}

/// <summary>
/// Colour measurements the palette contract is enforced with: contrast against a ground, perceptual
/// separation between neighbours, and the same measurements under simulated colour-vision deficiency.
/// Every value here is computed, so a palette change fails a test rather than a review (§6.6).
/// </summary>
public static class ColorMath
{
    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    public static double RelativeLuminance(Srgb color)
    {
        (double r, double g, double b) = ToLinear(color);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>WCAG contrast ratio between two colours, always at least 1.</summary>
    public static double ContrastRatio(Srgb first, Srgb second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        (double lighter, double darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>CIE76 colour difference in CIELAB. Adjacent palette entries must clear a stated minimum.</summary>
    public static double PerceptualDistance(Srgb first, Srgb second)
    {
        (double l1, double a1, double b1) = ToLab(first);
        (double l2, double a2, double b2) = ToLab(second);
        double dl = l1 - l2;
        double da = a1 - a2;
        double db = b1 - b2;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    /// <summary>Lightness difference in CIELAB, which is what survives a greyscale print.</summary>
    public static double LightnessDistance(Srgb first, Srgb second)
    {
        (double l1, _, _) = ToLab(first);
        (double l2, _, _) = ToLab(second);
        return Math.Abs(l1 - l2);
    }

    /// <summary>
    /// Simulates how a colour is seen under one vision model, using the Viénot, Brettel and Mollon
    /// dichromat projection in LMS space.
    /// </summary>
    public static Srgb Simulate(Srgb color, VisionModel model)
    {
        if (model == VisionModel.Normal)
        {
            return color;
        }

        (double r, double g, double b) = ToLinear(color);
        if (model == VisionModel.Greyscale)
        {
            double luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            return FromLinear(luminance, luminance, luminance);
        }

        double l = (17.8824 * r) + (43.5161 * g) + (4.11935 * b);
        double m = (3.45565 * r) + (27.1554 * g) + (3.86714 * b);
        double s = (0.0299566 * r) + (0.184309 * g) + (1.46709 * b);

        (l, m, s) = model switch
        {
            VisionModel.Protanopia => ((2.02344 * m) - (2.52581 * s), m, s),
            VisionModel.Deuteranopia => (l, (0.494207 * l) + (1.24827 * s), s),
            VisionModel.Tritanopia => (l, m, (-0.395913 * l) + (0.801109 * m)),
            _ => (l, m, s),
        };

        double red = (0.0809444479 * l) - (0.130504409 * m) + (0.116721066 * s);
        double green = (-0.0102485335 * l) + (0.0540193266 * m) - (0.113614708 * s);
        double blue = (-0.000365296938 * l) - (0.00412161469 * m) + (0.693511405 * s);
        return FromLinear(red, green, blue);
    }

    private static (double R, double G, double B) ToLinear(Srgb color) =>
        (Linearize(color.R), Linearize(color.G), Linearize(color.B));

    private static double Linearize(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Srgb FromLinear(double r, double g, double b) =>
        new(Encode(r), Encode(g), Encode(b));

    private static byte Encode(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        double encoded = clamped <= 0.0031308
            ? clamped * 12.92
            : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255.0), 0, 255);
    }

    private static (double L, double A, double B) ToLab(Srgb color)
    {
        (double r, double g, double b) = ToLinear(color);
        double x = ((0.4124 * r) + (0.3576 * g) + (0.1805 * b)) / 0.95047;
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double z = ((0.0193 * r) + (0.1192 * g) + (0.9505 * b)) / 1.08883;
        double fx = Pivot(x);
        double fy = Pivot(y);
        double fz = Pivot(z);
        return ((116.0 * fy) - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    private static double Pivot(double value)
    {
        const double Epsilon = 216.0 / 24389.0;
        const double Kappa = 24389.0 / 27.0;
        return value > Epsilon ? Math.Cbrt(value) : ((Kappa * value) + 16.0) / 116.0;
    }
}
