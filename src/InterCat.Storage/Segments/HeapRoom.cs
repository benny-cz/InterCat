namespace InterCat.Storage;

/// <summary>
/// Room under the managed heap's limit for one allocation: the one a whole-generation pass holds every call record in,
/// or a column a reader reads. Under a limit the GC keeps the regions its collections free committed, for what it budgets
/// to allocate before its next, and an ordinary collection, a full compacting one too, keeps them: an allocation larger
/// than any of them is committed anew, and fails under the limit while the live heap is a third of it. When what the GC
/// held committed at its last collection, what has been allocated since (which it may have committed anew) and the
/// allocation could pass the memory the GC may use, and the GC kept more than an eighth of that committed beyond what its
/// heap held, an aggressive collection gives those regions back first (revisions 875 and 879). A heap that nears the
/// limit itself takes none, since a collection could give nothing back, and one collection leaves too little kept to
/// take another until the GC keeps that much again. Without a limit the GC may use the machine's memory, and nothing is
/// collected short of it.
/// </summary>
public static class HeapRoom
{
    private static long collections;

    /// <summary>The aggressive collections this process has taken for room, which a qualification counts as their cost.</summary>
    public static long Collections => Interlocked.Read(ref collections);

    /// <summary>Makes room for one allocation of <paramref name="bytes"/>; true when that took a collection.</summary>
    public static bool For(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        GCMemoryInfo heap = GC.GetGCMemoryInfo();
        long allocatedSince = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - heap.HeapSizeBytes);
        if (!Needed(heap.TotalCommittedBytes, heap.HeapSizeBytes, allocatedSince, heap.TotalAvailableMemoryBytes, bytes))
        {
            return false;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _ = Interlocked.Increment(ref collections);
        return true;
    }

    /// <summary>
    /// Whether an allocation of <paramref name="bytes"/> takes the collection: when what the last collection left
    /// <paramref name="committed"/>, what was allocated since and the allocation could pass what the GC may use, and the
    /// GC kept more than an eighth of that committed beyond the <paramref name="heap"/> it held.
    /// </summary>
    internal static bool Needed(long committed, long heap, long allocatedSince, long available, long bytes) =>
        committed + allocatedSince + bytes > available && committed - heap > available / 8;
}
