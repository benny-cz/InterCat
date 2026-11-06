using System.Runtime.InteropServices;

namespace InterCat.SectionProbe;

/// <summary>
/// The probe's own views, by their bases, as the memory manager describes the probe's own address space: every region of
/// mapped memory - a section's or a file's view - and every image's, each by the base of the view it belongs to and which of
/// the two it is. A rundown's records naming the probe's process are held to them: taken just before a rundown and again once
/// it is over, a view in both was mapped throughout it, and a base in neither was never one of the probe's views then.
/// </summary>
internal static partial class OwnViews
{
    private const uint MemoryMapped = 0x40000;
    private const uint MemoryImage = 0x1000000;
    private const uint MemoryFree = 0x10000;

    /// <summary>The base of every view mapped in the probe's own process now, an image's or not.</summary>
    public static Dictionary<ulong, bool> Bases()
    {
        var bases = new Dictionary<ulong, bool>();
        nuint length = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        nint address = 0;
        while (VirtualQuery(address, out MemoryBasicInformation region, length) != 0)
        {
            if (region.Type is MemoryMapped or MemoryImage && region.State != MemoryFree)
            {
                bases[(ulong)region.AllocationBase] = region.Type == MemoryImage;
            }

            nint next = region.BaseAddress + region.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return bases;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    // MEMORY_BASIC_INFORMATION as a 64-bit process lays it out.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
