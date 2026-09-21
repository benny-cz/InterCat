using System.Runtime.CompilerServices;

namespace InterCat.Capture.Windows;

/// <summary>Bounded inline storage for one admitted resource name. No heap allocation (R9, R11).</summary>
[InlineArray(AdmittedEvent.MaximumNameLength)]
public struct AdmittedNameBuffer
{
#pragma warning disable IDE0044, IDE0051, CS0169 // An inline array is declared by exactly one field.
    private char element0;
#pragma warning restore IDE0044, IDE0051, CS0169
}

/// <summary>Fixed storage for the bounded set of admitted field values. No heap allocation (R9, R11).</summary>
[InlineArray(AdmissionPlanCompiler.MaximumSlots)]
public struct AdmittedSlotBuffer
{
#pragma warning disable IDE0044, IDE0051, CS0169 // An inline array is declared by exactly one field.
    private long element0;
#pragma warning restore IDE0044, IDE0051, CS0169
}

/// <summary>
/// Whether this record carries copies of the source record's extended-data items, and if not, why.
/// An adapter that cannot reach the items reports that instead of reporting none (R3, R21).
/// </summary>
public enum ExtendedDataAvailability
{
    /// <summary>The capture plan did not ask for extended items, so none were read.</summary>
    NotRequested = 0,

    /// <summary>The source record's items were walked; copied and omitted counts are both exact.</summary>
    Captured = 1,

    /// <summary>The source record declared no extended items at all.</summary>
    RecordCarriedNone = 2,

    /// <summary>The items exist but this adapter build cannot reach them. Their count stays unknown.</summary>
    UnavailableOnThisAdapter = 3,
}

/// <summary>One copied extended-data item's header. The bytes are copied separately (section 18.1).</summary>
public readonly record struct AdmittedExtendedItem(
    ushort Type,
    ushort Linkage,
    int OriginalLength,
    int CopiedLength,
    bool Truncated);

/// <summary>Packed headers for the bounded set of copied extended items. No heap allocation (R9).</summary>
[InlineArray(AdmittedEvent.MaximumExtendedItems)]
public struct AdmittedExtendedHeaderBuffer
{
#pragma warning disable IDE0044, IDE0051, CS0169 // An inline array is declared by exactly one field.
    private long element0;
#pragma warning restore IDE0044, IDE0051, CS0169
}

/// <summary>Bounded inline storage for copied extended-item bytes. No heap allocation (R9, R11).</summary>
[InlineArray(AdmittedEvent.MaximumExtendedItems * AdmittedEvent.MaximumExtendedItemBytes)]
public struct AdmittedExtendedByteBuffer
{
#pragma warning disable IDE0044, IDE0051, CS0169 // An inline array is declared by exactly one field.
    private byte element0;
#pragma warning restore IDE0044, IDE0051, CS0169
}

/// <summary>
/// One admitted record: the header fields InterCat always keeps plus the allowlisted slot values of its
/// descriptor. Values are copied out of callback-owned memory before the callback returns (section 18.1).
/// A slot that was not read stays unknown; it never becomes a zero (R3).
/// </summary>
public struct AdmittedEvent
{
    /// <summary>Longest resource name a bounded admission copies. Longer names are truncated, and the
    /// truncation is recorded rather than hidden (R8, I21).</summary>
    public const int MaximumNameLength = 160;

    /// <summary>How many extended-data items one record copies. Anything past this is counted, not lost.</summary>
    public const int MaximumExtendedItems = 4;

    /// <summary>Longest bounded copy of one extended-data item. A longer item is copied as a flagged prefix.</summary>
    public const int MaximumExtendedItemBytes = 64;

    private AdmittedSlotBuffer slots;
    private AdmittedNameBuffer name;
    private AdmittedExtendedHeaderBuffer extendedHeaders;
    private AdmittedExtendedByteBuffer extendedBytes;
    private Guid identifier;
    private byte knownMask;
    private byte nameLength;
    private bool nameTruncated;
    private bool identifierKnown;
    private byte extendedCopied;
    private ushort extendedPresent;
    private ushort extendedOmitted;
    private ExtendedDataAvailability extendedAvailability;

    public int SourceIndex { get; set; }
    public int EventId { get; set; }
    public int Version { get; set; }
    public int Opcode { get; set; }

    /// <summary>The source clock reading, preserved in its original encoding (I8).</summary>
    public long TimestampQpc { get; set; }

    /// <summary>The same reading as UTC ticks, kept separately so neither representation is lost (I8).</summary>
    public long TimestampUtcTicks { get; set; }

    /// <summary>Event-header process id. For several sources this is not the owning process (section 4.1).</summary>
    public int HeaderProcessId { get; set; }

    public int HeaderThreadId { get; set; }

    public int ProcessorNumber { get; set; }

    /// <summary>Monotonic ordinal within the capture stream, assigned in the callback (section 18.1).</summary>
    public long RecordOrdinal { get; set; }

    /// <summary>
    /// The event header's activity id. Several sources relate a start to its completion only through this
    /// field, so it is preserved rather than dropped with the rest of the header (section 18.1).
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>The header's related activity id, which links a nested activity to its parent.</summary>
    public Guid RelatedActivityId { get; set; }

    public readonly byte KnownSlotMask => knownMask;

    public void SetSlot(int index, long value)
    {
        slots[index] = value;
        knownMask |= (byte)(1 << index);
    }

    public readonly bool TryGetSlot(int index, out long value)
    {
        if ((knownMask & (1 << index)) == 0)
        {
            value = 0;
            return false;
        }

        value = slots[index];
        return true;
    }

    /// <summary>True when a bounded resource name was copied for this record.</summary>
    public readonly bool HasName => nameLength > 0;

