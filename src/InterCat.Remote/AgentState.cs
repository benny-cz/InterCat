using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>
/// What a remote agent keeps between the times its administrator runs it (ADR-117): its key, its enrollments and its
/// audit, in a folder only SYSTEM and the machine's administrators may use - by default <c>remote-agent</c> in InterCat's
/// root under the machine's program data, which InterCat's installation makes. The key is kept protected for the
/// machine; a folder admitting anyone else is refused before anything in it is read.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AgentState
{
    /// <summary>The key's file.</summary>
    public const string IdentityFileName = "agent-identity-v1.bin";

    private AgentState(string root, RemoteFolderOwners owners, TimeProvider? time)
    {
        Root = root;
        Owners = owners;
        Enrollments = new AgentEnrollments(new FileEnrollmentStore(root), time);
        Audit = new FileRemoteAudit(root);
    }

    /// <summary>The agent's folder.</summary>
    public string Root { get; }

    /// <summary>Whom the folder admits.</summary>
    public RemoteFolderOwners Owners { get; }

    /// <summary>Its enrollments, kept in its folder and shared by every process using it.</summary>
    public AgentEnrollments Enrollments { get; }

    /// <summary>Its audit.</summary>
    public FileRemoteAudit Audit { get; }

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
    public RemoteIdentity Identity()
    {
        string path = Path.Combine(Root, IdentityFileName);
        ProtectionScope scope = Owners == RemoteFolderOwners.Administrators ? ProtectionScope.Machine : ProtectionScope.CurrentUser;
        if (File.Exists(path))
        {
            return RemoteIdentity.FromPkcs12(LocalProtection.Unprotect(File.ReadAllBytes(path), scope));
        }

        RemoteIdentity made = RemoteIdentity.Create("agent");
        byte[] pkcs12 = made.ExportPkcs12();
        try
        {
            string written = path + ".new";
            File.WriteAllBytes(written, LocalProtection.Protect(pkcs12, scope));
            File.Move(written, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return made;
    }
}
