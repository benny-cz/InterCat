using InterCat.Application;

namespace InterCat.Desktop;

/// <summary>
/// Where each of the graph's nodes is drawn while it moves to the place its layout gave it (§6.8). New places - a level
/// change's, a refresh settling, a re-layout's or a pin's - carry every node from where it is drawn now to its new place
/// along one eased path, so the eye follows each node instead of finding the graph rearranged. A node new to the drawing
/// appears at its place, and a node gone is forgotten. Under reduced motion every node is drawn at its place at once.
/// Places are the graph's own fractions of the pane, so a resize mid-motion moves only the pane.
/// </summary>
internal sealed class GraphMotion
{
    /// <summary>
    /// How near a later drawing's place must be to the one a node was moving to for its move to go on. The layout reads a
    /// remembered place through its drawn units and back, which can change the last bits of a fraction of the pane.
    /// </summary>
    private const double SamePlace = 1e-9;

    private Dictionary<string, Path> paths = new(StringComparer.Ordinal);
    private Dictionary<string, Path> spare = new(StringComparer.Ordinal);
    private TimeSpan settled;

    /// <summary>One node's move: from a place to its place, starting at a moment, over a length; none when zero.</summary>
    private readonly record struct Path(GraphPoint From, GraphPoint To, TimeSpan Start, TimeSpan Length);

    /// <summary>The places last taken: where every node is drawn, or is moving to. Null before any.</summary>
    public IReadOnlyDictionary<string, GraphPoint>? Places { get; private set; }

    /// <summary>
    /// Moves every node toward its place in <paramref name="places"/> from where it is drawn at <paramref name="now"/>, over
    /// <paramref name="length"/>. Under <paramref name="reduced"/> motion every node is drawn at its place at once.
    /// </summary>
    public void MoveTo(IReadOnlyDictionary<string, GraphPoint> places, TimeSpan now, TimeSpan length, bool reduced)
    {
        ArgumentNullException.ThrowIfNull(places);
        Take(places, (key, place) => !reduced && length > TimeSpan.Zero && At(key, now) is { } drawn && drawn != place
            ? new Path(drawn, place, now, length)
            : Still(place, now));
    }

    /// <summary>
    /// Takes the places of another drawing of the graph - a later publication's, or another session's. A node still bound
    /// for the place it was moving to goes on moving there; any other is drawn at its place at once, since a move toward a
    /// place it no longer has, or one in another drawing, would show a change that did not happen.
    /// </summary>
    public void Adopt(IReadOnlyDictionary<string, GraphPoint> places, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(places);
        Take(places, (key, place) => paths.TryGetValue(key, out Path path)
            && Math.Abs(path.To.X - place.X) <= SamePlace && Math.Abs(path.To.Y - place.Y) <= SamePlace
                ? path with { To = place }
                : Still(place, now));
    }

    /// <summary>
    /// Draws a node at <paramref name="place"/> from <paramref name="now"/> on, as a drop puts it: a node the pointer
    /// carried there never travels back to where it was before it was carried.
    /// </summary>
    public void Place(string key, GraphPoint place, TimeSpan now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (paths.ContainsKey(key))
        {
            paths[key] = Still(place, now);
            settled = Settled(paths);
        }
    }

    /// <summary>
    /// Carries a node back to its place from <paramref name="from"/>, where the pointer left it when a drag was cancelled,
    /// over <paramref name="length"/>; under <paramref name="reduced"/> motion it is drawn at its place at once.
    /// </summary>
    public void Return(string key, GraphPoint from, TimeSpan now, TimeSpan length, bool reduced)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (paths.TryGetValue(key, out Path path))
        {
            paths[key] = reduced || length <= TimeSpan.Zero || from == path.To
                ? Still(path.To, now)
                : new Path(from, path.To, now, length);
            settled = Settled(paths);
        }
    }

    /// <summary>Draws every node at its place from now on, as if every move under way had ended.</summary>
    public void Finish()
    {
        spare.Clear();
        foreach ((string key, Path path) in paths)
        {
            spare[key] = path with { From = path.To, Length = TimeSpan.Zero };
        }

        (paths, spare) = (spare, paths);
        spare.Clear();
        settled = TimeSpan.MinValue;
    }

    /// <summary>Where a node is drawn at <paramref name="now"/>: exactly its place once its move is done; null when not drawn.</summary>
    public GraphPoint? At(string key, TimeSpan now)
    {
        if (!paths.TryGetValue(key, out Path path))
        {
            return null;
        }

        TimeSpan elapsed = now - path.Start;
        if (path.Length <= TimeSpan.Zero || elapsed >= path.Length)
        {
            return path.To;
        }

        double eased = EaseOut(elapsed / path.Length);
        return new GraphPoint(
            path.From.X + ((path.To.X - path.From.X) * eased),
            path.From.Y + ((path.To.Y - path.From.Y) * eased));
    }

    /// <summary>Whether any node is still moving at <paramref name="now"/>.</summary>
    public bool IsMoving(TimeSpan now) => now < settled;

    /// <summary>§6.8's easing: quick to leave, settling into place. 0 at the start, exactly 1 at the end.</summary>
    public static double EaseOut(double progress)
    {
        double rest = 1 - Math.Clamp(progress, 0, 1);
        return 1 - (rest * rest * rest);
    }

    private static Path Still(GraphPoint place, TimeSpan now) => new(place, place, now, TimeSpan.Zero);

    private void Take(IReadOnlyDictionary<string, GraphPoint> places, Func<string, GraphPoint, Path> path)
    {
        // Paths are built into the spare map from the current ones, then the two swap, so a change reuses both maps.
        spare.Clear();
        foreach ((string key, GraphPoint place) in places)
        {
            spare[key] = path(key, place);
        }

        (paths, spare) = (spare, paths);
        spare.Clear();
        settled = Settled(paths);
        Places = places;
    }

    private static TimeSpan Settled(Dictionary<string, Path> paths)
    {
        TimeSpan last = TimeSpan.MinValue;
        foreach (Path path in paths.Values)
        {
            if (path.Length > TimeSpan.Zero && path.Start + path.Length > last)
            {
                last = path.Start + path.Length;
            }
        }

        return last;
    }
}
