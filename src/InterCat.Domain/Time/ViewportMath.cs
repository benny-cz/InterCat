namespace InterCat.Domain;

/// <summary>Pure navigation math. Stored times remain integer ticks (R10).</summary>
public static class ViewportMath
{
    public static long TickAtPixel(TimeRange viewport, double pixel, double width)
    {
        if (!double.IsFinite(pixel))
        {
            throw new ArgumentOutOfRangeException(nameof(pixel));
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        decimal ratio = decimal.CreateChecked(Math.Clamp(pixel / width, 0d, 1d));
        decimal offset = decimal.CreateChecked(viewport.SpanTicks) * ratio;
        long roundedOffset = decimal.ToInt64(decimal.Round(offset, 0, MidpointRounding.AwayFromZero));
        return checked(viewport.StartTicks + Math.Min(roundedOffset, viewport.SpanTicks));
    }

    public static double PixelAtTick(TimeRange viewport, long ticks, double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        long clamped = Math.Clamp(ticks, viewport.StartTicks, viewport.EndTicks);
        decimal ratio = decimal.CreateChecked(clamped - viewport.StartTicks) / viewport.SpanTicks;
        return double.CreateChecked(ratio) * width;
    }

    public static TimeRange ZoomAtPixel(
        TimeRange viewport,
        double focusPixel,
        double width,
        decimal zoomFactor,
        TimeRange retainedExtent,
        long minimumSpanTicks = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(zoomFactor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSpanTicks);

        long focusTick = TickAtPixel(viewport, focusPixel, width);
        decimal focusRatio = decimal.CreateChecked(focusTick - viewport.StartTicks) / viewport.SpanTicks;
        decimal desiredSpan = decimal.CreateChecked(viewport.SpanTicks) / zoomFactor;

        // A retained extent shorter than the minimum span is itself the narrowest view: zooming it changes nothing rather
        // than failing on an inverted clamp.
        long newSpan = Math.Clamp(
            decimal.ToInt64(decimal.Round(desiredSpan, 0, MidpointRounding.AwayFromZero)),
            Math.Min(minimumSpanTicks, retainedExtent.SpanTicks),
            retainedExtent.SpanTicks);

        long leftTicks = decimal.ToInt64(decimal.Round(newSpan * focusRatio, 0, MidpointRounding.AwayFromZero));
        long start = checked(focusTick - leftTicks);
        return new TimeRange(start, checked(start + newSpan)).ClampInside(retainedExtent);
    }

    public static TimeRange PanByFraction(TimeRange viewport, decimal fraction, TimeRange retainedExtent)
    {
        decimal desiredOffset = decimal.CreateChecked(viewport.SpanTicks) * fraction;
        long offset = decimal.ToInt64(decimal.Round(desiredOffset, 0, MidpointRounding.AwayFromZero));
        return new TimeRange(
            checked(viewport.StartTicks + offset),
            checked(viewport.EndTicks + offset)).ClampInside(retainedExtent);
    }

    /// <summary>Moves an unchanged-width viewport to a tick, clamping its edges inside the retained extent.</summary>
    public static TimeRange CenterOnTick(TimeRange viewport, long focusTick, TimeRange retainedExtent)
    {
        long span = Math.Min(viewport.SpanTicks, retainedExtent.SpanTicks);
        decimal start = decimal.Round(decimal.CreateChecked(focusTick) - (decimal.CreateChecked(span) / 2),
            0, MidpointRounding.AwayFromZero);
        long clamped = decimal.ToInt64(Math.Clamp(start, decimal.CreateChecked(retainedExtent.StartTicks),
            decimal.CreateChecked(retainedExtent.EndTicks) - span));
        return new TimeRange(clamped, checked(clamped + span));
    }
}
