using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>When a service inventory was read: as its capture started, or as it stopped (`contracts/service-inventory-v1.md` §2).</summary>
public enum ServiceInventoryMoment
{
    /// <summary>Read as the capture started, once its session ran.</summary>
    Start = 1,

    /// <summary>Read as the capture stopped.</summary>
    Stop = 2,
}

/// <summary>One running service, by its name and its host's PID and creation time.</summary>
public sealed record InventoriedServiceV1
{
    /// <summary>The service's name, as the service control manager lists it.</summary>
    public required string Name { get; init; }

    /// <summary>The PID of the process hosting it when the list was read, which the machine may give another process later.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// When the operating system created the host, at its 100 ns resolution: the instant a lifecycle record of the same
    /// instance carries. Null when the host could not be opened to read it.
    /// </summary>
    public DateTimeOffset? HostCreatedUtc { get; init; }
}

/// <summary>
/// The services running at one moment of a capture, as the service control manager lists them, each with the process
/// hosting it (`contracts/service-inventory-v1.md`, ADR-095): evidence about the capture, which no journal holds and nothing
/// can rebuild, kept with it like its collector identities.
/// </summary>
public sealed record ServiceInventoryV1
{
    public const string ContractName = "service-inventory-v1";
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumServices = 4_096;
    public const int MaximumNameLength = 256;

    private const string FilePrefix = "service-inventory-";
    private const string FileSuffix = ".json";

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public required string Contract { get; init; }

    public required Guid CaptureId { get; init; }

    public required ServiceInventoryMoment Moment { get; init; }

    /// <summary>
    /// When the list began to be read. The list is no atomic view of the machine: a service can stop, start or change host
    /// while it is read, so the read's start and end bound when each service listed was running (§18.5).
    /// </summary>
    public required DateTimeOffset ReadStartedUtc { get; init; }

    /// <summary>When the list had been read, no earlier than <see cref="ReadStartedUtc"/>.</summary>
    public required DateTimeOffset ReadEndedUtc { get; init; }

    /// <summary>The running services the manager listed with a process, each once.</summary>
    public required IReadOnlyList<InventoriedServiceV1> Services { get; init; }

    /// <summary>
    /// The name an inventory of <paramref name="moment"/> published by <paramref name="generation"/> takes: one generation
    /// can publish both, as a capture published once at its stop does.
    /// </summary>
    public static string FileNameFor(ServiceInventoryMoment moment, long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return Enum.IsDefined(moment)
            ? string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{MomentName(moment)}-{generation:D10}{FileSuffix}")
            : throw new ArgumentOutOfRangeException(nameof(moment), moment, "An inventory is of a capture's start or its stop.");
    }

