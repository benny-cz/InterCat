using System.Diagnostics;
using System.Reflection;

namespace InterCat.Application;

/// <summary>
/// The errors nothing mapped that a process contained (§20.6 Internal), counted rather than logged (R11): how many in all,
/// and the first <see cref="MostListed"/> kinds of them, each by its type, its count and the methods of the code it was
/// thrown from. What an error says is never kept: it may hold what a record holds (P16), and the person was told it where
/// it happened.
/// </summary>
public sealed class InternalErrorTally
{
    /// <summary>The most kinds of error a tally lists; past them it counts each one in all, and lists no more.</summary>
    public const int MostListed = 16;

    /// <summary>The most methods a kind lists it was thrown from, the innermost first.</summary>
    public const int MostFrames = 32;

    private readonly Lock gate = new();
    private readonly List<SupportError> listed = [];
    private long contained;

    /// <summary>
    /// Counts <paramref name="exception"/>: with the kind thrown from the same methods when there is one, else as a kind of
    /// its own while fewer than <see cref="MostListed"/> are listed, and in all whatever it is.
    /// </summary>
    public void Count(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string kind = exception.GetType().FullName ?? exception.GetType().Name;
        string[] thrownFrom = ThrownFrom(exception);
        lock (gate)
        {
            contained++;
            int at = listed.FindIndex(error => error.Kind == kind && error.ThrownFrom.SequenceEqual(thrownFrom, StringComparer.Ordinal));
            if (at >= 0)
            {
                listed[at] = listed[at] with { Count = listed[at].Count + 1 };
            }
            else if (listed.Count < MostListed)
            {
                listed.Add(new(kind, 1, thrownFrom));
            }
        }
    }

    /// <summary>What the tally has counted, as a support bundle holds it.</summary>
    public SupportErrors Counted()
    {
        lock (gate)
        {
            return new(contained, [.. listed]);
        }
    }

    /// <summary>
    /// The methods <paramref name="exception"/> was thrown from, innermost first and at most <see cref="MostFrames"/>, each
    /// by its type's name and its own: no file, line or argument, so nothing but InterCat's and .NET's code is named.
    /// </summary>
    internal static string[] ThrownFrom(Exception exception) =>
        [.. new StackTrace(exception, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod())
            .OfType<MethodBase>()
            .Select(Named)
            .Take(MostFrames)];

    /// <summary>
    /// A method by its type and its name, where a state machine or an iterator the compiler made for a method - a type named
    /// after it, <c>&lt;Method&gt;d__7</c>, whose <c>MoveNext</c> runs its body - is named as the method it was written as,
    /// and a lambda or a closure by the type it was written in.
    /// </summary>
    private static string Named(MethodBase method)
    {
        string name = method.Name;
        Type? type = method.DeclaringType;
        while (type is { DeclaringType: { } outer } && type.Name.StartsWith('<'))
        {
            if (name == "MoveNext" && WrittenAs(type.Name) is { } written)
            {
                name = written;
            }

            type = outer;
        }

        // A frame names a generic type as it is declared, whatever it was made of.
        return $"{type?.FullName ?? type?.Name ?? "?"}.{name}";
    }

    /// <summary>
    /// The method a type the compiler made is named after: <c>Method</c> in <c>&lt;Method&gt;d__7</c>, the lambda
    /// <c>&lt;Method&gt;b__7_0</c> in <c>&lt;&lt;Method&gt;b__7_0&gt;d</c>; null for a closure, <c>&lt;&gt;c__DisplayClass7_0</c>.
    /// </summary>
    private static string? WrittenAs(string generated)
    {
        int depth = 0;
        for (int at = 0; at < generated.Length; at++)
        {
            depth += generated[at] switch { '<' => 1, '>' => -1, _ => 0 };
            if (depth == 0)
            {
                return at > 1 ? generated[1..at] : null;
            }
        }

        return null;
    }
}
