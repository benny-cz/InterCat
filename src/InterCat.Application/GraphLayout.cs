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
/// The presentation layout of a process graph (§19.4): relationship first. Related nodes relax by a bounded,
/// deterministic force-directed pass, so a hub and its peers form a star and unrelated components stand apart; a node
/// with no drawn edge is parked in a row of its own, never across an edge. Its input order, thread count and wall-clock
/// time cannot change its output: nodes and edges are sorted, and new nodes are seeded from a hash of the graph identity.
/// A later query result can apply this layout only when its graph identity still matches (R7).
/// </summary>
public static class GraphLayout
{
    public const int MaximumNodes = 512;
    public const int MaximumEdges = 4_096;
    public const int RelaxationIterations = 120;

    /// <summary>
    /// The graph pane's design width-to-height ratio, TUNABLE (§26.2). Positions stay window-independent (§19.4), but
    /// distances are measured as the pane shows them: a vertical gap counts as much as a horizontal gap of equal pixels.
    /// </summary>
    public const double DesignAspect = 2.6;

    // Tunables (§26.2), in drawn units: the pane is DesignAspect wide and 1 high.

    /// <summary>Kept clear at every edge of the pane.</summary>
    private const double Margin = 0.06;

    /// <summary>
    /// The bottom strip where nodes with no drawn edge are parked, as a left-aligned footer, when related nodes are
    /// drawn above it; with nothing related they sit in a centred row across the middle.
    /// </summary>
    private const double ParkedStripHeight = 0.28;

    /// <summary>The widest spacing between parked nodes, so each has room for its label.</summary>
    private const double ParkedSpacing = 0.55;

    /// <summary>
    /// Logical pixels per drawn unit at the design pane height, TUNABLE: how the layout converts §6.3's pixel radii
    /// into its own units to keep node discs apart.
    /// </summary>
    private const double PixelsPerUnit = 250;

    /// <summary>Space kept between two discs beyond their radii, and how hard overlapping discs are pushed apart.</summary>
    private const double Clearance = 0.03;

    private const double Collision = 4;

    /// <summary>Fruchterman-Reingold's C: the ideal distance is C·√(area / related nodes).</summary>
    private const double IdealDistanceScale = 0.75;

    /// <summary>The ideal distance never exceeds this, so two related nodes do not fly to opposite corners.</summary>
    private const double MaximumIdealDistance = 0.45;

    /// <summary>
    /// Pull toward the related region's centre per unit of vertical distance; across the pane's width it is divided by
    /// the squared design aspect, so separate components pack into the pane's shape rather than a disc.
    /// </summary>
    private const double Gravity = 0.25;

    /// <summary>Separate components push apart only when closer than this many ideal distances.</summary>
    private const double ComponentGap = 2.2;

    /// <summary>The largest first step of a fresh layout; later steps cool linearly toward zero.</summary>
    private const double FullTemperature = 0.12;

    /// <summary>
    /// The largest first step of a node that was drawn before. Cooling linearly over the fixed iterations, its steps sum
    /// to at most about 0.09 of the pane's height: a refresh can nudge what the user has seen, never rearrange it.
    /// </summary>
    private const double AnchoredTemperature = 0.0015;

    /// <summary>A node drawn before is pulled back toward its place by this much per unit of distance.</summary>
    private const double Anchor = 0.5;

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

        // Each process is sized by its incident metric, exactly as the projection sizes a process drawn on its own, so a
        // graph drawn without clustering is laid out as the same process graph through either entry point.
        var incident = new Dictionary<ProcessInstanceId, long>(nodes.Count);
        foreach (CommunicationEdge edge in edges)
        {
            incident[edge.SourceId] = incident.GetValueOrDefault(edge.SourceId) + Math.Max(0, edge.ObservationCount);
            if (edge.TargetId != edge.SourceId)
            {
                incident[edge.TargetId] = incident.GetValueOrDefault(edge.TargetId) + Math.Max(0, edge.ObservationCount);
            }
        }

