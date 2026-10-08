namespace InterCat.Analysis;

/// <summary>
/// What an operation's records owe one another across an interval release (ADR-043): when any row of one set stays -
/// retained, or kept - every released row of another is kept too. An RPC call, an ambiguous run of one, and an HTTP
/// exchange are one set both ways, so each stays whole; a client call owes the ALPC records and server call its other end
/// was read from. Only sets holding a released row are held, since nothing else could be given up.
/// </summary>
internal sealed class ReleaseDependencies
{
    private readonly List<RowAddress> rows = [];

    /// <summary>Per dependency: where its trigger rows lie in <see cref="rows"/>, and where the rows it keeps lie.</summary>
    private readonly List<(int When, int WhenEnd, int Then, int ThenEnd)> spans = [];

    private readonly Func<ulong, bool> released;

    public ReleaseDependencies(Func<ulong, bool> released)
    {
        ArgumentNullException.ThrowIfNull(released);
        this.released = released;
    }

    /// <summary>How many dependencies are held.</summary>
    public int Count => spans.Count;

    /// <summary>Records that the rows of one set stay together: any that stays keeps every other.</summary>
    public void Together(IReadOnlyCollection<RowAddress> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Count < 2 || !set.Any(row => released(row.Ordinal)))
        {
            return;
        }

        int start = rows.Count;
        rows.AddRange(set);
        spans.Add((start, rows.Count, start, rows.Count));
    }

    /// <summary>Records that when any of <paramref name="when"/> stays, every one of <paramref name="then"/> stays.</summary>
    public void Add(IReadOnlyCollection<RowAddress> when, IReadOnlyCollection<RowAddress> then)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(then);
        if (when.Count == 0 || !then.Any(row => released(row.Ordinal)))
        {
            return;
        }

        int start = rows.Count;
        rows.AddRange(when);
        int middle = rows.Count;
        rows.AddRange(then);
        spans.Add((start, middle, middle, rows.Count));
    }

    /// <summary>
    /// Keeps the released rows every dependency owes whose trigger has a row that stays; true when it kept any. A row
    /// stays when its record is not released or it is kept already.
    /// </summary>
    public bool Keep(HashSet<RowAddress> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        bool added = false;
        foreach ((int when, int whenEnd, int then, int end) in spans)
        {
            bool stays = false;
            for (int index = when; index < whenEnd && !stays; index++)
            {
                stays = !released(rows[index].Ordinal) || kept.Contains(rows[index]);
            }

            if (!stays)
            {
                continue;
            }

            for (int index = then; index < end; index++)
            {
                if (released(rows[index].Ordinal))
                {
                    added |= kept.Add(rows[index]);
                }
            }
        }

        return added;
    }
}
