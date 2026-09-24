using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class ViewportMathTests
{
    [Theory(DisplayName = "R10: pointer-focused zoom preserves its time focus")]
    [InlineData(50d)]
    [InlineData(250d)]
    [InlineData(500d)]
    [InlineData(850d)]
    [InlineData(950d)]
    public void ZoomPreservesPointerFocus(double pixel)
    {
        var extent = new TimeRange(0, 1_000_000);
        var viewport = new TimeRange(100_000, 900_000);
        long before = ViewportMath.TickAtPixel(viewport, pixel, 1_000);

        TimeRange zoomed = ViewportMath.ZoomAtPixel(viewport, pixel, 1_000, 1.25m, extent);
        long after = ViewportMath.TickAtPixel(zoomed, pixel, 1_000);

        Assert.InRange(Math.Abs(after - before), 0, 1);
    }

    [Fact(DisplayName = "R10: inverse zoom restores the viewport within rounding tolerance")]
    public void InverseZoomRestoresViewport()
    {
        var extent = new TimeRange(0, 2_000_000);
        var viewport = new TimeRange(400_000, 1_400_000);

        TimeRange zoomed = ViewportMath.ZoomAtPixel(viewport, 400, 1_000, 1.25m, extent);
        TimeRange restored = ViewportMath.ZoomAtPixel(zoomed, 400, 1_000, 0.8m, extent);

        Assert.InRange(Math.Abs(restored.StartTicks - viewport.StartTicks), 0, 1);
        Assert.InRange(Math.Abs(restored.EndTicks - viewport.EndTicks), 0, 1);
    }

    [Fact(DisplayName = "R10: a retained extent shorter than the minimum span zooms to itself rather than failing")]
    public void ShortExtentZoomsToItself()
    {
        var extent = new TimeRange(10, 210);

        TimeRange zoomed = ViewportMath.ZoomAtPixel(extent, 500, 1_000, 1.25m, extent, minimumSpanTicks: 100_000);

        Assert.Equal(extent, zoomed);
    }

    [Fact(DisplayName = "R10: panning cannot escape the retained extent")]
    public void PanClampsToRetainedExtent()
    {
        var extent = new TimeRange(0, 1_000);
        var viewport = new TimeRange(100, 300);

        TimeRange panned = ViewportMath.PanByFraction(viewport, -10m, extent);

        Assert.Equal(new TimeRange(0, 200), panned);
    }

    [Theory(DisplayName = "R10: centering a viewport on a tick keeps its span and clamps both edges")]
    [InlineData(-1_000L, -500L, -300L)]
    [InlineData(0L, -100L, 100L)]
    [InlineData(1_000L, 300L, 500L)]
    public void CenterOnTickClampsWithoutChangingSpan(long focus, long expectedStart, long expectedEnd)
    {
        var extent = new TimeRange(-500, 500);
        var viewport = new TimeRange(-100, 100);

        Assert.Equal(new TimeRange(expectedStart, expectedEnd),
            ViewportMath.CenterOnTick(viewport, focus, extent));
    }
}
