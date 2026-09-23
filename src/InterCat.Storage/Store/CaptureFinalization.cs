using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>
/// Durable evidence that a live capture reached its final publication. A complete journal chunk proves only that
/// one chunk committed; this companion is what distinguishes the capture's last committed chunk from an
/// intermediate live publication after a broker restart.
/// </summary>
public sealed record CaptureFinalizationV1
{
    public const string ContractName = "capture-finalization-v1";
    public const int MaximumBytes = 4_096;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public required string Contract { get; init; }

    public required Guid CaptureId { get; init; }

    /// <summary>
    /// Wall-clock evidence recorded only after the owned capture session's stop operation has returned. Whether that
    /// stop proved callback drain is recorded separately. This is provenance, not a source timestamp or record order.
    /// </summary>
    public required DateTimeOffset FinalizedUtc { get; init; }

    /// <summary>Whether provider shutdown had been observed before this final publication was staged.</summary>
    public required bool ProvidersStopped { get; init; }

    /// <summary>Whether the capture delivery pump proved callback drain before this final publication was staged.</summary>
    public required bool CallbacksDrained { get; init; }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException(
                $"The capture finalization marker is {bytes.Length} bytes, beyond its {MaximumBytes}-byte bound.");
    }

    public static CaptureFinalizationV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException(
                $"A {ContractName} file is empty or exceeds its {MaximumBytes}-byte bound.");
        }

        CaptureFinalizationV1 finalization;
        try
        {
            finalization = JsonSerializer.Deserialize<CaptureFinalizationV1>(bytes, Json)
                ?? throw new InvalidDataException("A capture finalization marker has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The capture finalization marker is not valid {ContractName} JSON.", exception);
        }

        finalization.Validate();
        return finalization;
    }

    /// <summary>
    /// Reads the exactly one finalization marker a generation carries and rechecks its immutable bytes against the
    /// dependency digest. SessionStore.Open already verifies dependencies, but this second small-file check binds the
    /// semantic decode to the same bytes the manifest names and makes this helper safe on any validated owned root.
    /// </summary>
    public static CaptureFinalizationV1? Read(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] markers =
        [
            .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.CaptureFinalization),
        ];
        if (markers.Length == 0)
        {
            return null;
        }

        if (markers.Length != 1)
        {
            throw new InvalidDataException(
                $"Generation {manifest.Generation} carries {markers.Length} capture finalization markers; exactly one is allowed.");
        }

        StoreDependency marker = markers[0];
        if (marker.LengthBytes is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException(
                $"Capture finalization dependency '{marker.Name}' is outside its {MaximumBytes}-byte bound.");
        }

        byte[] bytes = new byte[checked((int)marker.LengthBytes)];
        using (FileStream stream = directory.OpenOwnedFile(
            marker.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            if (stream.Length != marker.LengthBytes)
            {
                throw new InvalidDataException(
                    $"Capture finalization dependency '{marker.Name}' changed length after generation {manifest.Generation} was published.");
            }

            stream.ReadExactly(bytes);
        }

        string measured = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(measured, marker.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Capture finalization dependency '{marker.Name}' changed after generation {manifest.Generation} was published.");
        }

        return Decode(bytes);
    }

    public void Validate()
    {
        if (!string.Equals(Contract, ContractName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"A capture finalization marker must name contract '{ContractName}', not '{Contract}'.");
        }

        if (CaptureId == Guid.Empty)
        {
            throw new InvalidDataException("A capture finalization marker names the capture that reached final publication.");
        }

        if (FinalizedUtc == default)
        {
            throw new InvalidDataException("A capture finalization marker records when final publication began.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 8,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
