using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// The §6.7 navigation properties, generated over adversarial viewports rather than demonstrated on one
/// example (R10). Every case is produced from a printed seed, so a failure is reproducible (§13.6).
/// </summary>
public sealed class NavigationProperties
{
    private const int Cases = 500;

    /// <summary>Seeds are fixed so a run is deterministic; a failure message prints the case that broke.</summary>
    public static TheoryData<int> Seeds => [1, 7, 19, 4242, 20_260_921];

    [Theory(DisplayName = "R10: the time under the pointer survives a zoom across generated viewports")]
    [MemberData(nameof(Seeds))]
    public void PointerFocusSurvivesZoom(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            Scenario scenario = Scenario.Generate(random);
            long focusBefore = ViewportMath.TickAtPixel(scenario.Viewport, scenario.FocusPixel, scenario.Width);
            TimeRange zoomed = ViewportMath.ZoomAtPixel(
                scenario.Viewport,
                scenario.FocusPixel,
                scenario.Width,
                scenario.ZoomFactor,
                scenario.Extent);
            long focusAfter = ViewportMath.TickAtPixel(zoomed, scenario.FocusPixel, scenario.Width);

            // The focus can only move when a clamp pulled the viewport back inside the retained extent.
            bool clamped = zoomed.StartTicks == scenario.Extent.StartTicks
                || zoomed.EndTicks == scenario.Extent.EndTicks
                || zoomed.SpanTicks == scenario.Extent.SpanTicks;
            long tolerance = Math.Max(2, (zoomed.SpanTicks / (long)scenario.Width) + 1);
            if (!clamped)
            {
                Assert.True(
                    Math.Abs(focusAfter - focusBefore) <= tolerance,
                    scenario.Describe(seed, index, $"focus moved from {focusBefore} to {focusAfter}, tolerance {tolerance}"));
            }

            Assert.True(zoomed.SpanTicks > 0, scenario.Describe(seed, index, "zoom produced an empty viewport"));
            Assert.True(
                zoomed.StartTicks >= scenario.Extent.StartTicks && zoomed.EndTicks <= scenario.Extent.EndTicks,
                scenario.Describe(seed, index, "zoom escaped the retained extent"));
        }
    }

    [Theory(DisplayName = "R10: zooming in and back out restores the viewport unless a clamp intervened")]
    [MemberData(nameof(Seeds))]
    public void ZoomInAndOutRestoresTheViewport(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            Scenario scenario = Scenario.Generate(random);
            TimeRange zoomedIn = ViewportMath.ZoomAtPixel(
                scenario.Viewport,
                scenario.FocusPixel,
                scenario.Width,
                scenario.ZoomFactor,
                scenario.Extent);
            TimeRange restored = ViewportMath.ZoomAtPixel(
                zoomedIn,
                scenario.FocusPixel,
                scenario.Width,
                1m / scenario.ZoomFactor,
                scenario.Extent);

            bool clamped = zoomedIn.SpanTicks == scenario.Extent.SpanTicks
                || restored.SpanTicks == scenario.Extent.SpanTicks
                || restored.StartTicks == scenario.Extent.StartTicks
                || restored.EndTicks == scenario.Extent.EndTicks;
            if (clamped)
            {
                continue;
            }

            long tolerance = Math.Max(4, (scenario.Viewport.SpanTicks / (long)scenario.Width) + 1);
            Assert.True(
                Math.Abs(restored.StartTicks - scenario.Viewport.StartTicks) <= tolerance,
                scenario.Describe(seed, index, $"start drifted to {restored.StartTicks} from {scenario.Viewport.StartTicks}"));
            Assert.True(
                Math.Abs(restored.SpanTicks - scenario.Viewport.SpanTicks) <= tolerance,
                scenario.Describe(seed, index, $"span drifted to {restored.SpanTicks} from {scenario.Viewport.SpanTicks}"));
        }
    }

    [Theory(DisplayName = "R10: no sequence of pans escapes the retained extent")]
    [MemberData(nameof(Seeds))]
    public void PanNeverEscapesTheRetainedExtent(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            Scenario scenario = Scenario.Generate(random);
            TimeRange current = scenario.Viewport;
            for (int step = 0; step < 8; step++)
            {
                decimal fraction = (decimal)((random.NextDouble() * 4.0) - 2.0);
                current = ViewportMath.PanByFraction(current, fraction, scenario.Extent);
                Assert.True(
                    current.StartTicks >= scenario.Extent.StartTicks,
                    scenario.Describe(seed, index, $"pan left past the extent to {current.StartTicks}"));
                Assert.True(
                    current.EndTicks <= scenario.Extent.EndTicks,
                    scenario.Describe(seed, index, $"pan right past the extent to {current.EndTicks}"));
            }
        }
    }

    [Theory(DisplayName = "R10: panning and back restores the viewport")]
    [MemberData(nameof(Seeds))]
    public void PanAndBackRestoresTheViewport(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            Scenario scenario = Scenario.Generate(random);
            decimal fraction = (decimal)Math.Round((random.NextDouble() * 0.5) + 0.05, 3);
            TimeRange panned = ViewportMath.PanByFraction(scenario.Viewport, fraction, scenario.Extent);
            TimeRange restored = ViewportMath.PanByFraction(panned, -fraction, scenario.Extent);

            bool clamped = panned.StartTicks == scenario.Extent.StartTicks
                || panned.EndTicks == scenario.Extent.EndTicks
                || restored.StartTicks == scenario.Extent.StartTicks
                || restored.EndTicks == scenario.Extent.EndTicks;
            if (clamped)
            {
                continue;
            }

            Assert.True(
                Math.Abs(restored.StartTicks - scenario.Viewport.StartTicks) <= 2,
                scenario.Describe(seed, index, $"pan round trip landed on {restored.StartTicks}"));
        }
    }

    [Theory(DisplayName = "I19: navigation math depends only on its inputs, never on workspace state")]
    [MemberData(nameof(Seeds))]
    public void ClampingDependsOnlyOnTheExtentAndViewport(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            Scenario scenario = Scenario.Generate(random);

            // The same inputs must produce the same result whatever else the workspace is doing: the math
            // takes no layout state, no alignment revision and no wall-clock reading (I10, R10).
            TimeRange first = ViewportMath.ZoomAtPixel(
                scenario.Viewport,
                scenario.FocusPixel,
                scenario.Width,
                scenario.ZoomFactor,
                scenario.Extent);
            TimeRange second = ViewportMath.ZoomAtPixel(
                scenario.Viewport,
                scenario.FocusPixel,
                scenario.Width,
                scenario.ZoomFactor,
                scenario.Extent);

            Assert.Equal(first, second);
        }
    }

    private readonly record struct Scenario(
        TimeRange Extent,
        TimeRange Viewport,
        double FocusPixel,
        double Width,
        decimal ZoomFactor)
    {
        public static Scenario Generate(Random random)
        {
            long extentStart = random.NextInt64(-1_000_000_000L, 1_000_000_000L);
            long extentSpan = random.NextInt64(1_000, 10_000_000_000L);
            var extent = new TimeRange(extentStart, checked(extentStart + extentSpan));

            long viewportSpan = Math.Max(2, (long)(extentSpan * (random.NextDouble() * 0.9 + 0.01)));
            long maximumStart = extent.EndTicks - viewportSpan;
            long viewportStart = maximumStart <= extent.StartTicks
                ? extent.StartTicks
                : random.NextInt64(extent.StartTicks, maximumStart);
            var viewport = new TimeRange(viewportStart, checked(viewportStart + viewportSpan));

            double width = random.Next(320, 3_840);
            double focus = random.NextDouble() * width;
            decimal factor = random.Next(0, 2) == 0 ? 1.25m : 2.0m;
            return new(extent, viewport, focus, width, factor);
        }

        public string Describe(int seed, int index, string failure) => string.Create(
            CultureInfo.InvariantCulture,
            $"seed {seed}, case {index}: {failure}. extent [{Extent.StartTicks}, {Extent.EndTicks}), "
            + $"viewport [{Viewport.StartTicks}, {Viewport.EndTicks}), focus {FocusPixel:F2} of {Width:F0} px, "
            + $"factor {ZoomFactor}");
    }
}
