using System.Security.AccessControl;
using System.Security.Principal;

namespace InterCat.Remote;

/// <summary>Whom a remote folder admits: the machine's administrators - an agent's - or one user - a controller's.</summary>
public enum RemoteFolderOwners
{
    Administrators,
    CurrentUser,
}

/// <summary>
/// A folder an agent or a controller keeps its key and records in (ADR-117): admitting SYSTEM and its owners alone - the
/// machine's administrators for an agent, the user for a controller - and inheriting nothing from its parent, so no other
/// account reads its key or writes its enrollments. A folder found otherwise is refused, not repaired: someone else could
/// have written what it holds.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class RemoteFolder
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier BuiltinAdministrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Whether this process runs with its administrators' rights: an elevated prompt, or the system itself.</summary>
    public static bool RunsAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User == LocalSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Creates <paramref name="path"/> for <paramref name="owners"/> where it does not exist - its parent must - and checks it
    /// where it does.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder admits others than its owners, or this process is not one.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder's parent does not exist.</exception>
    public static void Ensure(string path, RemoteFolderOwners owners) => Ensure(path, owners, RunsAsAdministrator());

    /// <summary>As <see cref="Ensure(string, RemoteFolderOwners)"/>, saying whether this process runs as an administrator.</summary>
    internal static void Ensure(string path, RemoteFolderOwners owners, bool asAdministrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (owners == RemoteFolderOwners.Administrators && !asAdministrator)
        {
            throw new UnauthorizedAccessException(
                "The remote agent is its host's administrators': run it from an elevated prompt.");
        }

        var folder = new DirectoryInfo(Path.GetFullPath(path));
        SecurityIdentifier owner = Owner(owners);
        if (!folder.Exists)
        {
            if (folder.Parent is not { Exists: true })
            {
                throw new DirectoryNotFoundException($"The folder that would hold {folder.FullName} does not exist.");
            }

            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier admitted in Admitted(owners))
            {
                security.AddAccessRule(new FileSystemAccessRule(admitted, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            folder.Create(security);
        }

        if (Problem(folder, owners) is { } problem)
        {
            throw new UnauthorizedAccessException($"{folder.FullName} {problem}; it is not used.");
        }
    }

    /// <summary>What is wrong with <paramref name="folder"/> for <paramref name="owners"/>, or null where nothing is.</summary>
    public static string? Problem(DirectoryInfo folder, RemoteFolderOwners owners)
    {
        ArgumentNullException.ThrowIfNull(folder);
        DirectorySecurity security = folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        HashSet<SecurityIdentifier> admitted = [.. Admitted(owners)];
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !(admitted.Contains(owner) || owner == BuiltinAdministrators))
        {
            return "is owned by another account than its owners";
        }

        if (!security.AreAccessRulesProtected)
        {
            return "inherits who may use it from its parent";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !admitted.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return "admits another account than its owners";
            }
        }

        return null;
    }

    private static SecurityIdentifier Owner(RemoteFolderOwners owners) =>
        owners == RemoteFolderOwners.Administrators ? BuiltinAdministrators : CurrentUser();

    private static SecurityIdentifier[] Admitted(RemoteFolderOwners owners) =>
        owners == RemoteFolderOwners.Administrators ? [LocalSystem, BuiltinAdministrators] : [LocalSystem, CurrentUser()];

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("This process runs as no user.");
    }
}
