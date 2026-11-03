namespace InterCat.Storage;

/// <summary>
/// The readers a pass over a whole generation opens of its own and keeps across its stages - a release's plan - held to a
/// budget while they read, as the store's cached readers are held to theirs (revision 876). A reader that comes to hold
/// more past the budget has every other give back the columns it holds, the first opened first, its own kept for the stage
/// reading it, so a stage that reads every segment's columns holds the budget and the segment it reads rather than every
/// column it read: at three million rows a release's RPC pass read 285 MB of them beside the store's cache (S2, revision
/// 880). What a caller already read stays with it, and the next read of a column given back reads it and checks it again,
/// as after a trim. Values derived from rows stay until the pass gives them back itself.
/// </summary>
public sealed class SegmentReaderBudget
{
    /// <summary>A pass's readers' budget beside the store's cached readers': an eighth of theirs.</summary>
    public const long DefaultBytes = SessionStore.DefaultSegmentReaderCacheBytes / 8;

    private readonly Lock gate = new();
    private readonly List<SegmentReaderV1> readers = [];
    private readonly Action<SegmentReaderV1> grown;

    public SegmentReaderBudget(long budgetBytes = DefaultBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        BudgetBytes = budgetBytes;
        grown = Grown;
    }

    public long BudgetBytes { get; }

    /// <summary>What the readers opened through this budget hold now.</summary>
    public long HeldBytes
    {
        get
        {
            lock (gate)
            {
                return Held();
            }
        }
    }

    /// <summary>
    /// Opens a segment <paramref name="manifest"/> names, as <see cref="SessionSegments.Open(IOwnedDirectory, SessionManifestV1, string)"/>
    /// does, its reader held to this budget.
    /// </summary>
    public SegmentReaderV1 Open(IOwnedDirectory directory, SessionManifestV1 manifest, string segmentName)
    {
        SegmentReaderV1 reader = SessionSegments.Open(directory, manifest, segmentName);
        lock (gate)
        {
            readers.Add(reader);
        }

        reader.Grew = grown;
        return reader;
    }

    private void Grown(SegmentReaderV1 reader)
    {
        lock (gate)
        {
            long held = Held();
            foreach (SegmentReaderV1 other in readers)
            {
                if (held <= BudgetBytes)
                {
                    break;
                }

                if (!ReferenceEquals(other, reader))
                {
                    held -= other.GiveBackColumns();
                }
            }
        }
    }

    /// <summary>What every reader holds now. Called under the gate.</summary>
    private long Held()
    {
        long held = 0;
        foreach (SegmentReaderV1 reader in readers)
        {
            held += reader.ResidentBytes;
        }

        return held;
    }
}
