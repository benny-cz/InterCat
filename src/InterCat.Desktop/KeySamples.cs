using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// One line of the evidence-quality key, drawn by the graph's own edge routine: the dash pattern of a relation of
/// <see cref="Strength"/>, and the open ring a weak one carries, in body ink. Evidence quality owns the pattern and
/// never a hue or a mechanism's glyph (§6.6), so the key shows exactly what the graph draws and nothing more.
/// </summary>
public sealed class EvidenceKeySample : ThemedSample
{
    public static readonly StyledProperty<RelationStrength> StrengthProperty =
        AvaloniaProperty.Register<EvidenceKeySample, RelationStrength>(nameof(Strength), RelationStrength.Direct);

    static EvidenceKeySample() => AffectsRender<EvidenceKeySample>(StrengthProperty);

    public RelationStrength Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(36, 14);

    public override void Render(DrawingContext context) => GraphView.DrawEdgeSample(context, Strength, new Rect(Bounds.Size));
}

/// <summary>The legend's swatch of the coverage hatch, drawn by the timeline's own routine in the caution ink (§6.6).</summary>
public sealed class CoverageKeySample : ThemedSample
{
    protected override Size MeasureOverride(Size availableSize) => new(16, 11);

    public override void Render(DrawingContext context) =>
        TimelineView.DrawCoverageGap(context, new Rect(Bounds.Size).Deflate(0.5));
}

/// <summary>A key sample draws with the current theme mode's pens, so it redraws when the mode changes (§6.1).</summary>
public abstract class ThemedSample : Control
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeResources.ModeChanged += OnModeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeResources.ModeChanged -= OnModeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnModeChanged(object? sender, EventArgs e) => InvalidateVisual();
}
