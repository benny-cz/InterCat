using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace InterCat.Remote.Tests;

/// <summary>
/// What an agent and a controller keep between runs (ADR-117): a key protected at rest, enrollments every process shares,
/// an audit naming keys alone, each in a folder only its owners may use. A test's folders are its user's own, under the
/// run's temporary folder, and go with the test.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AgentStateTests
{
    [Fact(DisplayName = "§15: an agent keeps its key, enrollments and audit across the times it runs - a token one process issues another admits, a revocation reaches the serving agent, and its key stays its own")]
    public async Task AnAgentKeepsWhatItNeedsAcrossRuns()
    {
        using var folder = new TemporaryFolder();
        AgentState issuing = AgentState.Open(folder.Path, RemoteFolderOwners.CurrentUser);
        AgentState serving = AgentState.Open(folder.Path, RemoteFolderOwners.CurrentUser);
        Assert.Null(RemoteFolder.Problem(new DirectoryInfo(folder.Path), RemoteFolderOwners.CurrentUser));

        using RemoteIdentity first = issuing.Identity();
        using RemoteIdentity again = serving.Identity();
        Assert.Equal(first.Pin, again.Pin);

        // The key is kept protected: the file reads as no certificate, and as this one only through the protection.
        byte[] kept = File.ReadAllBytes(Path.Combine(folder.Path, AgentState.IdentityFileName));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(kept, password: null).Dispose());
        Assert.Equal(first.ExportPkcs12(), LocalProtection.Unprotect(kept, ProtectionScope.CurrentUser));

        // The administrator issues a token in one process; the agent, running in another, admits it.
        EnrollmentToken token = issuing.Enrollments.Issue(first.Pin, RemoteGrants.Download);
        var host = new RemoteAgentHost(Guid.NewGuid(), "kept-host", "Windows (test)", "1.0.0-test");
        var server = new RemoteAgentServer(again, serving.Enrollments, host, audit: serving.Audit);
        await using (server)
        {
            IPEndPoint endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using var controller = RemoteIdentity.Create("controller");
            await using RemoteAgentConnection connection = await RemoteAgentConnection.ConnectAsync(endpoint, controller, first.Pin,
                TimeSpan.FromSeconds(10), CancellationToken.None);
            _ = await connection.EnrollAsync(token, CancellationToken.None);
            Assert.Equal([controller.Pin], issuing.Enrollments.Controllers.Select(entry => entry.Pin));

            // Revoked from the administrator's process: the serving agent refuses the next request.
            Assert.True(issuing.Enrollments.Revoke(controller.Pin));
            RemoteAgentException revoked = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.InventoryAsync(CancellationToken.None));
            Assert.Equal(RemoteErrorCode.Revoked, revoked.Code);
        }

        // The audit outlives the agent, by key and never by address.
        IReadOnlyList<RemoteAuditEntry> audit = FileRemoteAudit.Read(folder.Path, 10);
        Assert.Equal([(RemoteMessageType.Enroll, "admitted: download"), (RemoteMessageType.Inventory, "refused: revoked")],
            audit.Select(entry => (entry.Action, entry.Outcome)));
        Assert.DoesNotContain("127.0.0.1", File.ReadAllText(Path.Combine(folder.Path, FileRemoteAudit.FileName)), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§15: an agent's enrollments that do not read as InterCat wrote them are refused, not reset, so no revocation is forgotten")]
    public void DamagedEnrollmentsAreRefusedNotReset()
    {
        using var folder = new TemporaryFolder();
        AgentState agent = AgentState.Open(folder.Path, RemoteFolderOwners.CurrentUser);
        using RemoteIdentity identity = agent.Identity();
        using RemoteIdentity controller = RemoteIdentity.Create("controller");
        _ = agent.Enrollments.Revoke(controller.Pin);
        string path = Path.Combine(folder.Path, FileEnrollmentStore.FileName);
        string damaged = File.ReadAllText(path)[..^20];
        File.WriteAllText(path, damaged);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => agent.Enrollments.Revoked);
        Assert.Contains("not reset", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => agent.Enrollments.Issue(identity.Pin, RemoteGrants.Download));
        Assert.Equal(damaged, File.ReadAllText(path));
    }

    [Fact(DisplayName = "§15: a folder an agent or a controller keeps its key in admits its owners alone - one that also admits another account, or inherits who may use it, is refused before anything in it is read")]
    public void AFolderAdmittingOthersIsRefused()
    {
        using var folder = new TemporaryFolder();
        _ = AgentState.Open(folder.Path, RemoteFolderOwners.CurrentUser);
        var directory = new DirectoryInfo(folder.Path);
        DirectorySecurity security = directory.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        directory.SetAccessControl(security);
        UnauthorizedAccessException refused = Assert.Throws<UnauthorizedAccessException>(() =>
            AgentState.Open(folder.Path, RemoteFolderOwners.CurrentUser));
        Assert.Contains("admits another account", refused.Message, StringComparison.Ordinal);
        Assert.Throws<UnauthorizedAccessException>(() => ControllerState.Open(folder.Path));

        // An agent's folder is its host's administrators': a process that is not one makes none and is refused.
        string administrators = folder.Path + "-administrators";
        UnauthorizedAccessException unelevated = Assert.Throws<UnauthorizedAccessException>(() =>
            RemoteFolder.Ensure(administrators, RemoteFolderOwners.Administrators, asAdministrator: false));
        Assert.Contains("elevated prompt", unelevated.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(administrators));

        // A folder made without its own protection, inheriting its parent's, is refused alike.
        string plain = Path.Combine(folder.Path, "..", Path.GetFileName(folder.Path) + "-plain");
        Directory.CreateDirectory(plain);
        try
        {
            UnauthorizedAccessException inherits = Assert.Throws<UnauthorizedAccessException>(() =>
                AgentState.Open(plain, RemoteFolderOwners.CurrentUser));
            Assert.Contains("inherits", inherits.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact(DisplayName = "§15: a controller keeps its key and the agents it enrolled with, each agent once by its key")]
    public void AControllerKeepsItsAgents()
    {
        using var folder = new TemporaryFolder();
        ControllerState state = ControllerState.Open(folder.Path);
        using RemoteIdentity first = state.Identity();
        using RemoteIdentity again = ControllerState.Open(folder.Path).Identity();
        Assert.Equal(first.Pin, again.Pin);

        using var agent = RemoteIdentity.Create("agent");
        var when = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        var known = new KnownAgent(IPEndPoint.Parse("192.0.2.10:7457"), agent.Pin, Guid.NewGuid(), "far-host", RemoteGrants.MetadataCapture, when);
        state.Remember(known);
        state.Remember(known with { Grants = RemoteGrants.MetadataCapture | RemoteGrants.Download });
        KnownAgent kept = Assert.Single(ControllerState.Open(folder.Path).Agents());
        Assert.Equal(known with { Grants = RemoteGrants.MetadataCapture | RemoteGrants.Download }, kept);
    }

    /// <summary>A folder of the run's temporary folder that goes with the test, made by the code under test.</summary>
    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "icat-remote-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
