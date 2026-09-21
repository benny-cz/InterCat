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
/// One admitted record: the header fields InterCat always keeps plus the allowlisted slot values of its
/// descriptor. Values are copied out of callback-owned memory before the callback returns (section 18.1).
/// A slot that was not read stays unknown; it never becomes a zero (R3).
/// </summary>
public struct AdmittedEvent
{
    /// <summary>Longest resource name a bounded admission copies. Longer names are truncated, and the
    /// truncation is recorded rather than hidden (R8, I21).</summary>
    public const int MaximumNameLength = 160;

    private AdmittedSlotBuffer slots;
    private AdmittedNameBuffer name;
    private Guid identifier;
    private byte knownMask;
    private byte nameLength;
    private bool nameTruncated;
    private bool identifierKnown;

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

    public void ClearSlots()
    {
        knownMask = 0;
        nameLength = 0;
        nameTruncated = false;
        identifierKnown = false;
    }
}
