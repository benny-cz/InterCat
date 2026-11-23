using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>
/// What a remote agent keeps between the times its administrator runs it (ADR-117): its key, its enrollments, its audit
/// and the captures it started, in a folder only SYSTEM and the machine's administrators may use - by default <c>remote-agent</c> in InterCat's
/// root under the machine's program data, which InterCat's installation makes. The key is kept protected for the
/// machine; a folder admitting anyone else is refused before anything in it is read.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AgentState
{
    /// <summary>The key's file.</summary>
    public const string IdentityFileName = "agent-identity-v1.bin";

    /// <summary>The agent's next key while its rotation is under way (`remote-agent-v1` §4.9), kept as its key is.</summary>
    public const string NextIdentityFileName = "agent-identity-next-v1.bin";

    private AgentState(string root, RemoteFolderOwners owners, TimeProvider? time)
    {
        Root = root;
        Owners = owners;
        Enrollments = new AgentEnrollments(new FileEnrollmentStore(root), time);
        Audit = new FileRemoteAudit(root);
        Captures = new FileCaptureRecords(root);
    }

    /// <summary>The agent's folder.</summary>
    public string Root { get; }

    /// <summary>Whom the folder admits.</summary>
    public RemoteFolderOwners Owners { get; }

    /// <summary>Its enrollments, kept in its folder and shared by every process using it.</summary>
    public AgentEnrollments Enrollments { get; }

    /// <summary>Its audit.</summary>
    public FileRemoteAudit Audit { get; }

    /// <summary>The captures it started, with where each one's evidence is, so it serves them after it restarts.</summary>
    public FileCaptureRecords Captures { get; }

    /// <summary>The agent's folder unless its administrator names another: <c>%ProgramData%\InterCat\remote-agent</c>.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InterCat", "remote-agent");

    /// <summary>
    /// Opens the agent's folder at <paramref name="root"/>, making it where it does not exist - its parent must - for
    /// <paramref name="owners"/>: the machine's administrators for an agent, the user only where a test runs one.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder admits others, or this process is not its owner.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder's parent does not exist.</exception>
    public static AgentState Open(string root, RemoteFolderOwners owners = RemoteFolderOwners.Administrators, TimeProvider? time = null)
    {
        string full = Path.GetFullPath(root);
        RemoteFolder.Ensure(full, owners);
        return new(full, owners, time);
    }

    /// <summary>The agent's key: the one it keeps, or a new one kept from now on where it has none.</summary>
    /// <exception cref="CryptographicException">The kept key is not this machine's, or was altered.</exception>
    public RemoteIdentity Identity() => Kept(IdentityFileName) ?? Keep(RemoteIdentity.Create("agent"), IdentityFileName);

    /// <summary>The key a rotation under way makes the agent's when it is finished; null where none is under way.</summary>
    public RemoteIdentity? NextIdentity() => Kept(NextIdentityFileName);

    /// <summary>Begins the agent's own key's rotation, or goes on with one under way: its next key, made and kept where there is none.</summary>
    public RemoteIdentity BeginRotation() => NextIdentity() ?? Keep(RemoteIdentity.Create("agent"), NextIdentityFileName);

    /// <summary>
    /// Finishes the rotation: the next key becomes the agent's, and the old one is gone. An agent serving now goes on with
    /// the key it started with until it is started again.
    /// </summary>
    public RemoteIdentity FinishRotation()
    {
        string next = Path.Combine(Root, NextIdentityFileName);
        if (!File.Exists(next))
        {
            throw new InvalidOperationException("No rotation of this agent's key is under way: icat agent rotate begins one.");
        }

        File.Move(next, Path.Combine(Root, IdentityFileName), overwrite: true);
        return Identity();
    }

    private ProtectionScope Scope => Owners == RemoteFolderOwners.Administrators ? ProtectionScope.Machine : ProtectionScope.CurrentUser;

    private RemoteIdentity? Kept(string name)
    {
        string path = Path.Combine(Root, name);
        return File.Exists(path) ? RemoteIdentity.FromPkcs12(LocalProtection.Unprotect(File.ReadAllBytes(path), Scope)) : null;
    }

    private RemoteIdentity Keep(RemoteIdentity made, string name)
    {
        string path = Path.Combine(Root, name);
        byte[] pkcs12 = made.ExportPkcs12();
        try
        {
            string written = path + ".new";
            File.WriteAllBytes(written, LocalProtection.Protect(pkcs12, Scope));
            File.Move(written, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return made;
    }
}
