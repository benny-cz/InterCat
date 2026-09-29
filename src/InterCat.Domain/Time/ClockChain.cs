namespace InterCat.Domain;

/// <summary>One link of a clock chain: a clock's mapping into the clock it was aligned to.</summary>
public sealed record ClockLink(Guid Clock, ClockMapping Mapping);

/// <summary>An instant's uncertainty through a chain; when it is unknown, the clock whose link made it so.</summary>
public readonly record struct ChainUncertainty(TimeUncertainty? Uncertainty, Guid? UnknownAt);

/// <summary>
/// How one clock reaches the workspace's time (§8.2): its own mapping into the clock it was aligned to, that clock's into
/// the one it was aligned to, and so on to the time reference, whose chain is empty. An instant passes through each link in
/// turn, and so does its uncertainty: each link adds its own, taken at the widest instant the one carried so far allows,
/// and carries the rest at its rate. Two chains that share their upper links meet in one clock, and there compare as a
/// shared mapping allows: its errors move both instants alike, save a drift over the time between them and roundings.
/// </summary>
public sealed class ClockChain
{
    public ClockChain(IReadOnlyList<ClockLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        Links = links;
    }

    /// <summary>The time reference's own chain: no link, its instants exact.</summary>
    public static ClockChain Reference { get; } = new([]);

    /// <summary>From the clock's own mapping up to the one into the time reference.</summary>
    public IReadOnlyList<ClockLink> Links { get; }

    /// <summary>A session instant in workspace time.</summary>
    public long ToWorkspace(long sessionNanoseconds)
    {
        long instant = sessionNanoseconds;
        foreach (ClockLink link in Links)
        {
            instant = link.Mapping.ToWorkspace(instant);
        }

        return instant;
    }

    /// <summary>The session instant a workspace instant maps from.</summary>
    public long FromWorkspace(long workspaceNanoseconds)
    {
        long instant = workspaceNanoseconds;
        for (int index = Links.Count - 1; index >= 0; index--)
        {
            instant = Links[index].Mapping.FromWorkspace(instant);
        }

        return instant;
    }

    /// <summary>The uncertainty of a session instant in workspace time.</summary>
    public ChainUncertainty UncertaintyAt(long sessionNanoseconds) => Carry(sessionNanoseconds, sessionNanoseconds, Links.Count).Uncertainty;

    /// <summary>The widest uncertainty of any session instant from <paramref name="first"/> to <paramref name="last"/>.</summary>
    public ChainUncertainty WidestUncertainty(long first, long last) => Carry(first, last, Links.Count).Uncertainty;

    /// <summary>
    /// Compares an instant of one chain's clock with an instant of another's in workspace time, stating an order only beyond
    /// the pair's uncertainty (§8.2). The links the two share count once: each chain's own links carry its instant into the
    /// clock where they meet, as independent sides, and the shared links above it add only the drift their errors may make
    /// over the time between the two instants, and each instant's own roundings. Two instants of one clock are ordered
    /// exactly, on that clock.
    /// </summary>
    public static TimeComparison Compare(ClockChain first, long firstNanoseconds, ClockChain second, long secondNanoseconds)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        int shared = 0;
        while (shared < first.Links.Count && shared < second.Links.Count
            && first.Links[^(shared + 1)].Clock == second.Links[^(shared + 1)].Clock)
        {
            shared++;
        }

        if (shared == first.Links.Count && shared == second.Links.Count)
        {
            return TimeComparison.OnOneClock(firstNanoseconds, secondNanoseconds);
        }

        (long a, long _, ChainUncertainty ownA) = first.Carry(firstNanoseconds, firstNanoseconds, first.Links.Count - shared);
        (long b, long _, ChainUncertainty ownB) = second.Carry(secondNanoseconds, secondNanoseconds, second.Links.Count - shared);
        if (ownA.Uncertainty is not { } ua || ownB.Uncertainty is not { } ub)
        {
            return new(TimeOrder.Unknown, null, null);
        }

        // In the clock where they meet the two are independent sides. Above it each shared link scales them, and its errors
        // move both alike but for its growth over the distance the two may lie apart, and each instant's own roundings.
        double bound = ua.BoundNanoseconds + ub.BoundNanoseconds;
        double random = Math.Sqrt((ua.RandomNanoseconds * ua.RandomNanoseconds) + (ub.RandomNanoseconds * ub.RandomNanoseconds));
        for (int index = first.Links.Count - shared; index < first.Links.Count; index++)
        {
            ClockMapping mapping = first.Links[index].Mapping;
            double apart = Math.Abs((double)checked(b - a)) + bound + random;
            if (mapping.Growth is not { } growth)
            {
                if (apart > 0)
                {
                    return new(TimeOrder.Unknown, null, null);
                }

                growth = 0;
            }

            bound = (bound * mapping.Scale) + (growth * apart) + (2 * mapping.PerInstantNanoseconds);
            random *= mapping.Scale;
            (a, b) = (mapping.ToWorkspace(a), mapping.ToWorkspace(b));
        }

        long difference = checked(b - a);
        var pair = new TimeUncertainty(bound, random);
        return Math.Abs((double)difference) > pair.HalfWidthNanoseconds
            ? new(difference > 0 ? TimeOrder.Before : TimeOrder.After, difference, pair)
            : new(TimeOrder.Ambiguous, difference, pair);
    }

    /// <summary>
    /// Carries a span of session instants through the first <paramref name="links"/> links: where its ends arrive, and the
    /// widest uncertainty of any instant in it there. Each link adds its own uncertainty at the widest instant the span,
    /// widened by what has been carried so far, reaches, and scales what was carried at its rate.
    /// </summary>
    private (long Low, long High, ChainUncertainty Uncertainty) Carry(long first, long last, int links)
    {
        long low = Math.Min(first, last);
        long high = Math.Max(first, last);
        double bound = 0;
        double random = 0;
        for (int index = 0; index < links; index++)
        {
            ClockLink link = Links[index];
            long spread = checked((long)Math.Ceiling(bound + random));
            if (link.Mapping.WidestUncertainty(checked(low - spread), checked(high + spread)) is not { } own)
            {
                return (low, high, new(null, link.Clock));
            }

            double scale = link.Mapping.Scale;
            bound = (bound * scale) + own.BoundNanoseconds;
            random = Math.Sqrt((random * scale * random * scale) + (own.RandomNanoseconds * own.RandomNanoseconds));
            (low, high) = (link.Mapping.ToWorkspace(low), link.Mapping.ToWorkspace(high));
        }

        return (low, high, new(new TimeUncertainty(bound, random), null));
    }
}
