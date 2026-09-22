using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// A record's canonical identity: a digest over the source it came from and everything the admitted
/// envelope actually carries. It is comparable, so an import can sort by it without re-deriving it, and
/// it is 32 bytes, so a sort entry is fixed width and can spill to disk without a length prefix.
/// </summary>
public readonly record struct CanonicalRecordKey(UInt128 High, UInt128 Low)
    : IComparable<CanonicalRecordKey>
{
    public const int Size = 32;

    public static CanonicalRecordKey Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt128BigEndian(source),
        BinaryPrimitives.ReadUInt128BigEndian(source[16..]));

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt128BigEndian(destination, High);
        BinaryPrimitives.WriteUInt128BigEndian(destination[16..], Low);
    }

    public int CompareTo(CanonicalRecordKey other) =>
        High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);

    public static bool operator <(CanonicalRecordKey left, CanonicalRecordKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(CanonicalRecordKey left, CanonicalRecordKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(CanonicalRecordKey left, CanonicalRecordKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(CanonicalRecordKey left, CanonicalRecordKey right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{High:x32}{Low:x32}");
}

/// <summary>
/// Computes canonical record keys. Everything the envelope persists is in the key; nothing that belongs
/// to the run that read it is. A callback-lifetime pointer, a consumer's context value or the position a
/// record happened to arrive in would make two imports of the same bytes disagree, so §18.4 excludes
/// them and this builder has no way to include them.
/// </summary>
public static class CanonicalRecordKeyBuilder
{
    public static CanonicalRecordKey Create(ImportSourceIdentity source, RecordEnvelopeV1 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrEmpty(source.ContentDigest))
        {
            throw new ArgumentException("A canonical key needs a hashed source.", nameof(source));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> scratch = stackalloc byte[16];
        hash.AppendData(Encoding.UTF8.GetBytes(CanonicalImportContract.RecordKeyDomain));
        hash.AppendData(Encoding.UTF8.GetBytes(source.ToString()));

        // The clock and the reading on it, in the encoding the source used. A converted reading is never
        // keyed: I8 keeps the native value authoritative, and two encodings of one instant are not the
        // same evidence.
        AppendGuid(hash, scratch, record.ClockId.Value);
        AppendU32(hash, scratch, (uint)record.TimestampEncoding);
        AppendI64(hash, scratch, record.NativeTicks);

        EventHeaderFieldsV1 header = record.Header;
        AppendGuid(hash, scratch, header.ProviderId);
        AppendU32(hash, scratch, header.EventId);
        AppendU32(hash, scratch, header.Version);
        AppendU32(hash, scratch, header.Channel);
        AppendU32(hash, scratch, header.Level);
        AppendU32(hash, scratch, header.Opcode);
        AppendU32(hash, scratch, header.Task);
        AppendU64(hash, scratch, header.Keyword);
        AppendU32(hash, scratch, header.Flags);
        AppendU32(hash, scratch, header.EventProperty);
        AppendI64(hash, scratch, header.ProcessId);
        AppendI64(hash, scratch, header.ThreadId);
        AppendGuid(hash, scratch, header.ActivityId);
        AppendGuid(hash, scratch, header.RelatedActivityId);

        // The buffer context is recorded content, not consumer state: which processor produced a record
        // and which logger carried it are facts about the record, and two otherwise identical records
        // from different processors are two records rather than one seen twice.
        AppendU32(hash, scratch, record.BufferContext.ProcessorNumber);
        AppendU32(hash, scratch, record.BufferContext.LoggerId);
        AppendU32(hash, scratch, record.PointerSize);

        AppendU32(hash, scratch, (uint)record.ExtendedItems.Count);
        foreach (ExtendedItemV1 item in record.ExtendedItems)
        {
            AppendU32(hash, scratch, item.Type);
            AppendU32(hash, scratch, item.Flags);
            AppendI64(hash, scratch, item.OriginalLength);
            AppendBytes(hash, scratch, item.Bytes.Span);
        }

        AppendI64(hash, scratch, record.OmittedExtendedItemCount);
        AppendU32(hash, scratch, (uint)record.Body.Classification);
        AppendU32(hash, scratch, (uint)record.Body.Disposition);
        AppendI64(hash, scratch, record.Body.OriginalLength);
        AppendBytes(hash, scratch, record.Body.Bytes.Span);

        Span<byte> digest = stackalloc byte[CanonicalRecordKey.Size];
        _ = hash.TryGetHashAndReset(digest, out _);
        return CanonicalRecordKey.Read(digest);
    }

    private static void AppendGuid(IncrementalHash hash, Span<byte> scratch, Guid value)
    {
        _ = value.TryWriteBytes(scratch, bigEndian: true, out _);
        hash.AppendData(scratch);
    }

    private static void AppendU32(IncrementalHash hash, Span<byte> scratch, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(scratch, value);
        hash.AppendData(scratch[..4]);
    }

    private static void AppendU64(IncrementalHash hash, Span<byte> scratch, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(scratch, value);
        hash.AppendData(scratch[..8]);
    }

    private static void AppendI64(IncrementalHash hash, Span<byte> scratch, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(scratch, value);
        hash.AppendData(scratch[..8]);
    }

    /// <summary>
    /// Length-prefixes every variable-length field, so no concatenation of a shorter body and a longer
    /// item can ever hash the same as another record's fields.
    /// </summary>
    private static void AppendBytes(IncrementalHash hash, Span<byte> scratch, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt64BigEndian(scratch, value.Length);
        hash.AppendData(scratch[..8]);
        hash.AppendData(value);
    }
}
