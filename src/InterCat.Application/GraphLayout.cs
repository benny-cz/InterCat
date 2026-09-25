using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>A position in graph coordinates, independent of the window size and timeline viewport.</summary>
public readonly record struct GraphPoint(double X, double Y)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y)
        && X >= 0 && X <= 1 && Y >= 0 && Y <= 1;
}

/// <summary>Immutable result of a bounded, deterministic layout computation.</summary>
public sealed record GraphLayoutResult(
    string GraphIdentity,
    IReadOnlyDictionary<ProcessInstanceId, GraphPoint> Positions,
    int Iterations,
    double Energy);

/// <summary>The layout of a drawn graph (<see cref="GraphDisplay"/>), keyed by display node.</summary>
public sealed record GraphDisplayLayout(
    string GraphIdentity,
    IReadOnlyDictionary<string, GraphPoint> Positions,
    int Iterations,
    double Energy);

/// <summary>
/// The presentation layout of a process graph. Its input order, thread count and wall-clock time cannot change
/// its output: nodes and edges are sorted before a fixed number of relaxation steps. A later query result can
/// apply this layout only when its graph identity still matches (§19.4, R7).
/// </summary>
public static class GraphLayout
{
    public const int MaximumNodes = 512;
    public const int MaximumEdges = 4_096;
    public const int RelaxationIterations = 120;

    /// <summary>
    /// The graph pane's design width-to-height ratio, TUNABLE (§26.2). Positions stay window-independent (§19.4), but
    /// distances are measured as the pane shows them: a vertical gap counts as much as a horizontal gap of equal pixels,
    /// so nodes that look separated are separated rather than stacked into a strip.
    /// </summary>
    public const double DesignAspect = 2.6;

    private const double BandMargin = 0.035;
    private const double MaximumSeparation = 0.105;
    private const double DesiredEdgeLength = 0.23;

    /// <summary>
    /// The separation nodes are pushed to: the historic 0.105 of the pane's width while it fits, and less as the graph
    /// grows, so every node of a full display budget still has a share of the pane rather than an overlapping crowd.
    /// </summary>
    private static double SeparationFor(int nodes) =>
        nodes <= 0 ? MaximumSeparation : Math.Min(MaximumSeparation, 0.9 * Math.Sqrt(1 / (DesignAspect * nodes)));

    public static GraphLayoutResult Compute(
        string graphIdentity,
        IReadOnlyList<ProcessGroup> groups,
        IReadOnlyList<ProcessNode> nodes,
        IReadOnlyList<CommunicationEdge> edges,
        IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? previous = null,
        IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? pins = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        if (groups.Select(group => group.Key).Distinct(StringComparer.Ordinal).Count() != groups.Count)
        {
            throw new ArgumentException("Group keys must be unique.", nameof(groups));
        }

        var ids = new Dictionary<string, ProcessInstanceId>(nodes.Count, StringComparer.Ordinal);
        foreach (ProcessNode node in nodes)
        {
            if (!ids.TryAdd(node.Id.ToString(), node.Id))
            {
                throw new ArgumentException($"Process instance {node.Id} occurs twice.", nameof(nodes));
            }
        }

        (IReadOnlyDictionary<string, GraphPoint> positions, double energy) = Core(
            graphIdentity,
            [.. nodes.Select(node => new LayoutNode(node.Id.ToString(), node.GroupKey, node.Id.ToString()))],
            [.. edges.Select(edge => new LayoutLink(edge.Key, edge.SourceId.ToString(), edge.TargetId.ToString()))],
            Keyed(previous),
            Keyed(pins),
            cancellationToken);
        return new(graphIdentity,
            new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(positions.ToDictionary(entry => ids[entry.Key], entry => entry.Value)),
            nodes.Count == 0 ? 0 : RelaxationIterations, energy);

        static Dictionary<string, GraphPoint>? Keyed(IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? coordinates) =>
            coordinates?.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Lays out a drawn graph. A process node is seeded by its process identity exactly as <see cref="Compute"/> seeds
    /// it, so a graph drawn without clustering is placed exactly as the process graph it is.
    /// </summary>
    public static GraphDisplayLayout ComputeDisplay(
        string graphIdentity,
        GraphDisplay display,
        IReadOnlyDictionary<string, GraphPoint>? previous = null,
        IReadOnlyDictionary<string, GraphPoint>? pins = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        ArgumentNullException.ThrowIfNull(display);
        (IReadOnlyDictionary<string, GraphPoint> positions, double energy) = Core(
            graphIdentity, DisplayNodes(display), DisplayLinks(display), previous, pins, cancellationToken);
        return new(graphIdentity, positions, display.Nodes.Count == 0 ? 0 : RelaxationIterations, energy);
    }

    /// <summary>
    /// Where <see cref="ComputeDisplay"/> starts: each node at its previous position kept inside its band, or on its band's
    /// deterministic lattice (§19.4 seeding), with no relaxation. A first frame drawn here moves only by relaxation when the
    /// complete layout for the same identity arrives, never from an unrelated placeholder.
    /// </summary>
    public static IReadOnlyDictionary<string, GraphPoint> SeedDisplay(
        string graphIdentity,
        GraphDisplay display,
        IReadOnlyDictionary<string, GraphPoint>? previous = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphIdentity);
        ArgumentNullException.ThrowIfNull(display);
        return Core(graphIdentity, DisplayNodes(display), DisplayLinks(display), previous, null, CancellationToken.None,
            iterations: 0).Positions;
    }

