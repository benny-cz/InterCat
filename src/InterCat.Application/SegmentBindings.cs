using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Each row's owner and channel under one derivation, packed four bytes a row and kept with the segment's reader
/// (<see cref="SegmentReaderV1.DerivedRows{T}"/>) for as long as the reader cache keeps it. A brushed count, a focused
/// timeline and an evidence page read these, derived once per segment and derivation, rather than binding every row
/// again from the seven to thirteen columns a binding reads, on every query (revision 169).
/// </summary>
/// <remarks>
/// An owner packs the instance's position plus one in the low 24 bits (0 for none), its strength in the next 3 and the
/// reason in the top 5; a channel packs its number plus one in the low 27 bits (0 for none) and the reason in the top 5.
/// A derivation beyond those bounds - 16 million instances, 134 million channels - is refused rather than wrapped.
/// </remarks>
internal static class SegmentBindings
{
    private const string OwnerKind = "process-owner-bindings";
    private const string ChannelKind = "transport-channel-bindings";
    private const uint InstanceMask = (1u << 24) - 1;
    private const uint ChannelMask = (1u << 27) - 1;

    /// <summary>Each row's owner binding under <paramref name="processes"/>.</summary>
    public static PackedOwners OwnersOf(SegmentReaderV1 segment, ProcessInstanceIndex processes)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(processes);
        return new(segment.DerivedRows(OwnerKind, processes, reader => Pack(processes.OwnersOf(reader))));
    }

    /// <summary>Each row's channel binding under <paramref name="relations"/>.</summary>
    public static PackedChannels ChannelsOf(SegmentReaderV1 segment, TransportRelationIndex relations)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(relations);
        return new(segment.DerivedRows(ChannelKind, relations, reader => Pack(relations.ChannelsOf(reader))));
    }

    internal static uint Pack(ProcessBinding binding)
    {
        uint instance = binding.Instance < 0 ? 0 : (uint)binding.Instance + 1;
        if (instance > InstanceMask || (uint)binding.Strength > 7 || (uint)binding.Reason > 31)
        {
            throw new InvalidOperationException(
                "An owner binding does not fit four bytes: more than 16,777,214 instances, or a code outside its set.");
        }

        return instance | ((uint)binding.Strength << 24) | ((uint)binding.Reason << 27);
    }

    internal static ProcessBinding UnpackOwner(uint packed) => new(
        (int)(packed & InstanceMask) - 1,
        (RelationStrength)((packed >> 24) & 7),
        (ProcessBindingReason)(packed >> 27));

    internal static uint Pack(ChannelBinding binding)
    {
        uint channel = binding.Channel < 0 ? 0 : (uint)binding.Channel + 1;
        if (channel > ChannelMask || (uint)binding.Reason > 31)
        {
            throw new InvalidOperationException(
                "A channel binding does not fit four bytes: more than 134,217,726 channels, or a code outside its set.");
        }

        return channel | ((uint)binding.Reason << 27);
    }

    internal static ChannelBinding UnpackChannel(uint packed) =>
        new((int)(packed & ChannelMask) - 1, (ProcessBindingReason)(packed >> 27));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint[] Pack(ProcessBinding[] bindings)
    {
        uint[] packed = new uint[bindings.Length];
        for (int row = 0; row < bindings.Length; row++)
        {
            packed[row] = Pack(bindings[row]);
        }

        return packed;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint[] Pack(ChannelBinding[] bindings)
    {
        uint[] packed = new uint[bindings.Length];
        for (int row = 0; row < bindings.Length; row++)
        {
            packed[row] = Pack(bindings[row]);
        }

        return packed;
    }
}

/// <summary>A segment's owner bindings under one derivation, four bytes a row (<see cref="SegmentBindings"/>).</summary>
internal readonly struct PackedOwners(uint[] packed)
{
    public ProcessBinding this[int row] => SegmentBindings.UnpackOwner(packed[row]);
}

/// <summary>A segment's channel bindings under one derivation, four bytes a row (<see cref="SegmentBindings"/>).</summary>
internal readonly struct PackedChannels(uint[] packed)
{
    public ChannelBinding this[int row] => SegmentBindings.UnpackChannel(packed[row]);
}
