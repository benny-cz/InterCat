using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>How an epoch's evidence was acquired, which decides what the epoch can know (`coverage-v1` §2).</summary>
public enum CoverageAcquisition
{
    /// <summary>A standalone file replayed through admission. A file records neither enablement nor keywords.</summary>
    EtlImport = 1,

    /// <summary>An owned live session, which knows what it enabled.</summary>
    LiveCapture = 2,
}

/// <summary>
/// `coverage-v1`: what a capture's sources could observe, over which readings, and what they are known to have lost.
/// It is evidence about the capture rather than a derivation of its journal, which holds admitted records only, so
/// nothing can rebuild it: a generation keeps it the way it keeps its normalizer plan (R21, §7.1 `CoverageInterval`).
/// </summary>
public sealed record CoverageLedgerV1
{
    public const string ContractName = "coverage-v1";
    public const int MaximumBytes = 1_048_576;
    public const int MaximumEpochs = 64;
    public const int MaximumDescriptors = 4_096;
    private const int MaximumProviderNameLength = 256;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public required string Contract { get; init; }

    public required IReadOnlyList<CoverageEpochV1> Epochs { get; init; }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException(
                $"The coverage ledger is {bytes.Length} bytes, beyond its {MaximumBytes}-byte bound.");
    }

    public static CoverageLedgerV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("A coverage-v1 file is empty or exceeds its 1 MiB bound.");
        }

        CoverageLedgerV1 ledger;
        try
        {
            ledger = JsonSerializer.Deserialize<CoverageLedgerV1>(bytes, Json)
                ?? throw new InvalidDataException("A coverage ledger has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The coverage ledger is not valid coverage-v1 JSON.", exception);
        }

        ledger.Validate();
        return ledger;
    }

    /// <summary>Refuses a ledger whose facts do not add up (`coverage-v1` §1, §3).</summary>
    public void Validate()
    {
        if (!string.Equals(Contract, ContractName, StringComparison.Ordinal)
            || Epochs is null or { Count: < 1 or > MaximumEpochs })
        {
            throw new InvalidDataException($"A coverage-v1 file names its contract and 1-{MaximumEpochs} epochs.");
        }

        for (int index = 0; index < Epochs.Count; index++)
        {
            CoverageEpochV1 epoch = Epochs[index] ?? throw new InvalidDataException("A coverage epoch is missing.");
            if (epoch.Epoch != index + 1)
            {
                throw new InvalidDataException("Coverage epochs are numbered 1, 2, ... in the order they occurred.");
            }

            Validate(epoch);
        }
    }

    private static void Validate(CoverageEpochV1 epoch)
    {
        string at = $"Coverage epoch {epoch.Epoch}";
        if (!Enum.IsDefined(epoch.Acquisition)
            || epoch.Collected is null
            || epoch.Collected.Count > MaximumDescriptors
            || epoch.Deliveries is null or { Count: > MaximumDescriptors }
            || epoch.Losses is null)
        {
            throw new InvalidDataException(
                $"{at} names how its evidence was acquired, what it collected, at most {MaximumDescriptors} "
                + "deliveries and its losses.");
        }

        if (epoch.FirstDeliveredNativeTicks.HasValue != epoch.LastDeliveredNativeTicks.HasValue
            || epoch.FirstDeliveredNativeTicks > epoch.LastDeliveredNativeTicks
            || epoch.FirstDeliveredNativeTicks.HasValue != (epoch.Deliveries.Count > 0))
        {
            throw new InvalidDataException(
                $"{at} is bounded by its first and last delivered readings exactly when something was delivered.");
        }

        var collected = new HashSet<(Guid, int, int)>();
        foreach (CoverageCollectedV1 descriptor in epoch.Collected)
        {
            if (descriptor is null
                || descriptor.ProviderId == Guid.Empty
                || string.IsNullOrWhiteSpace(descriptor.ProviderName)
                || descriptor.ProviderName.Length > MaximumProviderNameLength
                || descriptor.EventId is < 0 or > ushort.MaxValue
                || descriptor.Version is < 0 or > byte.MaxValue
                || !Enum.IsDefined(descriptor.Mechanism)
                || !collected.Add((descriptor.ProviderId, descriptor.EventId, descriptor.Version)))
            {
                throw new InvalidDataException($"{at} names a collected descriptor that is incomplete, out of range or repeated.");
            }
        }

        var delivered = new HashSet<(Guid, int?, int?)>();
        Int128 totalDelivered = 0;
        foreach (CoverageDeliveryV1 delivery in epoch.Deliveries)
        {
            if (delivery is null
                || (delivery.ProviderId == Guid.Empty && delivery.EventId.HasValue)
                || delivery.EventId is < 0 or > ushort.MaxValue
                || delivery.Version is < 0 or > byte.MaxValue
                || delivery.EventId.HasValue != delivery.Version.HasValue
                || !delivered.Add((delivery.ProviderId, delivery.EventId, delivery.Version)))
            {
                throw new InvalidDataException($"{at} names a delivery that is incomplete, out of range or repeated.");
            }

            // A provider the plan did not request is one delivery with no descriptor: every record of it met one
            // policy. Every other delivery is one descriptor.
            bool wholeProvider = delivery.EventId is null;
            Int128 undecodable = 0;
            foreach ((UndecodableReason reason, long records) in delivery.Undecodable ?? new Dictionary<UndecodableReason, long>())
            {
                if (!Enum.IsDefined(reason) || records < 1)
                {
                    throw new InvalidDataException($"{at} counts undecodable records under an undefined reason or none at all.");
                }

                undecodable += records;
            }

            totalDelivered += delivery.Delivered;
            if (totalDelivered > long.MaxValue
                || delivery.Delivered < 1
                || delivery.Admitted < 0
                || delivery.Omitted < 0
                || (Int128)delivery.Admitted + delivery.Omitted + undecodable != delivery.Delivered
                || (delivery.Omitted > 0) != delivery.Omission.HasValue
                || (delivery.Omission is { } omission && !Enum.IsDefined(omission))
                || wholeProvider != (delivery.Omission == OmissionReason.UnrequestedProvider)
                || (wholeProvider && delivery.Omitted != delivery.Delivered)
                || (delivery.Admitted > 0 && !collected.Contains((delivery.ProviderId, delivery.EventId!.Value, delivery.Version!.Value))))
            {
                throw new InvalidDataException(
                    $"{at} has a delivery whose outcomes do not add up to what it delivered, or that admitted records "
                    + "of a descriptor it did not collect.");
            }
        }

        var layers = new HashSet<LossLayer>();
        foreach (CoverageLossV1 loss in epoch.Losses)
        {
            if (loss is null || !Enum.IsDefined(loss.Layer) || loss.Lost < 0 || !layers.Add(loss.Layer))
            {
                throw new InvalidDataException($"{at} names a loss with an undefined or repeated layer, or a negative count.");
            }
        }

        LossLayer[] requiredLayers = epoch.Acquisition == CoverageAcquisition.EtlImport
            ? [LossLayer.SourceSession]
            : [LossLayer.SourceSession, LossLayer.ConsumerBuffers, LossLayer.CallbackQueue, LossLayer.Storage];
        if (requiredLayers.Any(layer => !layers.Contains(layer)))
        {
            throw new InvalidDataException(
                $"{at} omits a required measured loss layer. An absent counter is not a reported zero.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}

/// <summary>An interval over which the capture's configuration and admitted sources were unchanged (§22).</summary>
public sealed record CoverageEpochV1
{
    public required int Epoch { get; init; }

    public required CoverageAcquisition Acquisition { get; init; }

    /// <summary>The first native reading of any record a source delivered, admitted or not; null when none was.</summary>
    public long? FirstDeliveredNativeTicks { get; init; }

    /// <summary>The last such reading. Coverage outside the two readings is unknown.</summary>
    public long? LastDeliveredNativeTicks { get; init; }

    /// <summary>What the admission plan admitted, whether or not it delivered anything.</summary>
    public required IReadOnlyList<CoverageCollectedV1> Collected { get; init; }

    /// <summary>What became of every delivered record, by descriptor.</summary>
    public required IReadOnlyList<CoverageDeliveryV1> Deliveries { get; init; }

    /// <summary>What a layer reported lost and could not attribute to a descriptor.</summary>
    public required IReadOnlyList<CoverageLossV1> Losses { get; init; }
}

/// <summary>One descriptor the admission plan admitted, and the mechanism its records are.</summary>
public sealed record CoverageCollectedV1
{
    public required Guid ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public required int EventId { get; init; }

    public required int Version { get; init; }

    public required Mechanism Mechanism { get; init; }
}

/// <summary>
/// What became of one descriptor's delivered records: admitted, omitted by policy, or undecodable. A provider the plan
/// did not request is one delivery with no event and no version.
/// </summary>
public sealed record CoverageDeliveryV1
{
    public required Guid ProviderId { get; init; }

    public int? EventId { get; init; }

    public int? Version { get; init; }

    public required long Delivered { get; init; }

    public required long Admitted { get; init; }

    /// <summary>Why the omitted records were not admitted; null when none was omitted.</summary>
    public OmissionReason? Omission { get; init; }

    public required long Omitted { get; init; }

    /// <summary>The records that could not be read, by reason: the decode layer's loss.</summary>
    public IReadOnlyDictionary<UndecodableReason, long>? Undecodable { get; init; }
}

/// <summary>
/// What one layer reported lost. The count is records, except for <see cref="LossLayer.ConsumerBuffers"/>, which
/// counts buffers of unknown size. No loss of this version is located in time.
/// </summary>
public sealed record CoverageLossV1
{
    public required LossLayer Layer { get; init; }

    public required long Lost { get; init; }
}