    private static LayoutNode[] DisplayNodes(GraphDisplay display) =>
        [.. display.Nodes.Select(node => new LayoutNode(node.Key, node.Band, node.Process?.ToString() ?? node.Key))];

    private static LayoutLink[] DisplayLinks(GraphDisplay display) =>
        [.. display.Edges.Select(edge => new LayoutLink(edge.Key, edge.SourceKey, edge.TargetKey))];

    private readonly record struct LayoutNode(string Key, string Band, string Seed);

    private readonly record struct LayoutLink(string Key, string Source, string Target);

    private static (IReadOnlyDictionary<string, GraphPoint> Positions, double Energy) Core(
        string graphIdentity,
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutLink> edges,
        IReadOnlyDictionary<string, GraphPoint>? previous,
        IReadOnlyDictionary<string, GraphPoint>? pins,
        CancellationToken cancellationToken,
        int iterations = RelaxationIterations)
    {
        if (nodes.Count > MaximumNodes || edges.Count > MaximumEdges)
        {
            throw new InvalidOperationException(
                $"A graph with {nodes.Count} nodes and {edges.Count} edges needs compaction before layout "
                + $"(bounds: {MaximumNodes} nodes, {MaximumEdges} edges). No members were silently omitted.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        LayoutNode[] ordered = [.. nodes.OrderBy(node => node.Key, StringComparer.Ordinal)];
        string[] bands = [.. ordered.Select(node => node.Band).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var index = new Dictionary<string, int>(ordered.Length, StringComparer.Ordinal);
        for (int i = 0; i < ordered.Length; i++)
        {
            if (!index.TryAdd(ordered[i].Key, i))
            {
                throw new ArgumentException($"Graph node {ordered[i].Key} occurs twice.", nameof(nodes));
            }
        }

        ValidateCoordinates(previous, index, nameof(previous));
        ValidateCoordinates(pins, index, nameof(pins));
        (int Source, int Target)[] links = [.. edges
            .OrderBy(edge => edge.Key, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .Select(edge => (
                Source: index.TryGetValue(edge.Source, out int source) ? source
                    : throw new ArgumentException($"Edge {edge.Key} names a missing source node.", nameof(edges)),
                Target: index.TryGetValue(edge.Target, out int target) ? target
                    : throw new ArgumentException($"Edge {edge.Key} names a missing target node.", nameof(edges))))];

        if (ordered.Length == 0)
        {
            return (new ReadOnlyDictionary<string, GraphPoint>(new Dictionary<string, GraphPoint>()), 0);
        }

        var x = new double[ordered.Length];
        var y = new double[ordered.Length];
        var low = new double[ordered.Length];
        var high = new double[ordered.Length];
        var fixedNode = new bool[ordered.Length];
        var bestX = new double[ordered.Length];
        var bestY = new double[ordered.Length];
        double bandHeight = 1d / bands.Length;
        for (int band = 0; band < bands.Length; band++)
        {
            int[] members = [.. Enumerable.Range(0, ordered.Length)
                .Where(i => string.Equals(ordered[i].Band, bands[band], StringComparison.Ordinal))
                .OrderBy(i => Seed(graphIdentity, ordered[i].Seed))];
            if (members.Length == 0)
            {
                continue;
            }

            // The lattice is seeded for the pane's shape, so its cells are square as drawn rather than in the unit square.
            int columns = Math.Min(members.Length, (int)Math.Ceiling(Math.Sqrt(members.Length * DesignAspect / bandHeight)));
            int rows = (int)Math.Ceiling((double)members.Length / columns);
            for (int position = 0; position < members.Length; position++)
            {
                int i = members[position];
                low[i] = band * bandHeight + Math.Min(BandMargin, bandHeight * 0.15);
                high[i] = (band + 1) * bandHeight - Math.Min(BandMargin, bandHeight * 0.15);
                x[i] = 0.08 + 0.84 * ((position % columns) + 0.5) / columns;
                y[i] = low[i] + (high[i] - low[i]) * ((position / columns) + 0.5) / rows;
                if (previous?.TryGetValue(ordered[i].Key, out GraphPoint old) == true)
                {
                    // Keep an existing position when its band is unchanged. If regrouping moved the
                    // node to another band, keep its horizontal anchor and bring it inside that band.
                    x[i] = Math.Clamp(old.X, 0.06, 0.94);
                    y[i] = Math.Clamp(old.Y, low[i], high[i]);
                }

                if (pins?.TryGetValue(ordered[i].Key, out GraphPoint pin) == true)
                {
                    x[i] = pin.X;
                    y[i] = pin.Y;
                    fixedNode[i] = true;
                }
            }
        }

        // Relaxation runs in the pane's proportions: a vertical coordinate is divided by the design aspect, so a distance is
        // the distance as drawn. The result is converted back to window-independent graph coordinates at the end.
        for (int i = 0; i < ordered.Length; i++)
        {
            y[i] /= DesignAspect;
            low[i] /= DesignAspect;
            high[i] /= DesignAspect;
        }

        double separation = SeparationFor(ordered.Length);
        double bestEnergy = Energy(x, y, links, separation);
        Array.Copy(x, bestX, x.Length);
        Array.Copy(y, bestY, y.Length);
        var dx = new double[ordered.Length];
        var dy = new double[ordered.Length];
        for (int step = 0; step < iterations; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(dx);
            Array.Clear(dy);
            for (int i = 0; i < ordered.Length; i++)
            {
                for (int j = i + 1; j < ordered.Length; j++)
                {
                    double vx = x[i] - x[j];
                    double vy = y[i] - y[j];
                    double distance = Math.Sqrt(vx * vx + vy * vy);
                    if (distance >= separation)
                    {
                        continue;
                    }

                    // Coincident seeds separate in a stable direction derived from their sorted IDs.
                    if (distance < 1e-9)
                    {
                        vx = (i + j) % 2 == 0 ? 1 : -1;
                        vy = i % 2 == 0 ? 1 : -1;
                        distance = Math.Sqrt(2);
                    }

                    double force = (separation - distance) / distance;
                    dx[i] += vx * force;
                    dy[i] += vy * force;
                    dx[j] -= vx * force;
                    dy[j] -= vy * force;
                }
            }

            foreach ((int source, int target) in links)
            {
                if (source == target)
                {
                    continue;
                }

                double vx = x[target] - x[source];
                double vy = y[target] - y[source];
                double distance = Math.Max(1e-9, Math.Sqrt(vx * vx + vy * vy));
                double force = 0.14 * (distance - DesiredEdgeLength) / distance;
                dx[source] += vx * force;
                dy[source] += vy * force;
                dx[target] -= vx * force;
                dy[target] -= vy * force;
            }

            double rate = 0.025 * (1 - 0.75 * step / RelaxationIterations);
            for (int i = 0; i < ordered.Length; i++)
            {
                if (fixedNode[i])
                {
                    continue;
                }

                x[i] = Math.Clamp(x[i] + Math.Clamp(dx[i] * rate, -0.02, 0.02), 0.06, 0.94);
                y[i] = Math.Clamp(y[i] + Math.Clamp(dy[i] * rate, -0.02, 0.02), low[i], high[i]);
            }

            double energy = Energy(x, y, links, separation);
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                Array.Copy(x, bestX, x.Length);
                Array.Copy(y, bestY, y.Length);
            }
        }

        var positions = new Dictionary<string, GraphPoint>(ordered.Length, StringComparer.Ordinal);
        for (int i = 0; i < ordered.Length; i++)
        {
            // Back to graph coordinates, kept inside [0,1] against the conversion's rounding.
            positions[ordered[i].Key] = new(bestX[i], Math.Clamp(bestY[i] * DesignAspect, 0, 1));
        }

        return (new ReadOnlyDictionary<string, GraphPoint>(positions), bestEnergy);
    }

    private static void ValidateCoordinates(
        IReadOnlyDictionary<string, GraphPoint>? coordinates,
        Dictionary<string, int> nodes,
        string argument)
    {
        if (coordinates is null)
        {
            return;
        }

        foreach ((string key, GraphPoint point) in coordinates)
        {
            if (!nodes.ContainsKey(key) || !point.IsValid)
            {
                throw new ArgumentException(
                    $"A graph position must name a present node and have finite coordinates inside [0,1]: {key}.",
                    argument);
            }
        }
    }

    private static ulong Seed(string graphIdentity, string seed)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(graphIdentity + "\n" + seed));
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private static double Energy(double[] x, double[] y, IReadOnlyList<(int Source, int Target)> links, double separation)
    {
        double total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            for (int j = i + 1; j < x.Length; j++)
            {
                double distance = Math.Sqrt(Math.Pow(x[i] - x[j], 2) + Math.Pow(y[i] - y[j], 2));
                total += Math.Pow(Math.Max(0, separation - distance), 2) * 10;
            }
        }

        foreach ((int source, int target) in links)
        {
            if (source != target)
            {
                double distance = Math.Sqrt(Math.Pow(x[source] - x[target], 2) + Math.Pow(y[source] - y[target], 2));
                total += Math.Pow(distance - DesiredEdgeLength, 2);
            }
        }

        return total;
    }
}
