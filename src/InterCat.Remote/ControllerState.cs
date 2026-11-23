using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>An agent a controller enrolled with: where it was reached, its key, its host, and what it granted.</summary>
public sealed record KnownAgent(IPEndPoint Endpoint, RemotePin Pin, Guid HostId, string MachineName, RemoteGrants Grants, DateTimeOffset EnrolledUtc);

/// <summary>
/// What a controller keeps (ADR-117): its key, protected for its user, and the agents it enrolled with, in a folder only
/// SYSTEM and its user may use - by default <c>remote</c> in InterCat's folder under the user's local application data.
/// It reaches an agent only where it was enrolled, and only to the key it enrolled with.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ControllerState
{
    public const string IdentityFileName = "controller-identity-v1.bin";
    public const string AgentsFileName = "agents-v1.json";
    private const string AgentsContract = "remote-controller-agents-v1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private ControllerState(string root) => Root = root;

    /// <summary>The controller's folder.</summary>
    public string Root { get; }

    /// <summary>The controller's folder unless its user names another: <c>%LOCALAPPDATA%\InterCat\remote</c>.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterCat", "remote");

    /// <summary>Opens the controller's folder at <paramref name="root"/>, making it and its parents where they do not exist.</summary>
    /// <exception cref="UnauthorizedAccessException">The folder admits another account than its user.</exception>
    public static ControllerState Open(string root)
    {
        string full = Path.GetFullPath(root);
        if (Path.GetDirectoryName(full) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        RemoteFolder.Ensure(full, RemoteFolderOwners.CurrentUser);
        return new(full);
    }

    /// <summary>The controller's key: the one it keeps, or a new one kept from now on where it has none.</summary>
    /// <exception cref="CryptographicException">The kept key is not this user's, or was altered.</exception>
    public RemoteIdentity Identity()
    {
        string path = Path.Combine(Root, IdentityFileName);
        if (File.Exists(path))
        {
            return RemoteIdentity.FromPkcs12(LocalProtection.Unprotect(File.ReadAllBytes(path), ProtectionScope.CurrentUser));
        }

        RemoteIdentity made = RemoteIdentity.Create("controller");
        byte[] pkcs12 = made.ExportPkcs12();
        try
        {
            string written = path + ".new";
            File.WriteAllBytes(written, LocalProtection.Protect(pkcs12, ProtectionScope.CurrentUser));
            File.Move(written, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return made;
    }

    /// <summary>The agents enrolled with, in the order they were.</summary>
    /// <exception cref="InvalidDataException">The list does not read as InterCat wrote it.</exception>
    public IReadOnlyList<KnownAgent> Agents()
    {
        string path = Path.Combine(Root, AgentsFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            JsonObject root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException("it is not a JSON object");
            if ((string?)root["contract"] != AgentsContract || root["agents"] is not JsonArray agents)
            {
                throw new InvalidDataException($"it is not {AgentsContract}");
            }

            return [.. agents.Select(node => node as JsonObject ?? throw new InvalidDataException("an agent is not an object")).Select(item => new KnownAgent(
                IPEndPoint.Parse((string?)item["endpoint"] ?? throw new InvalidDataException("an agent has no endpoint")),
                RemotePin.TryParse((string?)item["pin"], out RemotePin pin) ? pin : throw new InvalidDataException("an agent's pin does not read"),
                Guid.Parse((string?)item["hostId"] ?? throw new InvalidDataException("an agent has no host")),
                (string?)item["machineName"] ?? string.Empty,
                RemoteGrantsExtensions.TryParse(((item["grants"] as JsonArray) ?? []).Select(name => (string?)name ?? string.Empty), out RemoteGrants grants)
                    ? grants : throw new InvalidDataException("an agent's grants do not read"),
                DateTimeOffset.Parse((string?)item["enrolledUtc"] ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidDataException($"The agents this controller enrolled with, at {path}, do not read as InterCat wrote them.", exception);
        }
    }

    /// <summary>Keeps <paramref name="agent"/>, replacing what was kept of the same key.</summary>
    public void Remember(KnownAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        KnownAgent[] agents = [.. Agents().Where(known => known.Pin != agent.Pin), agent];
        var root = new JsonObject
        {
            ["contract"] = AgentsContract,
            ["agents"] = new JsonArray([.. agents.Select(known => (JsonNode)new JsonObject
            {
                ["endpoint"] = known.Endpoint.ToString(),
                ["pin"] = known.Pin.Hex,
                ["hostId"] = known.HostId.ToString("D"),
                ["machineName"] = known.MachineName,
                ["grants"] = new JsonArray([.. known.Grants.Names().Select(name => (JsonNode)name)]),
                ["enrolledUtc"] = known.EnrolledUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            })]),
        };
        string path = Path.Combine(Root, AgentsFileName);
        string written = path + ".new";
        File.WriteAllText(written, root.ToJsonString(Indented));
        File.Move(written, path, overwrite: true);
    }
}
