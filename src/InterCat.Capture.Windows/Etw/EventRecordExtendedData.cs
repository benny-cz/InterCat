using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;

namespace InterCat.Capture.Windows;

/// <summary>
/// The <c>EVENT_RECORD</c> field offsets InterCat reads directly. Windows defines the header, the buffer
/// context, the extended-data array and the user data as separate parts of one record, so serializing the
/// user-data buffer alone would lose correlation and decoding context (section 18.1).
/// </summary>
/// <remarks>
/// The offsets below hold for both consumer pointer widths: the two counts sit at 84 and 86, and the first
/// pointer lands at 88 with no padding on either width. Only the pointer's own width differs, which
/// <see cref="nint"/> reads correctly. An extended-data item is a fixed 16 bytes on both widths because its
/// data pointer is declared <c>ULONGLONG</c> rather than as a native pointer.
/// </remarks>
public static class EventRecordLayout
{
    public const int HeaderFlagsOffset = 4;
    public const int ExtendedDataCountOffset = 84;
    public const int UserDataLengthOffset = 86;
    public const int ExtendedDataPointerOffset = 88;
    public const int ExtendedItemStride = 16;
    public const int ItemExtendedTypeOffset = 2;
    public const int ItemLinkageOffset = 4;
    public const int ItemDataSizeOffset = 6;
    public const int ItemDataPointerOffset = 8;

    /// <summary><c>EVENT_HEADER_FLAG_EXTENDED_INFO</c>: the record carries extended-data items.</summary>
    public const ushort HeaderFlagExtendedInfo = 0x0001;
}

/// <summary>
/// Copies a bounded prefix of every extended-data item out of callback-owned memory. It stores bytes, not
/// addresses, and never reads <c>UserContext</c>, so nothing in an admitted record can outlive the
/// callback's buffer (section 18.1, R9).
/// </summary>
public static class EtwExtendedDataReader
{
    /// <summary>
    /// Walks the record's extended-data array and copies what the per-record budget allows. Items past the
    /// budget, and items with no readable bytes, are counted as omissions rather than dropped silently.
    /// </summary>
    /// <param name="eventRecord">A pointer to the callback's <c>EVENT_RECORD</c>.</param>
    /// <param name="admitted">The record that takes ownership of the copied bytes.</param>
    public static unsafe void Copy(nint eventRecord, ref AdmittedEvent admitted)
    {
        if (eventRecord == 0)
        {
            admitted.BeginExtendedData(ExtendedDataAvailability.UnavailableOnThisAdapter, 0);
            return;
        }

        int present = (ushort)Marshal.ReadInt16(eventRecord, EventRecordLayout.ExtendedDataCountOffset);
        if (present == 0)
        {
            admitted.BeginExtendedData(ExtendedDataAvailability.RecordCarriedNone, 0);
            return;
        }

        nint items = Marshal.ReadIntPtr(eventRecord, EventRecordLayout.ExtendedDataPointerOffset);
        if (items == 0)
        {
            // The record declares items but does not point at them. The declared count is reported, because
            // zero copied items here would not be evidence that the record carried none.
            admitted.BeginExtendedData(ExtendedDataAvailability.UnavailableOnThisAdapter, present);
            return;
        }

        admitted.BeginExtendedData(ExtendedDataAvailability.Captured, present);
        for (int index = 0; index < present; index++)
        {
            nint item = items + (index * EventRecordLayout.ExtendedItemStride);
            ushort type = (ushort)Marshal.ReadInt16(item, EventRecordLayout.ItemExtendedTypeOffset);
            ushort linkage = (ushort)Marshal.ReadInt16(item, EventRecordLayout.ItemLinkageOffset);
            int size = (ushort)Marshal.ReadInt16(item, EventRecordLayout.ItemDataSizeOffset);
            nint data = (nint)Marshal.ReadInt64(item, EventRecordLayout.ItemDataPointerOffset);
            if (size == 0 || data == 0)
            {
                admitted.RecordOmittedExtendedItem();
                continue;
            }

            int copied = Math.Min(size, AdmittedEvent.MaximumExtendedItemBytes);
            var bytes = new ReadOnlySpan<byte>((void*)data, copied);
            if (!admitted.TryAppendExtendedItem(type, linkage, bytes, size))
            {
                admitted.RecordOmittedExtendedItem();
            }
        }
    }
}

/// <summary>
/// Reaches the <c>EVENT_RECORD</c> behind a TraceEvent callback argument. The managed wrapper exposes two
/// header identifiers but not the extended-data array, so IC-009 reads the record itself. This is a
/// deliberately disposable adapter detail: when the binding fails it reports why, and admission then records
/// extended data as unavailable instead of claiming that a record carried none (R21, P27).
/// </summary>
public sealed class TraceEventRecordAccessor
{
    private const string FieldName = "eventRecord";

    private readonly Func<TraceEvent, nint>? read;

    private TraceEventRecordAccessor(Func<TraceEvent, nint>? read, string? unavailableReason)
    {
        this.read = read;
        UnavailableReason = unavailableReason;
    }

    /// <summary>The process-wide accessor. Binding happens once, never inside a callback.</summary>
    public static TraceEventRecordAccessor Shared { get; } = Bind();

    public bool IsAvailable => read is not null;

    /// <summary>Why the accessor could not bind, or null when it did.</summary>
    public string? UnavailableReason { get; }

    /// <summary>The callback's record pointer, or zero when this adapter build cannot reach it.</summary>
    public nint TryGetEventRecord(TraceEvent data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return read is null ? 0 : read(data);
    }

    /// <summary>
    /// Builds an independent accessor rather than using the shared one. A caller that wants to assert the
    /// binding, or to re-check it after loading a different TraceEvent build, calls this.
    /// </summary>
    public static TraceEventRecordAccessor Bind()
    {
        try
        {
            FieldInfo? field = typeof(TraceEvent).GetField(
                FieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is null)
            {
                return new(
                    null,
                    $"TraceEvent {typeof(TraceEvent).Assembly.GetName().Version} has no '{FieldName}' field, "
                    + "so extended-data items cannot be read on this build.");
            }

            if (!field.FieldType.IsPointer)
            {
                return new(null, $"TraceEvent.{FieldName} is {field.FieldType}, not a record pointer.");
            }

            var method = new DynamicMethod(
                "InterCatReadEventRecord",
                typeof(nint),
                [typeof(TraceEvent)],
                typeof(TraceEvent).Module,
                skipVisibility: true);
            ILGenerator generator = method.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, field);
            generator.Emit(OpCodes.Conv_I);
            generator.Emit(OpCodes.Ret);
            return new((Func<TraceEvent, nint>)method.CreateDelegate(typeof(Func<TraceEvent, nint>)), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(null, $"The TraceEvent record accessor could not be built: {exception.Message}");
        }
    }
}
