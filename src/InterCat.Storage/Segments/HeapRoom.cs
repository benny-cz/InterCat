namespace InterCat.Storage;

/// <summary>
/// Room under the managed heap's limit for one allocation that grows with a session, such as the one a whole-generation
/// pass holds every call record in. Under a limit the GC keeps the regions its collections free committed, for what it
/// budgets to allocate before its next, and an ordinary collection, a full compacting one too, keeps them: an allocation
/// larger than any of them is committed anew, and fails under the limit while the live heap is a third of it. When what
/// the GC held committed at its last collection, what has been allocated since (which it may have committed anew) and the
/// allocation could pass the memory the GC may use, an aggressive collection gives those regions back first (revision
/// 875). Without a limit the GC may use the machine's memory, and nothing is collected short of it.
/// </summary>
public static class HeapRoom
{
    /// <summary>Makes room for one allocation of <paramref name="bytes"/>; true when that took a collection.</summary>
    public static bool For(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        GCMemoryInfo heap = GC.GetGCMemoryInfo();
        long allocatedSince = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - heap.HeapSizeBytes);
        if (!Needed(heap.TotalCommittedBytes, allocatedSince, heap.TotalAvailableMemoryBytes, bytes))
        {
            return false;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return true;
    }

    /// <summary>
    /// Whether an allocation of <paramref name="bytes"/> takes the collection: when what the last collection left
    /// <paramref name="committed"/>, what was allocated since and the allocation could pass what the GC may use.
    /// </summary>
    internal static bool Needed(long committed, long allocatedSince, long available, long bytes) =>
        committed + allocatedSince + bytes > available;
}
