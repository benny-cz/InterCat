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

    private const double BandMargin = 0.035;
    private const double MinimumDistance = 0.105;
    private const double DesiredEdgeLength = 0.23;

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
        if (nodes.Count > MaximumNodes || edges.Count > MaximumEdges)
        {
            throw new InvalidOperationException(
                $"A graph with {nodes.Count} nodes and {edges.Count} edges needs compaction before layout "
                + $"(bounds: {MaximumNodes} nodes, {MaximumEdges} edges). No members were silently omitted.");
        }

        if (groups.Select(group => group.Key).Distinct(StringComparer.Ordinal).Count() != groups.Count)
        {
            throw new ArgumentException("Group keys must be unique.", nameof(groups));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProcessNode[] ordered = [.. nodes.OrderBy(node => node.Id.ToString(), StringComparer.Ordinal)];
        string[] groupKeys = [.. ordered.Select(node => node.GroupKey)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var index = new Dictionary<ProcessInstanceId, int>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            if (!index.TryAdd(ordered[i].Id, i))
            {
                throw new ArgumentException($"Process instance {ordered[i].Id} occurs twice.", nameof(nodes));
            }
        }

        ValidateCoordinates(previous, index, nameof(previous));
        ValidateCoordinates(pins, index, nameof(pins));
        (int Source, int Target)[] links = [.. edges
            .OrderBy(edge => edge.Key, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId.ToString(), StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId.ToString(), StringComparer.Ordinal)
            .Select(edge => (
                Source: index.TryGetValue(edge.SourceId, out int source) ? source
                    : throw new ArgumentException($"Edge {edge.Key} names a missing source node.", nameof(edges)),
                Target: index.TryGetValue(edge.TargetId, out int target) ? target
                    : throw new ArgumentException($"Edge {edge.Key} names a missing target node.", nameof(edges))))];

        if (ordered.Length == 0)
        {
            return new(graphIdentity, new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(
                new Dictionary<ProcessInstanceId, GraphPoint>()), 0, 0);
        }

        var x = new double[ordered.Length];
        var y = new double[ordered.Length];
        var low = new double[ordered.Length];
        var high = new double[ordered.Length];
        var fixedNode = new bool[ordered.Length];
        var bestX = new double[ordered.Length];
        var bestY = new double[ordered.Length];
        double bandHeight = 1d / groupKeys.Length;
        for (int group = 0; group < groupKeys.Length; group++)
        {
            int[] members = [.. Enumerable.Range(0, ordered.Length)
                .Where(i => string.Equals(ordered[i].GroupKey, groupKeys[group], StringComparison.Ordinal))
                .OrderBy(i => Seed(graphIdentity, ordered[i].Id))];
            if (members.Length == 0)
            {
                continue;
            }

            int columns = Math.Min(members.Length, (int)Math.Ceiling(Math.Sqrt(members.Length / bandHeight)));
            int rows = (int)Math.Ceiling((double)members.Length / columns);
            for (int position = 0; position < members.Length; position++)
            {
                int i = members[position];
                low[i] = group * bandHeight + Math.Min(BandMargin, bandHeight * 0.15);
                high[i] = (group + 1) * bandHeight - Math.Min(BandMargin, bandHeight * 0.15);
                x[i] = 0.08 + 0.84 * ((position % columns) + 0.5) / columns;
                y[i] = low[i] + (high[i] - low[i]) * ((position / columns) + 0.5) / rows;
                if (previous?.TryGetValue(ordered[i].Id, out GraphPoint old) == true)
                {
                    // Keep an existing position when its band is unchanged. If regrouping moved the
                    // node to another band, keep its horizontal anchor and bring it inside that band.
                    x[i] = Math.Clamp(old.X, 0.06, 0.94);
                    y[i] = Math.Clamp(old.Y, low[i], high[i]);
                }

                if (pins?.TryGetValue(ordered[i].Id, out GraphPoint pin) == true)
                {
                    x[i] = pin.X;
                    y[i] = pin.Y;
                    fixedNode[i] = true;
                }
            }
        }

        double bestEnergy = Energy(x, y, links);
        Array.Copy(x, bestX, x.Length);
        Array.Copy(y, bestY, y.Length);
        var dx = new double[ordered.Length];
        var dy = new double[ordered.Length];
        for (int step = 0; step < RelaxationIterations; step++)
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
                    if (distance >= MinimumDistance)
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

                    double force = (MinimumDistance - distance) / distance;
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

            double energy = Energy(x, y, links);
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                Array.Copy(x, bestX, x.Length);
                Array.Copy(y, bestY, y.Length);
            }
        }

        var positions = new Dictionary<ProcessInstanceId, GraphPoint>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            positions[ordered[i].Id] = new(bestX[i], bestY[i]);
        }

        return new(graphIdentity, new ReadOnlyDictionary<ProcessInstanceId, GraphPoint>(positions),
            RelaxationIterations, bestEnergy);
    }

    private static void ValidateCoordinates(
        IReadOnlyDictionary<ProcessInstanceId, GraphPoint>? coordinates,
        Dictionary<ProcessInstanceId, int> nodes,
        string argument)
    {
        if (coordinates is null)
        {
            return;
        }

        foreach ((ProcessInstanceId id, GraphPoint point) in coordinates)
        {
            if (!nodes.ContainsKey(id) || !point.IsValid)
            {
                throw new ArgumentException(
                    $"A graph position must name a present node and have finite coordinates inside [0,1]: {id}.",
                    argument);
            }
        }
    }

    private static ulong Seed(string graphIdentity, ProcessInstanceId id)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(graphIdentity + "\n" + id));
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private static double Energy(double[] x, double[] y, IReadOnlyList<(int Source, int Target)> links)
    {
        double total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            for (int j = i + 1; j < x.Length; j++)
            {
                double distance = Math.Sqrt(Math.Pow(x[i] - x[j], 2) + Math.Pow(y[i] - y[j], 2));
                total += Math.Pow(Math.Max(0, MinimumDistance - distance), 2) * 10;
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
