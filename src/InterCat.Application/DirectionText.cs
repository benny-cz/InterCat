using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Who made a relationship's connections, in the words its edge's card, its row in the relationship table and the row's
/// explanation share (§6.3, §6.8, `contracts/relations-v1.md` §5): the end whose connect - or whose peer's accept - the
/// capture saw, counted where they went both ways or some opened unseen, and display order only where it saw none open.
/// The graph draws the same as its arrowheads.
/// </summary>
public static class DirectionText
{
    /// <summary>
    /// The line a card and an explanation state: "Direction: client.exe · PID 100 connected to server.exe · PID 200",
    /// "..., in all 4 of its connections" where there were more, each way counted where they went both ways or some opened
    /// unseen; display order only where none was seen to open, as for a UDP flow, which nothing opens, and for RPC calls,
    /// whose ends' rows say which called.
    /// </summary>
    /// <param name="fromSource">How many of its connections <paramref name="source"/> made.</param>
    /// <param name="fromTarget">How many <paramref name="target"/> made.</param>
    /// <param name="connections">How many it holds, opened where the capture saw it or not.</param>
    public static string Line(Mechanism mechanism, int fromSource, int fromTarget, int connections, string source, string target)
    {
        if (mechanism == Mechanism.Rpc)
        {
            return "Direction: display order only; each end's RPC rows say which calls and which served";
        }

        if (fromSource + fromTarget == 0)
        {
            return "Direction: display order only, not who initiated or sent";
        }

        string all = connections > 1
            ? string.Create(CultureInfo.CurrentCulture, $", in all {connections:N0} of its connections")
            : string.Empty;
        if (fromSource == connections)
        {
            return $"Direction: {source} connected to {target}{all}";
        }

        if (fromTarget == connections)
        {
            return $"Direction: {target} connected to {source}{all}";
        }

        int unseen = connections - fromSource - fromTarget;
        return "Direction: " + string.Join(", ", new[]
            {
                fromSource > 0 ? $"{source} connected to {target} in " + CountText.Of(fromSource, "connection") : null,
                fromTarget > 0 ? $"{target} connected to {source} in " + CountText.Of(fromTarget, "connection") : null,
            }.OfType<string>())
            + (unseen > 0 ? "; the capture saw " + CountText.Of(unseen, "connection") + " open neither way" : string.Empty);
    }

    /// <summary>
    /// The mark a table sets between a relationship's ends: "→" where only the source connected, "←" where only the
    /// target did, "⇄" where each did, and none where the capture saw none open, so no direction is invented (§6.6).
    /// </summary>
    public static string Glyph(int fromSource, int fromTarget) => (fromSource > 0, fromTarget > 0) switch
    {
        (true, false) => "→",
        (false, true) => "←",
        (true, true) => "⇄",
        _ => string.Empty,
    };

    /// <summary>
    /// The same as a screen reader says it within a row's name: "client.exe · PID 100 connected to server.exe · PID 200",
    /// or "each connected to the other"; null where none was seen to open, so the name says nothing of it.
    /// </summary>
    public static string? Spoken(int fromSource, int fromTarget, string source, string target) => (fromSource > 0, fromTarget > 0) switch
    {
        (true, false) => $"{source} connected to {target}",
        (false, true) => $"{target} connected to {source}",
        (true, true) => "each connected to the other",
        _ => null,
    };
}
