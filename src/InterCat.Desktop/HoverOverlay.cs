using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using InterCat.Desktop.Theme;

namespace InterCat.Desktop;

/// <summary>A pane whose marks explain themselves on hover: the card for the mark under the pointer, and where the pointer is.</summary>
internal interface IHoverCardSource
{
    /// <summary>The card for the mark under the pointer; null when the pointer rests on no mark.</summary>
    HoverCard? HoverCard { get; }

    /// <summary>Where the pointer is, in the source's own coordinates.</summary>
    Point HoverPoint { get; }

    /// <summary>Raised when the hovered mark or the pointer changes, so the layer drawing the card redraws it.</summary>
    event EventHandler? HoverChanged;
}

/// <summary>
/// The window's hover layer. It draws the card of whichever tracked pane has one, above every pane and clamped to the
/// window, so a card is never clipped by the pane its mark is in: at the minimum window the timeline's pane is shorter
/// than a complete §6.2 card. It takes no pointer input and holds no hover state of its own.
/// </summary>
internal sealed class HoverOverlay : Control
{
    private readonly List<Visual> sources = [];

    // One painter per theme mode, so a card drawn after a change of theme is in that theme's tokens (§6.1).
    private readonly Dictionary<ThemeMode, HoverCardPainter> painters = [];

    private HoverCardPainter Painter
    {
        get
        {
            ThemeMode mode = ThemeResources.CurrentMode;
            if (!painters.TryGetValue(mode, out HoverCardPainter? painter))
            {
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                painter = new(Token(surfaces.Elevated), Token(surfaces.Ink), Token(surfaces.MutedInk),
                    new Pen(Token(surfaces.MutedInk), 1));
                painters[mode] = painter;
            }

            return painter;
        }
    }

    public HoverOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Draws this pane's cards from now on.</summary>
    public void Track<T>(T source) where T : Visual, IHoverCardSource
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sources.Contains(source))
        {
            return;
        }

        sources.Add(source);
        source.HoverChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The card drawn now, if any: the one a tracked pane reports for the mark under the pointer.</summary>
    internal HoverCard? Card => Current()?.Card;

    /// <summary>Where the last frame drew a card, in this layer's coordinates; null when it drew none.</summary>
    internal Rect? CardBounds { get; private set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        CardBounds = Current() is { } current ? Painter.Draw(context, current.Card, current.Point, Bounds.Size) : null;
    }

    private (HoverCard Card, Point Point)? Current()
    {
        foreach (Visual source in sources)
        {
            if (source is IHoverCardSource { HoverCard: { } card } hover && source.IsEffectivelyVisible
                && source.TranslatePoint(hover.HoverPoint, this) is { } point)
            {
                return (card, point);
            }
        }

        return null;
    }

    private static SolidColorBrush Token(Srgb value) => new(ThemeResources.ToColor(value));
}
