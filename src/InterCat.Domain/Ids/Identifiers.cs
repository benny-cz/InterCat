using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Domain;

public readonly record struct CaptureId(Guid Value)
{
    public static CaptureId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct HostId(Guid Value)
{
    public static HostId New() => new(Guid.NewGuid());

    /// <summary>
    /// A deterministic identity for the machine this process runs on, so two captures taken here share one
    /// host and their native clocks stay comparable. It is derived from the machine name and OS
    /// description, which makes it stable across boots and reruns but not globally unique: two hosts with
    /// the same name and build produce the same value. Cross-host correlation therefore needs the stronger
    /// evidence IC-013 owns, never this identity alone.
    /// </summary>
    /// <summary>
    /// A host identity derived from evidence rather than from this machine. An imported file was
    /// recorded somewhere, and claiming it was recorded here would let an import's records be compared
    /// against local captures as if they shared a clock.
    /// </summary>
    public static HostId Derive(string canonicalForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalForm);
        return new(StableIdentityHash.CreateUuidV8(canonicalForm));
    }

    public static HostId ForLocalMachine() => new(StableIdentityHash.CreateUuidV8(string.Create(
        CultureInfo.InvariantCulture,
        $"intercat.host.v1|{Environment.MachineName}|{RuntimeInformation.OSDescription}|{RuntimeInformation.OSArchitecture}")));

    public override string ToString() => Value.ToString("N");
}

public readonly record struct BootId(Guid Value)
{
    public static BootId New() => new(Guid.NewGuid());

    /// <summary>
    /// A boot identity derived from evidence rather than minted. A monotonic source clock does not survive a
    /// restart, so a capture's clock scopes exactly one boot of its host: deriving the boot from the clock gives
    /// every process key of one capture the same boot, and two captures on different clocks different ones, without
    /// claiming anything the evidence does not say about which boot of the machine it was.
    /// </summary>
    public static BootId DeriveFromClock(ClockId clock) =>
        new(StableIdentityHash.CreateUuidV8(string.Create(
            CultureInfo.InvariantCulture,
            $"intercat.boot.v1|clock|{clock}")));

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ClockId(Guid Value)
{
    public static ClockId New() => new(Guid.NewGuid());

    /// <summary>
    /// A clock identity derived from evidence rather than minted for a run. Evidence recorded outside
    /// InterCat carries no clock ID of ours, and minting one per import would make two imports of the
    /// same file disagree about which clock its readings are on.
    /// </summary>
    public static ClockId Derive(string canonicalForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalForm);
        return new(StableIdentityHash.CreateUuidV8(canonicalForm));
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ProcessInstanceId(Guid Value)
{
    public static ProcessInstanceId New() => new(Guid.NewGuid());
    public static ProcessInstanceId FromKey(ProcessInstanceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(StableIdentityHash.CreateUuidV8(key.CanonicalForm));
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ResourceInstanceId(Guid Value)
{
    public static ResourceInstanceId FromKey(ResourceInstanceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(StableIdentityHash.CreateUuidV8(key.CanonicalForm));
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct RawRecordId(
    CaptureId CaptureId,
    uint StreamId,
    uint SourceEpoch,
    ulong RecordOrdinal)
{
    internal string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"{CaptureId}:{StreamId}:{SourceEpoch}:{RecordOrdinal}");
}

public readonly record struct NormalizerContractVersion
{
    public static NormalizerContractVersion V1 { get; } = new(1);

    public NormalizerContractVersion(uint value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    public uint Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A stable, semantic subrecord key. It is derived from the fact discriminator and occurrence, never
/// from decoder scheduling or collection iteration order.
/// </summary>
public readonly record struct FactKey(ulong High, ulong Low)
{
    public static FactKey Create(string discriminator, uint occurrence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        byte[] name = Encoding.UTF8.GetBytes(discriminator.Normalize(NormalizationForm.FormC));
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(discriminator), "A fact discriminator is limited to 65,535 UTF-8 bytes.");
        }

        byte[] canonical = new byte[sizeof(ushort) + name.Length + sizeof(uint)];
        BinaryPrimitives.WriteUInt16BigEndian(canonical, (ushort)name.Length);
        name.CopyTo(canonical.AsSpan(sizeof(ushort)));
        BinaryPrimitives.WriteUInt32BigEndian(canonical.AsSpan(sizeof(ushort) + name.Length), occurrence);
        byte[] hash = SHA256.HashData(canonical);
        return new(
            BinaryPrimitives.ReadUInt64BigEndian(hash),
            BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(sizeof(ulong))));
    }

    public static bool TryParse(string? value, out FactKey key)
    {
        key = default;
        if (value is null || value.Length != 32
            || !ulong.TryParse(value.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong high)
            || !ulong.TryParse(value.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong low))
        {
            return false;
        }

        key = new(high, low);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{High:x16}{Low:x16}");
}

/// <summary>
/// Stable normalized-fact identity. The two-argument constructor exists only to read M0 evidence that
/// predates the identity contract; production normalizers should call <see cref="Create"/>.
/// </summary>
[JsonConverter(typeof(ObservationIdJsonConverter))]
public readonly record struct ObservationId(
    RawRecordId RawRecordId,
    NormalizerContractVersion NormalizerContractVersion,
    FactKey FactKey)
{
    public ObservationId(RawRecordId rawRecordId, ushort legacyFactIndex)
        : this(
            rawRecordId,
            NormalizerContractVersion.V1,
            FactKey.Create("intercat.legacy-fact-index", legacyFactIndex))
    {
    }

    public static ObservationId Create(
        RawRecordId rawRecordId,
        NormalizerContractVersion normalizerContractVersion,
        string factDiscriminator,
        uint occurrence = 0) =>
        new(rawRecordId, normalizerContractVersion, FactKey.Create(factDiscriminator, occurrence));
}

/// <summary>Reads the pre-contract factIndex shape and writes only the canonical v1 wire shape.</summary>
public sealed class ObservationIdJsonConverter : JsonConverter<ObservationId>
{
    public override ObservationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        RawRecordId rawRecordId = root.GetProperty("rawRecordId").Deserialize<RawRecordId>(options);

        if (root.TryGetProperty("factIndex", out JsonElement legacyIndex))
        {
            return new(rawRecordId, legacyIndex.GetUInt16());
        }

        uint version = root.GetProperty("normalizerContractVersion").GetUInt32();
        string? factKeyText = root.GetProperty("factKey").GetString();
        if (!FactKey.TryParse(factKeyText, out FactKey factKey))
        {
            throw new JsonException("factKey must be a 32-character lowercase or uppercase hexadecimal value.");
        }

        return new(rawRecordId, new NormalizerContractVersion(version), factKey);
    }

    public override void Write(Utf8JsonWriter writer, ObservationId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("rawRecordId");
        JsonSerializer.Serialize(writer, value.RawRecordId, options);
        writer.WriteNumber("normalizerContractVersion", value.NormalizerContractVersion.Value);
        writer.WriteString("factKey", value.FactKey.ToString());
        writer.WriteEndObject();
    }
}

internal static class StableIdentityHash
{
    public static Guid CreateUuidV8(string canonicalForm)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForm));
        Span<byte> uuid = stackalloc byte[16];
        hash.AsSpan(0, uuid.Length).CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x80);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }
}
