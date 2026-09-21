using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The extended-data walker is exercised against a synthetic <c>EVENT_RECORD</c> built in native memory,
/// so the layout, the bounded copies and the omission counters are proved without ETW or elevation.
/// </summary>
public sealed class ExtendedDataReaderTests
{
    private const int EventRecordBytes = 112;

    [Fact(DisplayName = "I13: every declared extended item is copied or counted as an omission")]
    public void EveryDeclaredItemIsCopiedOrCounted()
    {
        using var record = new SyntheticEventRecord(
            new SyntheticItem(EtwExtendedDataTypes.ProcessStartKey, 0, [1, 2, 3, 4, 5, 6, 7, 8]),
            new SyntheticItem(EtwExtendedDataTypes.EventKey, 1, [9, 9, 9, 9]),
            new SyntheticItem(EtwExtendedDataTypes.ContainerId, 0, [3]),
            new SyntheticItem(EtwExtendedDataTypes.QpcDelta, 0, [4]),
            new SyntheticItem(EtwExtendedDataTypes.InstanceInfo, 0, [5]),
            new SyntheticItem(EtwExtendedDataTypes.PsmKey, 0, [6]));

        AdmittedEvent admitted = default;
        EtwExtendedDataReader.Copy(record.Pointer, ref admitted);

        Assert.Equal(ExtendedDataAvailability.Captured, admitted.ExtendedData);
        Assert.Equal(6, admitted.ExtendedItemsPresent);
        Assert.Equal(AdmittedEvent.MaximumExtendedItems, admitted.ExtendedItemsCopied);
        Assert.Equal(6 - AdmittedEvent.MaximumExtendedItems, admitted.ExtendedItemsOmitted);
        Assert.Equal(
            admitted.ExtendedItemsPresent,
            admitted.ExtendedItemsCopied + admitted.ExtendedItemsOmitted);

        AdmittedExtendedItem first = admitted.GetExtendedItem(0);
        Assert.Equal(EtwExtendedDataTypes.ProcessStartKey, first.Type);
        Assert.Equal(8, first.OriginalLength);
        Assert.Equal(8, first.CopiedLength);
        Assert.False(first.Truncated);
        Assert.Equal((ushort)1, admitted.GetExtendedItem(1).Linkage);
    }

    [Fact(DisplayName = "I21: an over-long extended item keeps its original length and a truncation flag")]
    public void OverLongItemIsTruncatedVisibly()
    {
        byte[] payload = new byte[AdmittedEvent.MaximumExtendedItemBytes * 3];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }

        using var record = new SyntheticEventRecord(new SyntheticItem(EtwExtendedDataTypes.Sid, 0, payload));

        AdmittedEvent admitted = default;
        EtwExtendedDataReader.Copy(record.Pointer, ref admitted);