    /// <summary>True when the descriptor carried an admitted identifier such as an interface UUID.</summary>
    public readonly bool HasIdentifier => identifierKnown;

    /// <summary>The admitted identifier. Unknown stays unknown: an absent one is never an empty GUID (R3).</summary>
    public readonly Guid Identifier => identifier;

    public void SetIdentifier(Guid value)
    {
        identifier = value;
        identifierKnown = true;
    }

    /// <summary>True when the name was longer than the bounded copy; the value is a prefix (I21).</summary>
    public readonly bool NameTruncated => nameTruncated;

    /// <summary>Copies a UTF-16 name into the bounded inline buffer without allocating (R9, R11).</summary>
    public void SetName(ReadOnlySpan<char> value, bool truncated)
    {
        int length = Math.Min(value.Length, MaximumNameLength);
        value[..length].CopyTo(name);
        nameLength = (byte)length;
        nameTruncated = truncated || value.Length > MaximumNameLength;
    }

    /// <summary>
    /// Copies the admitted name into the caller's buffer and returns its length. Inline storage is never
    /// exposed by reference, so a record copy can never alias another record's buffer.
    /// </summary>
    public readonly int CopyName(Span<char> destination)
    {
        if (nameLength == 0)
        {
            return 0;
        }

        int length = Math.Min(nameLength, destination.Length);
        for (int index = 0; index < length; index++)
        {
            destination[index] = name[index];
        }

        return length;
    }

    /// <summary>Resets every per-record value so a reused record can never inherit an earlier one's evidence.</summary>
    public void Clear()
    {
        knownMask = 0;
        nameLength = 0;
        nameTruncated = false;
        identifierKnown = false;
        extendedCopied = 0;
        extendedPresent = 0;
        extendedOmitted = 0;
        extendedAvailability = ExtendedDataAvailability.NotRequested;
    }

    /// <summary>Whether extended items were copied for this record, and if not, why (R21).</summary>
    public readonly ExtendedDataAvailability ExtendedData => extendedAvailability;

    /// <summary>Items the source record declared. Zero while availability is unavailable means unknown.</summary>
    public readonly int ExtendedItemsPresent => extendedPresent;

    /// <summary>Items copied into this record's bounded inline storage.</summary>
    public readonly int ExtendedItemsCopied => extendedCopied;

    /// <summary>
    /// Items the record declared but this admission did not copy, because the per-record slot budget was
    /// exhausted or the item carried no readable bytes. They are counted, never silently dropped (I13).
    /// </summary>
    public readonly int ExtendedItemsOmitted => extendedOmitted;

    /// <summary>
    /// Declares how extended data was handled for this record and how many items the source carried.
    /// Call this once per record before appending items.
    /// </summary>
    public void BeginExtendedData(ExtendedDataAvailability availability, int presentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(presentCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(presentCount, ushort.MaxValue);

        extendedAvailability = availability;
        extendedPresent = (ushort)presentCount;
        extendedCopied = 0;
        extendedOmitted = 0;
    }

    /// <summary>
    /// Copies one extended item's bytes into inline storage. Returns false when the record's bounded slot
    /// budget is full; the caller records that as an omission rather than growing the record (R9).
    /// </summary>
    public bool TryAppendExtendedItem(ushort type, ushort linkage, ReadOnlySpan<byte> bytes, int originalLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(originalLength, bytes.Length);
        if (extendedCopied == MaximumExtendedItems)
        {
            return false;
        }

        int copied = Math.Min(bytes.Length, MaximumExtendedItemBytes);
        int start = extendedCopied * MaximumExtendedItemBytes;
        Span<byte> storage = extendedBytes;
        bytes[..copied].CopyTo(storage.Slice(start, copied));
        extendedHeaders[extendedCopied] = PackHeader(
            type,
            linkage,
            originalLength,
            copied,
            truncated: copied < originalLength);
        extendedCopied++;
        return true;
    }

    /// <summary>Counts one declared item this admission did not copy, with no claim about its contents.</summary>
    public void RecordOmittedExtendedItem()
    {
        if (extendedOmitted < ushort.MaxValue)
        {
            extendedOmitted++;
        }
    }

    /// <summary>The header of one copied extended item.</summary>
    public readonly AdmittedExtendedItem GetExtendedItem(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, extendedCopied);
        return UnpackHeader(extendedHeaders[index]);
    }

    /// <summary>
    /// Copies one item's retained bytes into the caller's buffer and returns the length written. Inline
    /// storage is never handed out by reference, so no record can alias another record's bytes.
    /// </summary>
    public readonly int CopyExtendedItemBytes(int index, Span<byte> destination)
    {
        AdmittedExtendedItem item = GetExtendedItem(index);
        int length = Math.Min(item.CopiedLength, destination.Length);
        int start = index * MaximumExtendedItemBytes;
        ReadOnlySpan<byte> storage = extendedBytes;
        storage.Slice(start, length).CopyTo(destination);
        return length;
    }

    internal static long PackHeader(ushort type, ushort linkage, int originalLength, int copiedLength, bool truncated)
    {
        long packed = type;
        packed |= (long)linkage << 16;
        packed |= (long)Math.Min(originalLength, ushort.MaxValue) << 32;
        packed |= (long)(byte)copiedLength << 48;
        if (truncated)
        {
            packed |= 1L << 56;
        }

        return packed;
    }

    internal static AdmittedExtendedItem UnpackHeader(long packed) => new(
        (ushort)(packed & 0xFFFF),
        (ushort)((packed >> 16) & 0xFFFF),
        (int)((packed >> 32) & 0xFFFF),
        (int)((packed >> 48) & 0xFF),
        ((packed >> 56) & 1) != 0);
}
