using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>
/// M11's key rotation (`remote-agent-v1` §4.8, ADR-117): an enrolled key asks its agent for a rotation's secret, its
/// controller's next key enrolls with it - taking its grants - and the agent revokes the old key as it admits the next. The
/// secret is one-time and lapses, and admits no key but another than the one it rotates, while that one stands.
/// </summary>
public sealed class RemoteRotationTests
{
    private const RemoteGrants Granted = RemoteGrants.MetadataCapture | RemoteGrants.Download;

    [Fact(DisplayName = "§15: a controller's next key enrolls by its current key's rotation, taking its grants, and the agent revokes the current key as it admits the next, saying so in its audit")]
    public async Task ANextKeyReplacesTheCurrentOne()
    {
        await using var agent = AgentFixture.Start();
        using var current = RemoteIdentity.Create("controller");
        using var next = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection old = await EnrolledAsync(agent, current);

        RemoteRotation rotation = await old.RotateAsync(None);
        Assert.Equal(agent.Time.Now + AgentEnrollments.RotationLifetime, rotation.ExpiresUtc);
        await using RemoteAgentConnection fresh = await agent.ConnectAsync(next);
        RemoteEnrolled enrolled = await fresh.EnrollAsync(EnrollmentToken.ForRotation(agent.Identity.Pin, rotation.Secret), None);

        Assert.Equal((agent.Host.HostId, Granted), (enrolled.HostId, enrolled.Grants));
        Assert.Equal((Granted, (RemotePin?)current.Pin), (agent.Enrollments.Find(next.Pin)!.Grants, agent.Enrollments.Find(next.Pin)!.RotatedFrom));
        Assert.True(agent.Enrollments.IsRevoked(current.Pin));
        Assert.Null(agent.Enrollments.Find(current.Pin));
        Assert.Equal(RemoteErrorCode.Revoked, (await Assert.ThrowsAsync<RemoteAgentException>(() => old.InventoryAsync(None))).Code);
        _ = await fresh.InventoryAsync(None);

        Assert.Contains(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.Rotate && entry.Controller == current.Pin
            && entry.Outcome == "issued: its next key enrolls in its place within 10 minutes");
        Assert.Contains(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.Enroll && entry.Controller == next.Pin
            && entry.Outcome == $"admitted: metadata-capture,download; replaces {current.Pin.Abbreviated}, revoked");
    }

    [Fact(DisplayName = "§15: a rotation's secret enrolls one key once, within ten minutes, never the key it rotates, never after that key was revoked, and a key not enrolled has no rotation")]
    public async Task ARotationsSecretAdmitsOnlyWhatItShould()
    {
        await using var agent = AgentFixture.Start();
        using var current = RemoteIdentity.Create("controller");
        using var next = RemoteIdentity.Create("controller");
        using var another = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection old = await EnrolledAsync(agent, current);

        // Once: the next key enrolls, and another key presenting the same secret is refused.
        RemoteRotation rotation = await old.RotateAsync(None);
        await using (RemoteAgentConnection fresh = await agent.ConnectAsync(next))
        {
            _ = await fresh.EnrollAsync(EnrollmentToken.ForRotation(agent.Identity.Pin, rotation.Secret), None);
        }

        Assert.Equal(RemoteErrorCode.TokenRefused, await RefusedAsync(agent, another, rotation));

        // The key it rotates cannot enroll by it; it lapses after ten minutes; and the key it rotates, revoked meanwhile,
        // takes the rotation with it.
        using var second = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection secondOld = await EnrolledAsync(agent, second);
        RemoteRotation itself = await secondOld.RotateAsync(None);
        Assert.Equal(RemoteErrorCode.TokenRefused, (await Assert.ThrowsAsync<RemoteAgentException>(() =>
            secondOld.EnrollAsync(EnrollmentToken.ForRotation(agent.Identity.Pin, itself.Secret), None))).Code);
        Assert.False(agent.Enrollments.IsRevoked(second.Pin));

        // A refused enrollment ends its connection; the key asks again on a new one.
        await using RemoteAgentConnection secondAgain = await agent.ConnectAsync(second);
        RemoteRotation lapsing = await secondAgain.RotateAsync(None);
        agent.Time.Now += AgentEnrollments.RotationLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(RemoteErrorCode.TokenRefused, await RefusedAsync(agent, another, lapsing));

        RemoteRotation outlived = await secondAgain.RotateAsync(None);
        Assert.True(agent.Enrollments.Revoke(second.Pin));
        Assert.Equal(RemoteErrorCode.TokenRefused, await RefusedAsync(agent, another, outlived));
        Assert.Null(agent.Enrollments.Find(another.Pin));

        // A key not enrolled has nothing to rotate.
        using var stranger = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection asking = await agent.ConnectAsync(stranger);
        Assert.Equal(RemoteErrorCode.NotEnrolled, (await Assert.ThrowsAsync<RemoteAgentException>(() => asking.RotateAsync(None))).Code);
    }

    [Fact(DisplayName = "§15: an agent keeps a rotation and the key it replaced across the times it runs, in its enrollments' file")]
    public void ARotationIsKeptInTheAgentsFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "icat-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var current = RemoteIdentity.Create("controller");
            using var next = RemoteIdentity.Create("controller");
            using var agentKey = RemoteIdentity.Create("agent");
            var enrollments = new AgentEnrollments(new FileEnrollmentStore(folder));
            EnrollmentToken token = enrollments.Issue(agentKey.Pin, Granted);
            Assert.NotNull(enrollments.Admit(token.Secret, current.Pin));
            (byte[] secret, _) = enrollments.IssueRotation(current.Pin)!.Value;

            // Read again from the file, as an agent run later reads it: the rotation stands, and admits the next key.
            var later = new AgentEnrollments(new FileEnrollmentStore(folder));
            Assert.Equal(1, later.PendingTokens);
            EnrolledController admitted = later.Admit(secret, next.Pin)!;
            Assert.Equal((RemotePin?)current.Pin, admitted.RotatedFrom);
            var after = new AgentEnrollments(new FileEnrollmentStore(folder));
            Assert.Equal(((RemotePin?)current.Pin, true), (after.Find(next.Pin)!.RotatedFrom, after.IsRevoked(current.Pin)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<RemoteAgentConnection> EnrolledAsync(AgentFixture agent, RemoteIdentity controller)
    {
        RemoteAgentConnection connection = await agent.ConnectAsync(controller);
        _ = await connection.EnrollAsync(agent.Enrollments.Issue(agent.Identity.Pin, Granted), None);
        return connection;
    }

    /// <summary>The refusal <paramref name="controller"/> meets enrolling with <paramref name="rotation"/>'s secret.</summary>
    private static async Task<RemoteErrorCode?> RefusedAsync(AgentFixture agent, RemoteIdentity controller, RemoteRotation rotation)
    {
        await using RemoteAgentConnection connection = await agent.ConnectAsync(controller);
        return (await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.EnrollAsync(EnrollmentToken.ForRotation(agent.Identity.Pin, rotation.Secret), None))).Code;
    }
}