        AdmittedExtendedItem item = admitted.GetExtendedItem(0);
        Assert.True(item.Truncated);
        Assert.Equal(payload.Length, item.OriginalLength);
        Assert.Equal(AdmittedEvent.MaximumExtendedItemBytes, item.CopiedLength);
        Span<byte> copied = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        Assert.Equal(AdmittedEvent.MaximumExtendedItemBytes, admitted.CopyExtendedItemBytes(0, copied));
        Assert.Equal(payload[..AdmittedEvent.MaximumExtendedItemBytes], copied.ToArray());
    }

    [Fact(DisplayName = "R9: an admitted record owns its extended bytes after the source buffer changes")]
    public void CopiedBytesSurviveSourceMutation()
    {
        using var record = new SyntheticEventRecord(
            new SyntheticItem(EtwExtendedDataTypes.EventKey, 0, [10, 20, 30, 40]));

        AdmittedEvent admitted = default;
        EtwExtendedDataReader.Copy(record.Pointer, ref admitted);
        record.OverwriteItemPayload(0, [99, 99, 99, 99]);

        Span<byte> copied = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        int length = admitted.CopyExtendedItemBytes(0, copied);

        Assert.Equal<byte>([10, 20, 30, 40], copied[..length].ToArray());
    }

    [Fact(DisplayName = "R3: a record that declares no extended item is distinguished from an unreadable one")]
    public void AbsenceAndUnreadabilityAreDifferentOutcomes()
    {
        using var empty = new SyntheticEventRecord();
        AdmittedEvent declaredNone = default;
        EtwExtendedDataReader.Copy(empty.Pointer, ref declaredNone);

        AdmittedEvent unreachable = default;
        EtwExtendedDataReader.Copy(0, ref unreachable);

        Assert.Equal(ExtendedDataAvailability.RecordCarriedNone, declaredNone.ExtendedData);
        Assert.Equal(0, declaredNone.ExtendedItemsPresent);
        Assert.Equal(ExtendedDataAvailability.UnavailableOnThisAdapter, unreachable.ExtendedData);
        Assert.Equal(0, unreachable.ExtendedItemsCopied);
    }

    [Fact(DisplayName = "R21: an item with a declared size but no data is counted, not read at a guess")]
    public void ItemWithoutDataIsCounted()
    {
        using var record = new SyntheticEventRecord(
            new SyntheticItem(EtwExtendedDataTypes.EventKey, 0, [1, 2]));
        record.ClearItemPointer(0);

        AdmittedEvent admitted = default;
        EtwExtendedDataReader.Copy(record.Pointer, ref admitted);

        Assert.Equal(ExtendedDataAvailability.Captured, admitted.ExtendedData);
        Assert.Equal(1, admitted.ExtendedItemsPresent);
        Assert.Equal(0, admitted.ExtendedItemsCopied);
        Assert.Equal(1, admitted.ExtendedItemsOmitted);
    }

    [Fact(DisplayName = "R21: the record accessor either reaches the callback record or states why it cannot")]
    public void AccessorEitherBindsOrExplainsItself()
    {
        TraceEventRecordAccessor accessor = TraceEventRecordAccessor.Bind();

        if (!accessor.IsAvailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(accessor.UnavailableReason));
            return;
        }

        Assert.Null(accessor.UnavailableReason);
        using var record = new SyntheticEventRecord(
            new SyntheticItem(EtwExtendedDataTypes.ProcessStartKey, 0, [8, 7, 6, 5]));
        var fabricated = (TraceEvent)RuntimeHelpers.GetUninitializedObject(typeof(TcpIpTraceData));
        SetEventRecord(fabricated, record.Pointer);

        Assert.Equal(record.Pointer, accessor.TryGetEventRecord(fabricated));
    }

    private static unsafe void SetEventRecord(TraceEvent target, nint value)
    {
        System.Reflection.FieldInfo field = typeof(TraceEvent).GetField(
            "eventRecord",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)!;
        field.SetValue(target, System.Reflection.Pointer.Box((void*)value, field.FieldType));
    }

    private sealed record SyntheticItem(ushort Type, ushort Linkage, byte[] Payload);

    /// <summary>
    /// A native <c>EVENT_RECORD</c> with a real extended-data array, laid out exactly as Windows does.
    /// It exists so the walker's offsets are asserted rather than assumed.
    /// </summary>
    private sealed class SyntheticEventRecord : IDisposable
    {
        private readonly nint record;
        private readonly nint items;
        private readonly nint[] payloads;
        private readonly int[] payloadLengths;

        public SyntheticEventRecord(params SyntheticItem[] declared)
        {
            record = Marshal.AllocHGlobal(EventRecordBytes);
            for (int offset = 0; offset < EventRecordBytes; offset++)
            {
                Marshal.WriteByte(record, offset, 0);
            }

            payloads = new nint[declared.Length];
            payloadLengths = new int[declared.Length];
            if (declared.Length == 0)
            {
                items = 0;
                return;
            }

            items = Marshal.AllocHGlobal(declared.Length * EventRecordLayout.ExtendedItemStride);
            for (int index = 0; index < declared.Length; index++)
            {
                SyntheticItem item = declared[index];
                nint payload = Marshal.AllocHGlobal(Math.Max(item.Payload.Length, 1));
                Marshal.Copy(item.Payload, 0, payload, item.Payload.Length);
                payloads[index] = payload;
                payloadLengths[index] = item.Payload.Length;

                nint slot = items + (index * EventRecordLayout.ExtendedItemStride);
                Marshal.WriteInt16(slot, 0, 0);
                Marshal.WriteInt16(slot, EventRecordLayout.ItemExtendedTypeOffset, unchecked((short)item.Type));
                Marshal.WriteInt16(slot, EventRecordLayout.ItemLinkageOffset, unchecked((short)item.Linkage));
                Marshal.WriteInt16(
                    slot,
                    EventRecordLayout.ItemDataSizeOffset,
                    unchecked((short)(ushort)item.Payload.Length));
                Marshal.WriteInt64(slot, EventRecordLayout.ItemDataPointerOffset, payload);
            }

            Marshal.WriteInt16(
                record,
                EventRecordLayout.HeaderFlagsOffset,
                unchecked((short)EventRecordLayout.HeaderFlagExtendedInfo));
            Marshal.WriteInt16(
                record,
                EventRecordLayout.ExtendedDataCountOffset,
                unchecked((short)(ushort)declared.Length));
            Marshal.WriteIntPtr(record, EventRecordLayout.ExtendedDataPointerOffset, items);
        }

        public nint Pointer => record;

        public void OverwriteItemPayload(int index, byte[] replacement) =>
            Marshal.Copy(replacement, 0, payloads[index], Math.Min(replacement.Length, payloadLengths[index]));

        public void ClearItemPointer(int index) => Marshal.WriteInt64(
            items + (index * EventRecordLayout.ExtendedItemStride),
            EventRecordLayout.ItemDataPointerOffset,
            0);

        public void Dispose()
        {
            foreach (nint payload in payloads)
            {
                if (payload != 0)
                {
                    Marshal.FreeHGlobal(payload);
                }
            }

            if (items != 0)
            {
                Marshal.FreeHGlobal(items);
            }

            Marshal.FreeHGlobal(record);
        }
    }
}
