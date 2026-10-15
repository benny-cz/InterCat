using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What a generation's capture admits, as the normalizer plan it retains beside its journal says
/// (`contracts/normalizer-plan-v1.md`): the mechanisms of the descriptors its admission planned. A capture publishes its
/// plan with its first chunk, so the plan speaks for it while it records, before the coverage ledger it publishes with its
/// last says what it collected.
/// </summary>
public static class CaptureAdmissions
{
    /// <summary>The contract a plan this reader reads names (`contracts/normalizer-plan-v1.md`).</summary>
    private const string Contract = "normalizer-plan-v1";

    /// <summary>
    /// The mechanisms the generation's retained plan admits a descriptor of; null when it retains none, or one of another
    /// contract, as a later writer's may be, which says nothing this reader can read of what it admitted.
    /// </summary>
    public static IReadOnlySet<Mechanism>? Of(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        if (SessionSegments.NormalizerPlan(directory, manifest) is not { } plan)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(plan);
            if (!string.Equals(document.RootElement.GetProperty("contract").GetString(), Contract, StringComparison.Ordinal))
            {
                return null;
            }

            var admitted = new HashSet<Mechanism>();
            foreach (JsonElement descriptor in document.RootElement.GetProperty("descriptors").EnumerateArray())
            {
                _ = admitted.Add((Mechanism)descriptor.GetProperty("mechanism").GetInt32());
            }

            return admitted;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException)
        {
            throw new InvalidDataException($"The retained normalizer plan is not {Contract} JSON.", exception);
        }
    }
}
