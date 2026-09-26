using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace InterCat.Desktop;

/// <summary>
/// Laid-out text for the drawn panes, built once per (text, size, brush, width, lines, trimming) and reused. A cached
/// layout draws without allocating, where drawing a <see cref="FormattedText"/> allocates on every call, so a repaint
/// that draws the same labels allocates nothing (R11; §19.4: measure text once per string, font and theme, and cache
/// it). A cache that outgrows its bound is emptied: a pan past many distinct labels may fill it, and the labels drawn
/// next are laid out again.
/// </summary>
internal sealed class PaneText(int capacity)
{
    /// <summary>Every pane draws in one typeface, resolved once rather than per label.</summary>
    internal static readonly Typeface Face = new("Segoe UI");

    private readonly Dictionary<Key, TextLayout> layouts = [];

    /// <summary>One line of <paramref name="text"/>, or up to <paramref name="lines"/> wrapped lines, trimmed at the width.</summary>
    public TextLayout Get(string text, double size, IBrush brush, double width = double.PositiveInfinity, int lines = 1,
        TextTrimming? trimming = null)
    {
        var key = new Key(text, size, brush, width, lines, trimming);
        if (!layouts.TryGetValue(key, out TextLayout? layout))
        {
            if (layouts.Count >= capacity)
            {
                layouts.Clear();
            }

            layout = new TextLayout(text, Face, size, brush, TextAlignment.Left,
                lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, trimming, maxWidth: width, maxLines: lines);
            layouts[key] = layout;
        }

        return layout;
    }

    private readonly record struct Key(string Text, double Size, IBrush Brush, double Width, int Lines, TextTrimming? Trimming);
}
