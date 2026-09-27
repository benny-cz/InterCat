using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InterCat.Capture.Windows;

/// <summary>
/// Reads, from TDH, the fixed length and out type of every top-level binary field of a manifest provider's events
/// (<see cref="ManifestFieldShapes"/>). Only a length TDH states as fixed is reported: one that another field gives, or
/// an array, stays unreported, and its field stays unreachable. Nothing is reported when TDH cannot answer, which leaves
/// a manifest as TraceEvent rebuilt it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class TdhFieldShapeReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    // TRACE_EVENT_INFO: the property array follows 112 bytes of header; each EVENT_PROPERTY_INFO is 24 bytes.
    private const int PropertyCountOffset = 100;
    private const int TopLevelPropertyCountOffset = 104;
    private const int PropertyArrayOffset = 112;
    private const int PropertyInfoLength = 24;

    // EVENT_PROPERTY_INFO flags, and the TDH_INTYPE and TDH_OUTTYPE codes this reader knows.
    private const int PropertyStruct = 0x1;
    private const int PropertyParamLength = 0x2;
    private const int PropertyParamCount = 0x4;
    private const int PropertyParamFixedCount = 0x20;
    private const ushort InTypeBinary = 14;
    private const ushort OutTypeIpv6 = 24;

    public static IReadOnlyList<ManifestFieldShape> Read(Guid provider)
    {
        var shapes = new List<ManifestFieldShape>();
        foreach (EventDescriptor descriptor in Descriptors(provider))
        {
            ReadEvent(provider, descriptor, shapes);
        }

        return shapes;
    }

    private static List<EventDescriptor> Descriptors(Guid provider)
    {
        uint size = 0;
        uint status = TdhEnumerateManifestProviderEvents(ref provider, 0, ref size);
        if (status != ErrorInsufficientBuffer || size < 8)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (TdhEnumerateManifestProviderEvents(ref provider, buffer, ref size) != ErrorSuccess)
            {
                return [];
            }

            int count = Marshal.ReadInt32(buffer, 0);
            int available = (int)((size - 8) / (uint)Marshal.SizeOf<EventDescriptor>());
            var descriptors = new List<EventDescriptor>(Math.Min(count, available));
            for (int index = 0; index < Math.Min(count, available); index++)
            {
                descriptors.Add(Marshal.PtrToStructure<EventDescriptor>(buffer + 8 + (index * Marshal.SizeOf<EventDescriptor>())));
            }

            return descriptors;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadEvent(Guid provider, EventDescriptor descriptor, List<ManifestFieldShape> shapes)
    {
        uint size = 0;
        if (TdhGetManifestEventInformation(ref provider, ref descriptor, 0, ref size) != ErrorInsufficientBuffer
            || size < PropertyArrayOffset)
        {
            return;
        }

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (TdhGetManifestEventInformation(ref provider, ref descriptor, buffer, ref size) != ErrorSuccess)
            {
                return;
            }

            int properties = Marshal.ReadInt32(buffer, PropertyCountOffset);
            int topLevel = Marshal.ReadInt32(buffer, TopLevelPropertyCountOffset);
            if (topLevel < 0 || topLevel > properties || PropertyArrayOffset + ((long)properties * PropertyInfoLength) > size)
            {
                return;
            }

            for (int index = 0; index < topLevel; index++)
            {
                nint info = buffer + PropertyArrayOffset + (index * PropertyInfoLength);
                int flags = Marshal.ReadInt32(info, 0);
                int nameOffset = Marshal.ReadInt32(info, 4);
                ushort inType = (ushort)Marshal.ReadInt16(info, 8);
                ushort outType = (ushort)Marshal.ReadInt16(info, 10);
                ushort count = (ushort)Marshal.ReadInt16(info, 16);
                ushort length = (ushort)Marshal.ReadInt16(info, 18);
                if ((flags & (PropertyStruct | PropertyParamLength | PropertyParamCount)) != 0
                    || ((flags & PropertyParamFixedCount) != 0 && count != 1)
                    || inType != InTypeBinary
                    || length == 0
                    || nameOffset <= 0
                    || nameOffset >= size)
                {
                    continue;
                }

                string? name = Marshal.PtrToStringUni(buffer + nameOffset);
                if (!string.IsNullOrEmpty(name))
                {
                    shapes.Add(new(descriptor.Id, descriptor.Version, name, length, outType == OutTypeIpv6 ? "win:IPv6" : null));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("tdh.dll")]
    private static partial uint TdhEnumerateManifestProviderEvents(ref Guid providerGuid, nint buffer, ref uint bufferSize);

    [LibraryImport("tdh.dll")]
    private static partial uint TdhGetManifestEventInformation(
        ref Guid providerGuid,
        ref EventDescriptor eventDescriptor,
        nint buffer,
        ref uint bufferSize);

    /// <summary>EVENT_DESCRIPTOR, 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }
}
