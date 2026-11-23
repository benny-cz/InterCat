using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>
/// A token issued and not yet consumed: its secret's hash, what it grants and when it lapses - and, for a key's rotation,
/// the enrolled key it rotates, which the key it enrolls replaces.
/// </summary>
public sealed record PendingToken(byte[] SecretHash, RemoteGrants Grants, DateTimeOffset ExpiresUtc, RemotePin? RotatesFrom = null);

/// <summary>Everything an agent knows of its enrollments at one moment.</summary>
public sealed record EnrollmentState(
    IReadOnlyList<PendingToken> Pending,
    IReadOnlyList<EnrolledController> Enrolled,
    IReadOnlyList<RemotePin> Revoked)
{
    public static EnrollmentState Empty { get; } = new([], [], []);
}

/// <summary>
/// Where an agent's enrollments live. Each change reads the state, changes it and keeps it as one step, so an
/// administrator issuing a token in one process and the agent admitting a controller in another never lose either's.
/// </summary>
public interface IEnrollmentStore
{
    EnrollmentState Read();

    T Change<T>(Func<EnrollmentState, (EnrollmentState Next, T Result)> change);
}

/// <summary>Enrollments held in memory, for one process.</summary>
public sealed class MemoryEnrollmentStore : IEnrollmentStore
{
    private readonly Lock gate = new();
    private EnrollmentState state = EnrollmentState.Empty;

    public EnrollmentState Read()
    {
        lock (gate)
        {
            return state;
        }
    }

    public T Change<T>(Func<EnrollmentState, (EnrollmentState Next, T Result)> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            (EnrollmentState next, T result) = change(state);
            state = next;
            return result;
        }
    }
}

/// <summary>
/// Enrollments kept in a file, <c>enrollments-v1.json</c>, in the agent's folder (`remote-agent-v1` §2): read and written
/// under a lock file every InterCat process takes alike, replaced whole, never edited in place. A file that does not read
/// as one InterCat wrote is refused, not reset: a reset would forget whom the administrator revoked.
/// </summary>
public sealed class FileEnrollmentStore : IEnrollmentStore
{
    public const string FileName = "enrollments-v1.json";
    private const string Contract = "remote-agent-enrollments-v1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string path;
    private readonly string lockPath;

    public FileEnrollmentStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        path = Path.Combine(folder, FileName);
        lockPath = Path.Combine(folder, "enrollments-v1.lock");
    }

    public EnrollmentState Read()
    {
        using FileStream held = AgentFiles.Hold(lockPath);
        return Load();
    }

    public T Change<T>(Func<EnrollmentState, (EnrollmentState Next, T Result)> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using FileStream held = AgentFiles.Hold(lockPath);
        (EnrollmentState next, T result) = change(Load());
        Save(next);
        return result;
    }

    private EnrollmentState Load()
    {
        if (!File.Exists(path))
        {
            return EnrollmentState.Empty;
        }

        try
        {
            JsonObject root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
                ?? throw new InvalidDataException("it is not a JSON object");
            if ((string?)root["contract"] != Contract)
            {
                throw new InvalidDataException($"it is not {Contract}");
            }

            PendingToken[] pending = [.. Items(root, "pending").Select(item => new PendingToken(
                Convert.FromHexString(Text(item, "secretSha256")),
                Grants(item),
                DateTimeOffset.Parse(Text(item, "expiresUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Optional(item, "rotatesFrom")))];
            EnrolledController[] enrolled = [.. Items(root, "enrolled").Select(item => new EnrolledController(
                Pin(Text(item, "pin")),
                Grants(item),
                DateTimeOffset.Parse(Text(item, "enrolledUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Optional(item, "rotatedFrom"),
                Optional(item, "toldNext")))];
            RemotePin[] revoked = [.. (root["revoked"] as JsonArray ?? throw new InvalidDataException("it lists no revoked keys"))
                .Select(item => Pin((string?)item ?? string.Empty))];
            return new(pending, enrolled, revoked);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"The agent's enrollments at {path} do not read as InterCat wrote them ({exception.Message}); they are not "
                + "reset, so no revocation is forgotten. Its administrator restores or removes the file.", exception);
        }
    }

    private void Save(EnrollmentState state)
    {
        var root = new JsonObject
        {
            ["contract"] = Contract,
            ["pending"] = new JsonArray([.. state.Pending.Select(token => (JsonNode)new JsonObject
            {
                ["secretSha256"] = Convert.ToHexStringLower(token.SecretHash),
                ["grants"] = new JsonArray([.. token.Grants.Names().Select(name => (JsonNode)name)]),
                ["expiresUtc"] = token.ExpiresUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["rotatesFrom"] = token.RotatesFrom?.Hex,
            })]),
            ["enrolled"] = new JsonArray([.. state.Enrolled.Select(controller => (JsonNode)new JsonObject
            {
                ["pin"] = controller.Pin.Hex,
                ["grants"] = new JsonArray([.. controller.Grants.Names().Select(name => (JsonNode)name)]),
                ["enrolledUtc"] = controller.EnrolledUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["rotatedFrom"] = controller.RotatedFrom?.Hex,
                ["toldNext"] = controller.ToldNext?.Hex,
            })]),
            ["revoked"] = new JsonArray([.. state.Revoked.Select(pin => (JsonNode)pin.Hex)]),
        };

        AgentFiles.Replace(path, root.ToJsonString(Indented));
    }

    private static IEnumerable<JsonObject> Items(JsonObject root, string name) =>
        (root[name] as JsonArray ?? throw new InvalidDataException($"it lists no {name}"))
            .Select(item => item as JsonObject ?? throw new InvalidDataException($"an entry of {name} is not an object"));

    private static string Text(JsonObject item, string name) =>
        (string?)item[name] ?? throw new InvalidDataException($"an entry has no {name}");

    private static RemoteGrants Grants(JsonObject item) =>
        RemoteGrantsExtensions.TryParse(
            (item["grants"] as JsonArray ?? throw new InvalidDataException("an entry has no grants")).Select(name => (string?)name ?? string.Empty),
            out RemoteGrants grants)
            ? grants
            : throw new InvalidDataException("an entry grants what version 1 does not define");

    private static RemotePin Pin(string text) =>
        RemotePin.TryParse(text, out RemotePin pin) ? pin : throw new InvalidDataException($"'{text}' is not a pin");

    /// <summary>A pin an entry may name, since a key's rotation: absent, or null, where it names none.</summary>
    private static RemotePin? Optional(JsonObject item, string name) => (string?)item[name] is { } text ? Pin(text) : null;
}
