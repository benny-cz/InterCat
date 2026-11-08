using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What of the SDK's schema never reached a capture (ADR-111): each announcing process's schema beside the SDK events the
/// capture's retained plan admitted and the minor this InterCat knows, in one set of words for the command line and
/// InterCat's window (R18).
/// </summary>
public static class CooperativeSchemaText
{
    /// <summary>The contract a plan this reader reads names (`contracts/normalizer-plan-v1.md`).</summary>
    private const string Contract = "normalizer-plan-v1";

    /// <summary>
    /// The SDK events the generation's capture admitted, as the normalizer plan it retains says; null where it retains none,
    /// or one of another contract, which says nothing this reader can read of what it admitted.
    /// </summary>
    public static IReadOnlySet<ushort>? AdmittedEvents(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
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

            var admitted = new HashSet<ushort>();
            foreach (JsonElement descriptor in document.RootElement.GetProperty("descriptors").EnumerateArray())
            {
                if (descriptor.TryGetProperty("providerGuid", out JsonElement provider) && provider.GetGuid() == CooperativeSdk.ProviderId)
                {
                    _ = admitted.Add(descriptor.GetProperty("eventId").GetUInt16());
                }
            }

            return admitted;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException)
        {
            throw new InvalidDataException($"The retained normalizer plan is not {Contract} JSON.", exception);
        }
    }

    /// <summary>
    /// What of the schema the announcing processes speak never reached the capture, a sentence for each schema announced: a
    /// minor later than this InterCat knows, whose added events no capture of it admits; or the events of a minor it knows,
    /// content's aside, that the capture's plan did not admit. None where nothing was missed, and of known minors none where
    /// the plan does not say what the capture admitted.
    /// </summary>
    public static IReadOnlyList<string> Unreached(
        IReadOnlyList<CooperativeAnnouncement> announcements,
        IReadOnlySet<ushort>? admitted,
        ProcessInstanceIndex processes,
        EvidencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(announcements);
        ArgumentNullException.ThrowIfNull(processes);
        string known = string.Create(CultureInfo.InvariantCulture, $"{CooperativeSdk.SchemaMajor}.{CooperativeSdk.SchemaMinor}");
        var said = new List<string>();
        foreach (IGrouping<int, CooperativeAnnouncement> schema in announcements
            .Where(announcement => announcement.Major == CooperativeSdk.SchemaMajor && announcement.Minor is not null)
            .GroupBy(announcement => announcement.Minor!.Value)
            .OrderBy(schema => schema.Key))
        {
            int minor = schema.Key;
            string who = string.Join(", ", schema.Select(announcement => Process(processes, policy, announcement)).Distinct());
            string version = string.Create(CultureInfo.InvariantCulture, $"{CooperativeSdk.SchemaMajor}.{minor}");
            if (minor > CooperativeSdk.SchemaMinor)
            {
                said.Add($"{who} announced the SDK's schema {version}, later than the {known} this InterCat knows: what it added "
                    + "never reached the capture, and is counted nowhere here.");
                continue;
            }

            // A content event a capture keeping none leaves out by choice, never for want of knowing it (ADR-107).
            int[] missed = admitted is null ? [] : [.. Enumerable.Range(0, minor + 1).Where(added =>
                CooperativeSdk.AddedBy(added).Any(id => !CooperativeSdk.ContentOnly(id) && !admitted.Contains(id)))];
            if (missed.Length > 0)
            {
                said.Add($"{who} announced the SDK's schema {version}, but the capture admitted none of "
                    + string.Join(" or ", missed.Select(What)) + ": they never reached it, and are counted nowhere here.");
            }
        }

        return said;
    }

    /// <summary>What a minor added, as a sentence names it.</summary>
    private static string What(int minor) => minor switch
    {
        0 => "the channels, messages and operations minor 0 began with",
        1 => "the batches minor 1 added",
        _ => string.Create(CultureInfo.InvariantCulture, $"what minor {minor} added"),
    };

    /// <summary>
    /// The process an announcement names, as a listing says it: its executable and PID where the evidence policy admits its
    /// binding, or its PID and why no instance holds it.
    /// </summary>
    public static string Process(ProcessInstanceIndex processes, EvidencePolicy policy, CooperativeAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(announcement);
        if (announcement.Process.IsAdmittedUnder(policy))
        {
            ProcessInstance instance = processes.Instances[announcement.Process.Instance];
            return string.Create(CultureInfo.CurrentCulture, $"{instance.ImageName ?? "executable not witnessed"} · {instance.ProcessId}");
        }

        ProcessBindingReason reason = announcement.Process.IsBound ? ProcessBindingReason.NotAdmittedByPolicy : announcement.Process.Reason;
        return string.Create(CultureInfo.CurrentCulture, $"PID {announcement.ProcessId} · {BindingText.Reason(reason)}");
    }
}
