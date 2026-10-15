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
    /// The scale a node's size is read against: the busiest node the rung draws for itself. A focused rung's context node
    /// counts the rest of the machine, which is not what the rung measures, so it neither sets the scale nor is sized by it;
    /// nor does the Other hosts node, a resource the drawn processes' connections run to, measured by none of them.
    /// </summary>
    public static long NodeScale(GraphDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        // Read on every repaint of the graph, so an index loop: a foreach over the interface would allocate (R11).
        long scale = 0;
        for (int index = 0; index < display.Nodes.Count; index++)
        {
            GraphDisplayNode node = display.Nodes[index];
            if (node.Kind is not (GraphNodeKind.Context or GraphNodeKind.OtherHosts) && node.Weight > scale)
            {
                scale = node.Weight;
            }
        }

        return scale;
    }

    /// <summary>
    /// A node's radius in logical pixels: 6 + 10 × intensity of its incident metric, floor 6 so a quiet participant
    /// stays selectable, plus 3 for an aggregate, whose stack of rims needs the room. A context node is drawn at the
    /// aggregate floor whatever it holds, since its activity is outside the scale (<see cref="NodeScale"/>), and the Other
    /// hosts node at the largest a process can be drawn, the hub its edges meet at.
    /// </summary>
    public static double NodeRadius(GraphDisplayNode node, long scale)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind switch
        {
            GraphNodeKind.Process => 6 + (10 * Intensity(node.Weight, scale)),
            GraphNodeKind.Context => 9,
            GraphNodeKind.OtherHosts => 16,
            _ => 9 + (10 * Intensity(node.Weight, scale)),
        };
    }

    /// <summary>
    /// The scale an edge's thickness is read against: the heaviest edge drawn, by the metric its thickness reads. An edge to
    /// the other hosts is drawn with no traffic value, so it does not set the scale.
    /// </summary>
    public static long EdgeScale(GraphDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        long scale = 0;
        for (int index = 0; index < display.Edges.Count; index++)
        {
            if (!display.Edges[index].ToHosts)
            {
                scale = Math.Max(scale, display.Edges[index].Weight);
            }
        }

        return scale;
    }

    /// <summary>
    /// The width of an unmeasured edge's band: the widest thickness, so the open outline and its cross-hatch read at a
    /// glance and the band is never mistaken for a quiet edge's hairline (§6.6).
    /// </summary>
    public const double UnmeasuredBand = 6;

    /// <summary>
    /// An edge's thickness in logical pixels: 1.25 + 4.75 × intensity of its metric; an unmeasured edge's band is
    /// <see cref="UnmeasuredBand"/> wide, which is where it is hit, whatever its scale. An edge to the other hosts carries
    /// no traffic value, so it is drawn at the thinnest stroke, constant, whatever it counts.
    /// </summary>
    public static double EdgeThickness(GraphDisplayEdge edge, long scale)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.ToHosts ? 1.25
            : edge.Unmeasured ? UnmeasuredBand
            : 1.25 + (4.75 * Intensity(edge.Weight, scale));
    }
}
