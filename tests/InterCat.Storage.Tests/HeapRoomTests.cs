using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §12's analysis budget under a heap limit: an allocation - a whole-generation pass's call records, a column a reader
/// reads - asks the GC for the regions it keeps committed only when the allocation could pass what the GC may use and
/// the GC keeps enough beyond its heap to give back (revisions 875 and 879).
/// </summary>
public sealed class HeapRoomTests
{
    [Fact(DisplayName = "§12: an allocation takes a collection first only when what the GC held committed, what was allocated since and the allocation could pass what it may use")]
    public void AnAllocationTakesACollectionOnlyWhenItCouldPassTheLimit()
    {
        // What the last collection left committed, the heap it held, what was allocated since, the memory the GC may use
        // and the allocation.
        Assert.False(HeapRoom.Needed(committed: 300, heap: 100, allocatedSince: 50, available: 400, bytes: 50));
        Assert.True(HeapRoom.Needed(committed: 300, heap: 100, allocatedSince: 50, available: 400, bytes: 51));
        Assert.True(HeapRoom.Needed(committed: 300, heap: 100, allocatedSince: 101, available: 400, bytes: 0));

        // A finished rolling capture's checkpoint under a 384 MiB limit: its last collection left 312 MB committed for a live
        // heap of 61 MB, beside which 88 MB of call records fit; 143 MB had been allocated since, and the records, committed
        // anew, did not.
        Assert.False(HeapRoom.Needed(committed: 312_422_400, heap: 61_221_264, allocatedSince: 0, available: 402_653_184, bytes: 87_499_664));
        Assert.True(HeapRoom.Needed(committed: 312_422_400, heap: 61_221_264, allocatedSince: 143_376_472, available: 402_653_184, bytes: 87_499_664));

        // Without a limit the GC may use the machine's memory, far past what a test holds, and nothing is collected.
        Assert.False(HeapRoom.For(1 << 20));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => HeapRoom.For(-1));
    }

    [Fact(DisplayName = "§12: a heap that nears its limit itself takes no collection for an allocation, since the GC keeps too little beyond it to give back")]
    public void AHeapNearItsLimitTakesNoCollection()
    {
        // The GC keeps an eighth of what it may use beyond its heap, or more: a collection gives it back.
        Assert.True(HeapRoom.Needed(committed: 380, heap: 329, allocatedSince: 10, available: 400, bytes: 20));

        // It keeps less: the heap itself is what fills the limit, and a collection would give nothing back. So does a
        // collection just taken, which left what was committed at what the heap held.
        Assert.False(HeapRoom.Needed(committed: 380, heap: 330, allocatedSince: 10, available: 400, bytes: 20));
        Assert.False(HeapRoom.Needed(committed: 330, heap: 330, allocatedSince: 60, available: 400, bytes: 20));
    }
}