    /// <summary>The moment an inventory's published name says it is of; null for a name of another form.</summary>
    public static ServiceInventoryMoment? MomentNamedBy(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.StartsWith(FilePrefix + MomentName(ServiceInventoryMoment.Start) + "-", StringComparison.Ordinal)
                ? ServiceInventoryMoment.Start
            : fileName.StartsWith(FilePrefix + MomentName(ServiceInventoryMoment.Stop) + "-", StringComparison.Ordinal)
                ? ServiceInventoryMoment.Stop
            : null;
    }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException($"The service inventory is {bytes.Length} bytes, beyond its {MaximumBytes}-byte bound.");
    }

    public static ServiceInventoryV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"A {ContractName} file is empty or exceeds its {MaximumBytes}-byte bound.");
        }

        ServiceInventoryV1 inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<ServiceInventoryV1>(bytes, Json)
                ?? throw new InvalidDataException("A service inventory file has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The service inventory is not valid {ContractName} JSON.", exception);
        }

        inventory.Validate();
        return inventory;
    }

    /// <summary>
    /// The inventories a generation carries, its start's before its stop's, each's bytes checked against the digest its
    /// generation records; empty for a generation that carries none, as an import's, a redacted package's and every
    /// capture's that wrote none do.
    /// </summary>
    public static IReadOnlyList<ServiceInventoryV1> Read(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] files = [.. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.ServiceInventory)];
        var inventories = new List<(long Generation, ServiceInventoryV1 Inventory)>(files.Length);
        foreach (StoreDependency file in files)
        {
            ServiceInventoryV1 inventory = ReadOne(directory, manifest, file);
            inventories.Add((PublishedBy(file, inventory.Moment, manifest), inventory));
        }

        if (inventories.GroupBy(static entry => entry.Inventory.Moment).Any(static moment => moment.Count() > 1)
            || inventories.Select(static entry => entry.Inventory.CaptureId).Distinct().Count() > 1)
        {
            throw new InvalidDataException(
                $"Generation {manifest.Generation} carries {files.Length} service inventories; a capture has one of its start and one of its stop.");
        }

        // A stop's inventory is published with the capture's last generation, never before its start's, and with it where the
        // capture published once.
        if (inventories.Count == 2
            && inventories.Single(static entry => entry.Inventory.Moment == ServiceInventoryMoment.Stop).Generation
                < inventories.Single(static entry => entry.Inventory.Moment == ServiceInventoryMoment.Start).Generation)
        {
            throw new InvalidDataException($"Generation {manifest.Generation} carries a stop's service inventory published before its start's.");
        }

        return [.. inventories.OrderBy(static entry => entry.Inventory.Moment).Select(static entry => entry.Inventory)];
    }

    public void Validate()
    {
        string? problem = !string.Equals(Contract, ContractName, StringComparison.Ordinal)
                ? $"it must name contract '{ContractName}', not '{Contract}'"
            : CaptureId == Guid.Empty ? "it names no capture"
            : !Enum.IsDefined(Moment) ? "it names no moment it knows"
            : ReadEndedUtc < ReadStartedUtc ? "its read ended before it began"
            : Services is null || Services.Count > MaximumServices ? $"it lists no services, or more than {MaximumServices}"
            : Services.Any(static service => service is null || service.Name is null || service.Name.Length is < 1 or > MaximumNameLength)
                ? $"a service has no name, or one longer than {MaximumNameLength} characters"
            : Services.Any(static service => service.ProcessId < 1) ? "a service names no PID"
            : Services.Select(static service => service.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Services.Count
                ? "it names one service twice"
            : null;
        if (problem is not null)
        {
            throw new InvalidDataException($"A service inventory is refused: {problem}.");
        }
    }

    private static ServiceInventoryV1 ReadOne(IOwnedDirectory directory, SessionManifestV1 manifest, StoreDependency file)
    {
        if (file.LengthBytes is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"Service inventory '{file.Name}' is outside its {MaximumBytes}-byte bound.");
        }

        byte[] bytes = new byte[checked((int)file.LengthBytes)];
        using (FileStream stream = directory.OpenOwnedFile(
            file.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            if (stream.Length != file.LengthBytes)
            {
                throw new InvalidDataException(
                    $"Service inventory '{file.Name}' changed length after generation {manifest.Generation} was published.");
            }

            stream.ReadExactly(bytes);
        }

        string measured = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(measured, file.Digest, StringComparison.Ordinal)
            ? Decode(bytes)
            : throw new InvalidDataException(
                $"Service inventory '{file.Name}' changed after generation {manifest.Generation} was published.");
    }

    // The generation a file's name says published it, which must name the moment its inventory is of.
    private static long PublishedBy(StoreDependency file, ServiceInventoryMoment moment, SessionManifestV1 manifest)
    {
        string prefix = FilePrefix + MomentName(moment) + "-";
        return file.Name.StartsWith(prefix, StringComparison.Ordinal) && file.Name.EndsWith(FileSuffix, StringComparison.Ordinal)
            && long.TryParse(file.Name.AsSpan(prefix.Length, file.Name.Length - prefix.Length - FileSuffix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out long generation)
            && generation >= 1 && generation <= manifest.Generation
            ? generation
            : throw new InvalidDataException(
                $"Service inventory '{file.Name}' is not named as an inventory of its capture's {MomentName(moment)}, published by "
                + $"generation {manifest.Generation} or one before it, is.");
    }

    // The word a file's name gives a moment.
    private static string MomentName(ServiceInventoryMoment moment) => moment == ServiceInventoryMoment.Start ? "start" : "stop";

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
