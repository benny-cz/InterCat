using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>What part a process played in collecting a capture (`contracts/collector-identities-v1.md` §2).</summary>
public enum CollectorRole
{
    /// <summary>The broker that owned the capture's trace session and wrote its journal.</summary>
    Broker = 1,

    /// <summary>The process whose authenticated request started the capture: the window or <c>icat capture</c>.</summary>
    Client = 2,

    /// <summary><c>icat record</c>, which owned its trace session itself.</summary>
    Recorder = 3,
}

/// <summary>One process that collected a capture, by its PID and the moment the operating system created it.</summary>
public sealed record CollectorProcessV1
{
    public required CollectorRole Role { get; init; }

    /// <summary>The process's ID while it collected, which the machine may give another process once it exits.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// When the operating system created the process, at its 100 ns resolution: the instant a lifecycle record of the same
    /// instance carries. Null when the process could not be opened to read it.
    /// </summary>
    public DateTimeOffset? CreatedUtc { get; init; }
}

/// <summary>
/// The processes that collected a live capture (`contracts/collector-identities-v1.md`), so a reader can label their own
/// activity in it (§19.5): evidence about the capture, which no journal holds and nothing can rebuild, kept with it like
/// its clock calibration.
/// </summary>
public sealed record CollectorIdentitiesV1
{
    public const string ContractName = "collector-identities-v1";
    public const int MaximumBytes = 4_096;
    public const int MaximumProcesses = 8;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public required string Contract { get; init; }

    public required Guid CaptureId { get; init; }

    /// <summary>The broker and its client, or the recorder: each process that collected the capture.</summary>
    public required IReadOnlyList<CollectorProcessV1> Processes { get; init; }

    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        return bytes.Length <= MaximumBytes
            ? bytes
            : throw new InvalidDataException($"The collector identities are {bytes.Length} bytes, beyond their {MaximumBytes}-byte bound.");
    }

    public static CollectorIdentitiesV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"A {ContractName} file is empty or exceeds its {MaximumBytes}-byte bound.");
        }

        CollectorIdentitiesV1 identities;
        try
        {
            identities = JsonSerializer.Deserialize<CollectorIdentitiesV1>(bytes, Json)
                ?? throw new InvalidDataException("A collector identities file has no object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The collector identities are not valid {ContractName} JSON.", exception);
        }

        identities.Validate();
        return identities;
    }

    /// <summary>
    /// The collectors a generation carries, their bytes checked against the digest its generation records; null for a
    /// generation that carries none, as an import's, a redacted package's and every capture's before revision 414 do.
    /// </summary>
    public static CollectorIdentitiesV1? Read(IOwnedDirectory directory, SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] files =
        [
            .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.CollectorIdentities),
        ];
        if (files.Length == 0)
        {
            return null;
        }

        if (files.Length != 1)
        {
            throw new InvalidDataException(
                $"Generation {manifest.Generation} carries {files.Length} collector identities files; one capture has one.");
        }

        StoreDependency file = files[0];
        if (file.LengthBytes is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException($"Collector identities '{file.Name}' are outside their {MaximumBytes}-byte bound.");
        }

        byte[] bytes = new byte[checked((int)file.LengthBytes)];
        using (FileStream stream = directory.OpenOwnedFile(
            file.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            if (stream.Length != file.LengthBytes)
            {
                throw new InvalidDataException(
                    $"Collector identities '{file.Name}' changed length after generation {manifest.Generation} was published.");
            }

            stream.ReadExactly(bytes);
        }

        string measured = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(measured, file.Digest, StringComparison.Ordinal)
            ? Decode(bytes)
            : throw new InvalidDataException(
                $"Collector identities '{file.Name}' changed after generation {manifest.Generation} was published.");
    }

    public void Validate()
    {
        string? problem = !string.Equals(Contract, ContractName, StringComparison.Ordinal)
                ? $"it must name contract '{ContractName}', not '{Contract}'"
            : CaptureId == Guid.Empty ? "it names no capture"
            : Processes is null || Processes.Count is < 1 or > MaximumProcesses
                ? $"it names no process, or more than {MaximumProcesses}"
            : Processes.Any(process => process is null || !Enum.IsDefined(process.Role) || process.ProcessId < 1)
                ? "a process has no role it knows, or no PID"
            : Processes.Select(process => (process.Role, process.ProcessId)).Distinct().Count() != Processes.Count
                ? "it names one role and PID twice"
            : null;
        if (problem is not null)
        {
            throw new InvalidDataException($"Collector identities are refused: {problem}.");
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
