using Avalonia;
using InterCat.Desktop.Settings;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Where InterCat's window opens from where it last closed (§26.3): on a screen that still shows its title bar, inside
/// that screen and no larger than it, or where the system places it when none does.
/// </summary>
public sealed class WindowPlacingTests
{
    private static readonly Size Least = new(1_080, 700);
    private static readonly (PixelRect, double)[] OneScreen = [(new PixelRect(0, 0, 1_920, 1_040), 1)];

    [Fact(DisplayName = "§26.3: a window that closed on a screen still there opens where it closed, as large")]
    public void AWindowOpensWhereItClosed()
    {
        Assert.Equal((new PixelPoint(200, 120), new Size(1_300, 800)),
            WindowPlacing.Restore(new WindowPlacement(200, 120, 1_300, 800, false), OneScreen, Least));

        // On a second screen, to the left of the first and drawn at half as large again, it opens there too.
        (PixelRect, double)[] two = [.. OneScreen, (new PixelRect(-2_880, 0, 2_880, 1_560), 1.5)];
        Assert.Equal((new PixelPoint(-2_700, 100), new Size(1_400, 900)),
            WindowPlacing.Restore(new WindowPlacement(-2_700, 100, 1_400, 900, true), two, Least));

        // One that reached across onto the first opens wholly on the screen that shows its title bar.
        Assert.Equal((new PixelPoint(-2_100, 100), new Size(1_400, 900)),
            WindowPlacing.Restore(new WindowPlacement(-2_000, 100, 1_400, 900, false), two, Least));
    }

    [Fact(DisplayName = "§26.3: a window whose screen is gone, or whose title bar no screen shows, opens where the system places it")]
    public void AWindowNoScreenShowsOpensWhereTheSystemPlacesIt()
    {
        // The screen it closed on, to the right of this one, was unplugged.
        Assert.Null(WindowPlacing.Restore(new WindowPlacement(2_100, 100, 1_300, 800, false), OneScreen, Least));

        // Its title bar is above the top of the only screen, where no pointer can take hold of it.
        Assert.Null(WindowPlacing.Restore(new WindowPlacement(200, -60, 1_300, 800, false), OneScreen, Least));

        // Only its right edge is on the screen, left of which there is none.
        Assert.Null(WindowPlacing.Restore(new WindowPlacement(-1_250, 100, 1_300, 800, false), OneScreen, Least));
        Assert.Null(WindowPlacing.Restore(new WindowPlacement(200, 100, 1_300, 800, false), [], Least));
    }

    [Fact(DisplayName = "§26.3: a window partly off its screen, or larger than it is now, is moved inside it and made to fit, never smaller than the least the window takes")]
    public void AWindowIsMovedInsideItsScreenAndFits()
    {
        // Its left part shown on the screen, its right beyond the edge: moved left until it is all there.
        Assert.Equal((new PixelPoint(620, 100), new Size(1_300, 800)),
            WindowPlacing.Restore(new WindowPlacement(1_700, 100, 1_300, 800, false), OneScreen, Least));

        // Larger than the screen it opens on now, a smaller one: as large as the screen's working area, at its corner.
        Assert.Equal((new PixelPoint(0, 0), new Size(1_920, 1_040)),
            WindowPlacing.Restore(new WindowPlacement(40, 30, 2_560, 1_400, false), OneScreen, Least));

        // Drawn at half as large again, the screen holds fewer of the window's own pixels.
        (PixelRect, double)[] scaled = [(new PixelRect(0, 0, 1_920, 1_080), 1.5)];
        Assert.Equal((new PixelPoint(0, 0), new Size(1_280, 720)),
            WindowPlacing.Restore(new WindowPlacement(10, 10, 1_600, 900, false), scaled, Least));

        // Never smaller than the least the window takes, even on a screen smaller than that.
        Assert.Equal(new Size(1_080, 700),
            WindowPlacing.Restore(new WindowPlacement(100, 100, 500, 300, false), OneScreen, Least)!.Value.Size);
        (PixelRect, double)[] small = [(new PixelRect(0, 0, 1_024, 600), 1)];
        Assert.Equal((new PixelPoint(0, 0), new Size(1_080, 700)),
            WindowPlacing.Restore(new WindowPlacement(100, 50, 1_200, 800, false), small, Least));
    }
}
