namespace InterCat.Application;

/// <summary>
/// §6.3's fixed magnitude encoding, shared by the layout and the view so they agree on how large a node is drawn: the
/// layout keeps drawn discs apart, and the pane draws them at exactly that size.
/// </summary>
public static class GraphEncoding
{
    /// <summary>§6.2's intensity: log2(1 + v) / log2(1 + vScale), zero for nothing observed.</summary>
    public static double Intensity(long value, long scale) =>
        value <= 0 || scale <= 0 ? 0 : Math.Log2(1 + (double)value) / Math.Log2(1 + (double)scale);

    /// <summary>
    /// A node's radius in logical pixels: 6 + 10 × intensity of its incident metric, floor 6 so a quiet participant
    /// stays selectable, plus 3 for an aggregate, whose stack of rims needs the room.
    /// </summary>
    public static double NodeRadius(GraphDisplayNode node, long scale)
    {
        ArgumentNullException.ThrowIfNull(node);
        return 6 + (10 * Intensity(node.Observations, scale)) + (node.Kind == GraphNodeKind.Process ? 0 : 3);
    }

    /// <summary>An edge's thickness in logical pixels: 1.25 + 4.75 × intensity of its metric.</summary>
    public static double EdgeThickness(GraphDisplayEdge edge, long scale)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return 1.25 + (4.75 * Intensity(edge.ObservationCount, scale));
    }
}
