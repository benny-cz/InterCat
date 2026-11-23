using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>
/// An agent a controller enrolled with: where it was reached, its key, its host, what it granted, the controller's key it
/// knows - null for the controller's current key, as every agent was before a rotation - and the agent's own next key it
/// told this controller of while its rotation is under way (`remote-agent-v1` §4.9).
/// </summary>
public sealed record KnownAgent(IPEndPoint Endpoint, RemotePin Pin, Guid HostId, string MachineName, RemoteGrants Grants, DateTimeOffset EnrolledUtc,
    RemotePin? Controller = null, RemotePin? NextPin = null);

/// <summary>
/// What a controller keeps (ADR-117): its key, protected for its user, and the agents it enrolled with, in a folder only
/// SYSTEM and its user may use - by default <c>remote</c> in InterCat's folder under the user's local application data.
/// It reaches an agent only where it was enrolled, and only to the key it enrolled with.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ControllerState
{
    public const string IdentityFileName = "controller-identity-v1.bin";

    /// <summary>The controller's next key while a rotation is under way, kept as its key is (`remote-agent-v1` §4.8).</summary>
    public const string NextIdentityFileName = "controller-identity-next-v1.bin";
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
        Keep(made, IdentityFileName, overwrite: false);
        return made;
    }

    /// <summary>Keeps <paramref name="identity"/> protected for this user as <paramref name="name"/> in the controller's folder.</summary>
    private void Keep(RemoteIdentity identity, string name, bool overwrite)
    {
        string path = Path.Combine(Root, name);
        byte[] pkcs12 = identity.ExportPkcs12();
        try
        {
            string written = path + ".new";
            File.WriteAllBytes(written, LocalProtection.Protect(pkcs12, ProtectionScope.CurrentUser));
            File.Move(written, path, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    /// <summary>The key a rotation under way enrolls in the current one's place; null where none is under way.</summary>
    /// <exception cref="CryptographicException">The kept key is not this user's, or was altered.</exception>
    public RemoteIdentity? NextIdentity()
    {
        string path = Path.Combine(Root, NextIdentityFileName);
        return File.Exists(path)
            ? RemoteIdentity.FromPkcs12(LocalProtection.Unprotect(File.ReadAllBytes(path), ProtectionScope.CurrentUser))
            : null;
    }

    /// <summary>Begins a rotation, or goes on with one under way: its next key, made and kept where there is none yet.</summary>
    public RemoteIdentity BeginRotation()
    {
        if (NextIdentity() is { } next)
        {
            return next;
        }

        RemoteIdentity made = RemoteIdentity.Create("controller");
        Keep(made, NextIdentityFileName, overwrite: false);
        return made;
    }

    /// <summary>
    /// Ends a rotation once every agent knows the next key: it becomes this controller's key, and the old one is gone. Refused
    /// while an agent still knows the old one, which this controller would no longer reach.
    /// </summary>
    public RemoteIdentity FinishRotation()
    {
        using RemoteIdentity next = NextIdentity() ?? throw new InvalidOperationException("No rotation of this controller's key is under way.");
        if (Agents().FirstOrDefault(agent => agent.Controller != next.Pin) is { } behind)
        {
            throw new InvalidOperationException($"The agent at {behind.Endpoint} still knows this controller's old key; rotate it first.");
        }

        string current = Path.Combine(Root, IdentityFileName);
        File.Move(Path.Combine(Root, NextIdentityFileName), current, overwrite: true);
        return Identity();
    }

    /// <summary>
    /// The key <paramref name="agent"/> knows this controller by: its current key, or the next one where a rotation moved
    /// that agent to it.
    /// </summary>
    /// <exception cref="InvalidDataException">This controller keeps no key that agent knows.</exception>
    public RemoteIdentity IdentityFor(KnownAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        RemoteIdentity current = Identity();
        if (agent.Controller is not { } known || known == current.Pin)
        {
            return current;
        }

        current.Dispose();
        return NextIdentity() is { } next && next.Pin == known
            ? next
            : throw new InvalidDataException($"This controller keeps no key the agent at {agent.Endpoint} knows it by.");
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
                DateTimeOffset.Parse((string?)item["enrolledUtc"] ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (string?)item["controller"] is { } controller
                    ? RemotePin.TryParse(controller, out RemotePin knows) ? knows : throw new InvalidDataException("an agent's controller key does not read")
                    : null,
                (string?)item["nextPin"] is { } told
                    ? RemotePin.TryParse(told, out RemotePin next) ? next : throw new InvalidDataException("an agent's next key does not read")
                    : null))];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidDataException($"The agents this controller enrolled with, at {path}, do not read as InterCat wrote them.", exception);
        }
    }

    /// <summary>
    /// Takes <paramref name="next"/> as <paramref name="agent"/>'s key, where it stands in the list: the next key it told this
    /// controller of answered in its place, so its rotation is finished (`remote-agent-v1` §4.9).
    /// </summary>
    public void Promote(KnownAgent agent, RemotePin next)
    {
        ArgumentNullException.ThrowIfNull(agent);
        List<KnownAgent> agents = [.. Agents()];
        int at = agents.FindIndex(known => known.Pin == agent.Pin);
        if (at < 0 || agents[at].NextPin != next)
        {
            throw new InvalidOperationException("Only the next key an agent told this controller of becomes its key here.");
        }

        agents[at] = agents[at] with { Pin = next, NextPin = null };
        Write(agents);
    }

    /// <summary>Keeps <paramref name="agent"/>, replacing what was kept of the same key where it stands in the list.</summary>
    public void Remember(KnownAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        List<KnownAgent> agents = [.. Agents()];
        int at = agents.FindIndex(known => known.Pin == agent.Pin);
        if (at >= 0)
        {
            agents[at] = agent;
        }
        else
        {
            agents.Add(agent);
        }

        Write(agents);
    }

    private void Write(List<KnownAgent> agents)
    {
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
                ["controller"] = known.Controller?.Hex,
                ["nextPin"] = known.NextPin?.Hex,
            })]),
        };
        string path = Path.Combine(Root, AgentsFileName);
        string written = path + ".new";
        File.WriteAllText(written, root.ToJsonString(Indented));
        File.Move(written, path, overwrite: true);
    }
}
