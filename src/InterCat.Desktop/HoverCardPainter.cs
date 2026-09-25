using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace InterCat.Desktop;

/// <summary>
/// Draws a hover card beside the pointer, flipped to stay inside its pane, in the inspector's ink and surface. The graph
/// and the timeline share it, so a mark is explained the same way wherever it is drawn (§6.2's hover contract).
/// </summary>
internal sealed class HoverCardPainter(IBrush surface, IBrush ink, IBrush mutedInk, IPen border)
{
    private const double Padding = 8;
    private const double Gap = 14;
    private const double MaximumWidth = 460;

    /// <summary>Card text is laid out once per text and width, not on every pointer move.</summary>
    private readonly Dictionary<(string Text, double Width, double Size, bool Strong), FormattedText> texts = [];

    /// <summary>Draws the card and returns where it was drawn; null when the pane is too small to hold one.</summary>
    public Rect? Draw(DrawingContext context, HoverCard card, Point pointer, Size pane)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(card);
        if (pane.Width < 80 || pane.Height < 60)
        {
            return null;
        }

        // Stay inside the pane even in a transiently narrow layout; semantic text wraps instead of pushing the card off-screen.
        double width = Math.Min(MaximumWidth, pane.Width - 16);
        double textWidth = Math.Max(1, width - (2 * Padding));
        FormattedText title = Text(card.Title, 12, strong: true, textWidth);
        double bodyHeight = 0;
        for (int i = 0; i < card.Lines.Count; i++)
        {
            bodyHeight += Text(card.Lines[i], 11, strong: false, textWidth).Height;
        }

        double height = Padding + title.Height + 4 + bodyHeight + Padding;
        double x = pointer.X + Gap + width <= pane.Width ? pointer.X + Gap : Math.Max(0, pointer.X - Gap - width);
        double y = Math.Clamp(pointer.Y + Gap, 0, Math.Max(0, pane.Height - height));
        context.DrawRectangle(surface, border, new Rect(x, y, width, height), 6, 6);
        double top = y + Padding;
        context.DrawText(title, new(x + Padding, top));
        top += title.Height + 4;
        for (int i = 0; i < card.Lines.Count; i++)
        {
            FormattedText line = Text(card.Lines[i], 11, strong: false, textWidth);
            context.DrawText(line, new(x + Padding, top));
            top += line.Height;
        }

        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Hover text may wrap to two lines: clipping a semantic or accounting line would defeat the card's purpose, and a
    /// small bound keeps a narrow pane readable.
    /// </summary>
    private FormattedText Text(string text, double size, bool strong, double width)
    {
        if (!texts.TryGetValue((text, width, size, strong), out FormattedText? formatted))
        {
            if (texts.Count > 256) texts.Clear();
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new("Segoe UI"), size,
                strong ? ink : mutedInk)
            {
                MaxTextWidth = width,
                MaxLineCount = 2,
                Trimming = TextTrimming.WordEllipsis,
            };
            texts[(text, width, size, strong)] = formatted;
        }

        return formatted;
    }
}
