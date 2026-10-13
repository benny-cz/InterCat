using Avalonia;
using InterCat.Desktop.Settings;

namespace InterCat.Desktop;

/// <summary>
/// Where InterCat's window opens from where it last closed (§26.3): on the screen that still shows the part of its title
/// bar a person takes hold of, moved inside that screen's working area and no larger than it, or where the system opens
/// a window when no screen shows it - as when the screen it closed on is gone.
/// </summary>
internal static class WindowPlacing
{
    /// <summary>
    /// The position, in a screen's pixels, and the size, in device-independent pixels, to open at; null to open where
    /// the system places a window. The size is never less than <paramref name="least"/>.
    /// </summary>
    public static (PixelPoint Position, Size Size)? Restore(
        WindowPlacement kept, IEnumerable<(PixelRect WorkingArea, double Scaling)> screens, Size least)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(screens);
        foreach ((PixelRect area, double scaling) in screens)
        {
            // Where a person takes hold of the window: just inside its title bar, toward its left.
            var grip = new PixelPoint(
                kept.X + (int)Math.Min(kept.Width * scaling / 2, 100 * scaling),
                kept.Y + (int)(8 * scaling));
            if (!area.Contains(grip)) continue;

            var size = new Size(
                Math.Clamp(kept.Width, least.Width, Math.Max(least.Width, area.Width / scaling)),
                Math.Clamp(kept.Height, least.Height, Math.Max(least.Height, area.Height / scaling)));
            int width = (int)Math.Ceiling(size.Width * scaling);
            int height = (int)Math.Ceiling(size.Height * scaling);
            return (new PixelPoint(
                Math.Clamp(kept.X, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(kept.Y, area.Y, Math.Max(area.Y, area.Bottom - height))), size);
        }

        return null;
    }
}
