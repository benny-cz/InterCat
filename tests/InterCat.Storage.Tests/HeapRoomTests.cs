using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// §12's analysis budget under a heap limit: a whole-generation pass about to hold an allocation that grows with the session
/// asks the GC for the regions it keeps committed only when that allocation could pass what the GC may use (revision 875).
/// </summary>
public sealed class HeapRoomTests
{
    [Fact(DisplayName = "§12: an allocation takes a collection first only when what the GC held committed, what was allocated since and the allocation could pass what it may use")]
    public void AnAllocationTakesACollectionOnlyWhenItCouldPassTheLimit()
    {
        // What the last collection left committed, what was allocated since, the memory the GC may use and the allocation.
        Assert.False(HeapRoom.Needed(committed: 300, allocatedSince: 50, available: 400, bytes: 50));
        Assert.True(HeapRoom.Needed(committed: 300, allocatedSince: 50, available: 400, bytes: 51));
        Assert.True(HeapRoom.Needed(committed: 300, allocatedSince: 101, available: 400, bytes: 0));

        // A finished rolling capture's checkpoint under a 384 MiB limit: its last collection left 312 MB committed for a live
        // heap of 61 MB, beside which 88 MB of call records fit; 143 MB had been allocated since, and the records, committed
        // anew, did not.
        Assert.False(HeapRoom.Needed(committed: 312_422_400, allocatedSince: 0, available: 402_653_184, bytes: 87_499_664));
        Assert.True(HeapRoom.Needed(committed: 312_422_400, allocatedSince: 143_376_472, available: 402_653_184, bytes: 87_499_664));

        // Without a limit the GC may use the machine's memory, far past what a test holds, and nothing is collected.
        Assert.False(HeapRoom.For(1 << 20));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => HeapRoom.For(-1));
    }
}