        long scale = incident.Count == 0 ? 0 : incident.Values.Max();
        (IReadOnlyDictionary<string, GraphPoint> positions, double energy) = Core(
            graphIdentity,
            [.. nodes.Select(node => new LayoutNode(node.Id.ToString(), node.Id.ToString(),
                ProcessRadius(incident.GetValueOrDefault(node.Id), scale)))],
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
    /// Where <see cref="ComputeDisplay"/> starts, with no relaxation (§19.4 seeding): a parked node in its slot, a related
    /// node at its previous position, and any other beside a placed neighbour or in its component's grid cell. A first
    /// frame drawn here moves only by relaxation when the complete layout for the same identity arrives, never from an
    /// unrelated placeholder.
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

    /// <summary>A process drawn on its own: §6.3's radius for its incident metric, in drawn units.</summary>
    private static double ProcessRadius(long incident, long scale) =>
        (6 + (10 * GraphEncoding.Intensity(incident, scale))) / PixelsPerUnit;

    private static LayoutNode[] DisplayNodes(GraphDisplay display)
    {
        // Radii come from the whole published scope, as the layout does; a brush re-counts sizes, never positions.
        long scale = display.Nodes.Count == 0 ? 0 : display.Nodes.Max(node => node.Observations);
        return [.. display.Nodes.Select(node => new LayoutNode(
            node.Key, node.Process?.ToString() ?? node.Key, GraphEncoding.NodeRadius(node, scale) / PixelsPerUnit))];
    }

    private static LayoutLink[] DisplayLinks(GraphDisplay display) =>
        [.. display.Edges.Select(edge => new LayoutLink(edge.Key, edge.SourceKey, edge.TargetKey))];

    private readonly record struct LayoutNode(string Key, string Seed, double Radius);

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
                    : throw new ArgumentException($"Edge {edge.Key} names a missing target node.", nameof(edges))))
            .Where(link => link.Source != link.Target)];

        if (ordered.Length == 0)
        {
            return (new ReadOnlyDictionary<string, GraphPoint>(new Dictionary<string, GraphPoint>()), 0);
        }

        int count = ordered.Length;
        var neighbours = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            neighbours[i] = [];
        }

        foreach ((int source, int target) in links)
        {
            neighbours[source].Add(target);
            neighbours[target].Add(source);
        }

        // The seed order: a hash of (graph identity, node seed), so no enumeration order and no clock can move a node.
        ulong[] hash = [.. ordered.Select(node => Seed(graphIdentity, node.Seed))];
        int[] byHash = [.. Enumerable.Range(0, count).OrderBy(i => hash[i]).ThenBy(i => ordered[i].Key, StringComparer.Ordinal)];
        bool[] linked = [.. neighbours.Select(list => list.Count > 0)];
        bool anyLinked = linked.Any(value => value);
        bool anyParked = linked.Any(value => !value);

        // Everything is laid out in drawn units - x across [0, DesignAspect], y across [0, 1] - so a distance means the
        // same horizontally and vertically, as the pane shows it.
        const double Width = DesignAspect;
        double top = Margin;
        double bottom = anyParked && anyLinked ? 1 - ParkedStripHeight : 1 - Margin;
        double left = Margin;
        double right = Width - Margin;
        double parkedRow = anyLinked ? 1 - (ParkedStripHeight / 3) : 0.5;

        var x = new double[count];
        var y = new double[count];
        var placed = new bool[count];
        var fixedNode = new bool[count];

        // A node with no drawn edge has nothing to relax toward. It is parked in a slot that depends only on the parked
        // set: a left-aligned footer below the related nodes, which reads as a summary rather than as part of a nearby
        // component, or a centred row across the middle when nothing is related.
        int[] parked = [.. Enumerable.Range(0, count).Where(i => !linked[i])];
        double spacing = Math.Min(ParkedSpacing, (right - left) / Math.Max(1, parked.Length));
        double firstSlot = anyLinked ? left + (spacing / 2) : ((left + right) / 2) - (spacing * (parked.Length - 1) / 2);
        for (int slot = 0; slot < parked.Length; slot++)
        {
            int i = parked[slot];
            x[i] = firstSlot + (spacing * slot);
            y[i] = parkedRow;
            placed[i] = true;
            fixedNode[i] = true;
        }

        var anchored = new bool[count];
        var anchorX = new double[count];
        var anchorY = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (pins?.TryGetValue(ordered[i].Key, out GraphPoint pin) == true)
            {
                x[i] = pin.X * Width;
                y[i] = pin.Y;
                placed[i] = true;
                fixedNode[i] = true;
            }
            else if (linked[i] && previous?.TryGetValue(ordered[i].Key, out GraphPoint old) == true)
            {
                // A node drawn before starts where it was, inside the region related nodes now occupy, and is anchored
                // there: the user's mental map of the graph survives a refresh (§6.3).
                x[i] = anchorX[i] = Math.Clamp(old.X * Width, left, right);
                y[i] = anchorY[i] = Math.Clamp(old.Y, top, bottom);
                placed[i] = true;
                anchored[i] = true;
            }
        }

        int related = linked.Count(value => value);
        double ideal = Math.Min(MaximumIdealDistance,
            IdealDistanceScale * Math.Sqrt((right - left) * (bottom - top) / Math.Max(1, related)));
        int[] component = Components(byHash, neighbours, linked, out List<List<int>> members);
        PlaceUnplaced(ordered, hash, members, neighbours, placed, x, y, left, right, top, bottom, ideal);

        // Relaxation (Fruchterman-Reingold): within a component every pair repels by k²/d and every relationship
        // attracts by d²/k, so a hub and its peers form a star. Separate components repel only when closer than
        // ComponentGap·k, and gravity - weaker across the pane's width, as it is wider - gathers them toward the centre:
        // they pack side by side instead of being pressed flat against the edges. A step is capped by a temperature
        // that cools linearly. A node drawn before is anchored to its place and steps only slightly, so a refresh
        // settles new nodes around the graph the user has seen instead of rearranging it.
        double centreX = (left + right) / 2;
        double centreY = (top + bottom) / 2;
        double reach = ComponentGap * ideal;
        int[] moving = [.. Enumerable.Range(0, count).Where(i => linked[i] && !fixedNode[i])];

        // A refresh that adds a few nodes keeps the rest anchored. Opening a group or focusing a process restructures the
        // drawing - many new nodes, a large share of it - and anchored neighbours would leave them no room, so anchoring
        // eases off smoothly as both the number and the share of new nodes grow. They still start where they were.
        int fresh = moving.Count(i => !anchored[i]);
        double freedom = Math.Clamp((fresh - 4) / 12d, 0, 1)
            * Math.Clamp((((double)fresh / Math.Max(1, related)) - 0.1) / 0.3, 0, 1);
        int[] repelling = [.. Enumerable.Range(0, count).Where(i => linked[i])];
        var dx = new double[count];
        var dy = new double[count];
        for (int step = 0; step < iterations; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(dx);
            Array.Clear(dy);
            for (int a = 0; a < repelling.Length; a++)
            {
                int i = repelling[a];
                for (int b = a + 1; b < repelling.Length; b++)
                {
                    int j = repelling[b];
                    bool sameComponent = component[i] == component[j];
                    double clearance = ordered[i].Radius + ordered[j].Radius + Clearance;
                    double vx = x[i] - x[j];
                    double vy = y[i] - y[j];
                    double distance = Math.Sqrt((vx * vx) + (vy * vy));
                    if (!sameComponent && distance >= Math.Max(reach, clearance))
                    {
                        continue;
                    }

                    if (distance < 1e-6)
                    {
                        // Coincident nodes part in a direction fixed by their hashes.
                        double angle = (hash[i] ^ hash[j]) % 3600 / 3600d * 2 * Math.PI;
                        distance = 1e-6;
                        vx = Math.Cos(angle) * distance;
                        vy = Math.Sin(angle) * distance;
                    }

                    // Across components the push fades to zero at the reach, so packing is gentle and continuous. Two
                    // discs closer than their radii and the clearance are pushed apart harder, so no node hides another.
                    double spread = ideal * ideal / Math.Max(distance, 0.01);
                    double force = sameComponent ? spread : Math.Max(0, spread - (ideal * ideal / reach));
                    if (distance < clearance)
                    {
                        force += (clearance - distance) * Collision;
                    }
                    dx[i] += vx / distance * force;
                    dy[i] += vy / distance * force;
                    dx[j] -= vx / distance * force;
                    dy[j] -= vy / distance * force;
                }
            }

            foreach ((int source, int target) in links)
            {
                double vx = x[target] - x[source];
                double vy = y[target] - y[source];
                double distance = Math.Max(1e-6, Math.Sqrt((vx * vx) + (vy * vy)));
                double force = distance * distance / ideal;
                dx[source] += vx / distance * force;
                dy[source] += vy / distance * force;
                dx[target] -= vx / distance * force;
                dy[target] -= vy / distance * force;
            }

            double cooling = 1 - ((double)step / iterations);
            foreach (int i in moving)
            {
                dx[i] += (centreX - x[i]) * Gravity / (DesignAspect * DesignAspect);
                dy[i] += (centreY - y[i]) * Gravity;
                double temperature = FullTemperature * cooling * cooling;
                if (anchored[i])
                {
                    dx[i] += (anchorX[i] - x[i]) * Anchor * (1 - freedom);
                    dy[i] += (anchorY[i] - y[i]) * Anchor * (1 - freedom);
                    temperature = (AnchoredTemperature * cooling * (1 - freedom)) + (temperature * freedom);
                }

                double displacement = Math.Sqrt((dx[i] * dx[i]) + (dy[i] * dy[i]));
                if (displacement < 1e-12)
                {
                    continue;
                }

                double moved = Math.Min(displacement, temperature);
                x[i] = Math.Clamp(x[i] + (dx[i] / displacement * moved), left, right);
                y[i] = Math.Clamp(y[i] + (dy[i] / displacement * moved), top, bottom);
            }
        }

        var positions = new Dictionary<string, GraphPoint>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            // Back to window-independent graph coordinates, kept inside [0,1] against rounding.
            positions[ordered[i].Key] = new(Math.Clamp(x[i] / Width, 0, 1), Math.Clamp(y[i], 0, 1));
        }

        return (new ReadOnlyDictionary<string, GraphPoint>(positions), Energy(x, y, links, repelling, ideal));
    }

    /// <summary>
    /// Places every related node not already placed: each unplaced component starts from a cell of a deterministic grid
    /// over the region, at its best-connected node, and every other node starts beside a placed neighbour. A node that
    /// joins a graph the user has seen therefore appears next to what it relates to, not somewhere arbitrary.
    /// </summary>
    private static void PlaceUnplaced(
        LayoutNode[] ordered,
        ulong[] hash,
        List<List<int>> members,
        List<int>[] neighbours,
        bool[] placed,
        double[] x,
        double[] y,
        double left,
        double right,
        double top,
        double bottom,
        double ideal)
    {
        // Components with nothing placed get grid cells, largest first, filling the region's shape row by row.
        List<int>[] fresh = [.. members
            .Where(list => !list.Any(i => placed[i]))
            .OrderByDescending(list => list.Count)
            .ThenBy(list => list.Min(i => hash[i]))];
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(fresh.Length * (right - left) / Math.Max(1e-9, bottom - top))));
        int rows = Math.Max(1, (int)Math.Ceiling((double)fresh.Length / columns));
        for (int cell = 0; cell < fresh.Length; cell++)
        {
            int root = fresh[cell]
                .OrderByDescending(i => neighbours[i].Count)
                .ThenBy(i => hash[i])
                .ThenBy(i => ordered[i].Key, StringComparer.Ordinal)
                .First();
            x[root] = left + ((right - left) * ((cell % columns) + 0.5) / columns);
            y[root] = top + ((bottom - top) * ((cell / columns) + 0.5) / rows);
            placed[root] = true;
        }

        // Breadth first from every placed node: a neighbour starts one ideal distance away, at an angle its own hash fixes.
        var queue = new Queue<int>(members
            .SelectMany(list => list)
            .Where(i => placed[i])
            .OrderBy(i => hash[i])
            .ThenBy(i => ordered[i].Key, StringComparer.Ordinal));
        while (queue.Count > 0)
        {
            int from = queue.Dequeue();
            foreach (int next in neighbours[from].Distinct().OrderBy(i => hash[i]))
            {
                if (placed[next])
                {
                    continue;
                }

                double angle = hash[next] % 3600 / 3600d * 2 * Math.PI;
                x[next] = Math.Clamp(x[from] + (Math.Cos(angle) * ideal), left, right);
                y[next] = Math.Clamp(y[from] + (Math.Sin(angle) * ideal), top, bottom);
                placed[next] = true;
                queue.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// The connected components of the related nodes, each discovered breadth first from its lowest-hash node; a parked
    /// node belongs to none (-1).
    /// </summary>
    private static int[] Components(int[] byHash, List<int>[] neighbours, bool[] linked, out List<List<int>> members)
    {
        var component = new int[linked.Length];
        Array.Fill(component, -1);
        members = [];
        foreach (int i in byHash)
        {
            if (!linked[i] || component[i] >= 0)
            {
                continue;
            }

            var list = new List<int> { i };
            component[i] = members.Count;
            for (int next = 0; next < list.Count; next++)
            {
                foreach (int neighbour in neighbours[list[next]])
                {
                    if (component[neighbour] < 0)
                    {
                        component[neighbour] = members.Count;
                        list.Add(neighbour);
                    }
                }
            }

            members.Add(list);
        }

        return component;
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

    /// <summary>
    /// The stated energy of a layout: each relationship's squared departure from the ideal distance, plus a squared
    /// penalty for related nodes closer than half of it. Lower is more legible; it is reported, not optimized directly.
    /// </summary>
    private static double Energy(
        double[] x, double[] y, IReadOnlyList<(int Source, int Target)> links, int[] repelling, double ideal)
    {
        double total = 0;
        for (int a = 0; a < repelling.Length; a++)
        {
            for (int b = a + 1; b < repelling.Length; b++)
            {
                double distance = Math.Sqrt(Math.Pow(x[repelling[a]] - x[repelling[b]], 2) + Math.Pow(y[repelling[a]] - y[repelling[b]], 2));
                total += Math.Pow(Math.Max(0, (ideal / 2) - distance), 2);
            }
        }

        foreach ((int source, int target) in links)
        {
            double distance = Math.Sqrt(Math.Pow(x[source] - x[target], 2) + Math.Pow(y[source] - y[target], 2));
            total += Math.Pow(distance - ideal, 2);
        }

        return total;
    }
}
