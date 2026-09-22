using System.Runtime.Versioning;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// A real broker root provisioned beneath a temporary parent. The policy differs from production in
/// exactly two declared ways - the test process, not SYSTEM, is the broker principal, and the parent
/// is a user-owned temporary directory - so the boundary under test is the same code path production
/// runs, at whatever integrity the test process itself holds.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TemporaryBrokerRoot : IDisposable
{
    public const string RootName = "broker-root";

    private bool disposed;

    private TemporaryBrokerRoot(string parent, WindowsBrokerRoot root, BrokerRootSecurityPolicy policy, string userSid)
    {
        Parent = parent;
        Root = root;
        Policy = policy;
        UserSid = userSid;
    }

    public string Parent { get; }

    public WindowsBrokerRoot Root { get; }

    public BrokerRootSecurityPolicy Policy { get; }

    public string UserSid { get; }

    /// <summary>The integrity level a test root is labelled with: the test process's own, never above it.</summary>
    public static int CurrentIntegrityLevel
    {
        get
        {
            int observed = WindowsBrokerTokenIdentity.ReadCurrentProcess().IntegrityLevel;
            return observed >= BrokerIntegrityLevel.High
                ? BrokerIntegrityLevel.High
                : observed >= BrokerIntegrityLevel.Medium
                    ? BrokerIntegrityLevel.Medium
                    : BrokerIntegrityLevel.Low;
        }
    }

    public static string CurrentUserSid =>
        WindowsBrokerTokenIdentity.ReadCurrentProcess().UserSid;

    public static BrokerRootSecurityPolicy PolicyForCurrentProcess() => new()
    {
        BrokerPrincipalSids = [CurrentUserSid],
        TrustedOwnerSids =
        [
            CurrentUserSid,
            BrokerRootSecurityPolicy.AdministratorsSid,
            BrokerRootSecurityPolicy.LocalSystemSid,
        ],
        MandatoryIntegrityLevel = CurrentIntegrityLevel,
        RequireElevatedBroker = false,
        RequireTrustedParentOwner = false,
    };

    public static string CreateTemporaryParent()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            "InterCat.CaptureBroker.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        return parent;
    }

    public static TemporaryBrokerRoot Create(BrokerRootSecurityPolicy? policy = null, string rootName = RootName)
    {
        string parent = CreateTemporaryParent();
        try
        {
            BrokerRootSecurityPolicy effective = policy ?? PolicyForCurrentProcess();
            return new(parent, Provision(parent, effective, rootName), effective, CurrentUserSid);
        }
        catch
        {
            Delete(parent);
            throw;
        }
    }

    public static WindowsBrokerRoot Provision(
        string parent,
        BrokerRootSecurityPolicy policy,
        string rootName = RootName) =>
        WindowsBrokerRoot.Provision(new()
        {
            TrustedParentDirectory = parent,
            RootDirectoryName = rootName,
            CapturingUserSid = CurrentUserSid,
            Policy = policy,
        });

    /// <summary>Reopens the same root, as a restarted broker would.</summary>
    public WindowsBrokerRoot Reprovision(BrokerRootSecurityPolicy? policy = null) =>
        Provision(Parent, policy ?? Policy);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Root.Dispose();
        Delete(Parent);
    }

    private static void Delete(string parent)
    {
        try
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temporary directory must never fail the assertion that produced it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
