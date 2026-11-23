using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>
/// M11's rotation of an agent's own key (`remote-agent-v1` §4.9, ADR-117): while its rotation is under way the agent tells
/// each enrolled controller its next key over the connection its current key secures, and keeps whom it told; a controller
/// accepts that key where it answers in the known key's place, and no other.
/// </summary>
public sealed class RemoteAgentKeyRotationTests
{
    [Fact(DisplayName = "§15: an agent rotating its own key tells each enrolled controller its next key - in its enrollment's answer and its hello - keeps whom it told, and tells a key not enrolled nothing")]
    public async Task AnAgentTellsItsNextKey()
    {
        using var next = RemoteIdentity.Create("agent");
        await using var agent = AgentFixture.Start(nextKey: next.Pin);
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.ConnectAsync(controller);
        RemoteEnrolled enrolled = await connection.EnrollAsync(agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.MetadataCapture), None);
        Assert.Equal((RemotePin?)next.Pin, enrolled.NextKey);
        Assert.Equal((RemotePin?)next.Pin, (await connection.HelloAsync(None)).NextKey);
        Assert.Equal((RemotePin?)next.Pin, agent.Enrollments.Find(controller.Pin)!.ToldNext);
        RemoteAuditEntry told = Assert.Single(agent.Audit.Entries, entry => entry.Outcome.Contains("told its next key", StringComparison.Ordinal));
        Assert.Equal((RemoteMessageType.Enroll, $"admitted: metadata-capture; told its next key, {next.Pin.Abbreviated}"), (told.Action, told.Outcome));

        using var stranger = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection asking = await agent.ConnectAsync(stranger);
        RemoteHello hello = await asking.HelloAsync(None);
        Assert.Equal((false, (RemotePin?)null), (hello.Enrolled, hello.NextKey));

        await using var steady = AgentFixture.Start();
        await using RemoteAgentConnection plain = await steady.ConnectAsync(controller);
        _ = await plain.EnrollAsync(steady.Enrollments.Issue(steady.Identity.Pin, RemoteGrants.MetadataCapture), None);
        Assert.Null((await plain.HelloAsync(None)).NextKey);
    }

    [Fact(DisplayName = "§15: a controller reaches an agent under the next key it was told, in its known key's place, and any other key answering there is refused")]
    public async Task AControllerFollowsAnAgentToItsNextKey()
    {
        var next = RemoteIdentity.Create("agent");
        using var controller = RemoteIdentity.Create("controller");
        AgentEnrollments enrollments;
        RemotePin current;
        await using (var agent = AgentFixture.Start(nextKey: next.Pin))
        {
            current = agent.Identity.Pin;
            enrollments = agent.Enrollments;
            await using RemoteAgentConnection connection = await agent.ConnectAsync(controller);
            _ = await connection.EnrollAsync(agent.Enrollments.Issue(current, RemoteGrants.MetadataCapture), None);
        }

        // Its administrator finished the rotation and started it again: it answers under its next key, its enrollments kept.
        await using var restarted = AgentFixture.Start(identity: next, enrollments: enrollments);
        await using RemoteAgentConnection followed = await RemoteAgentConnection.ConnectAsync(restarted.Endpoint, controller, current,
            TimeSpan.FromSeconds(10), None, next.Pin);
        Assert.Equal(next.Pin, followed.Agent);
        Assert.True((await followed.HelloAsync(None)).Enrolled);

        RemoteAgentException unexpected = await Assert.ThrowsAsync<RemoteAgentException>(() => RemoteAgentConnection.ConnectAsync(
            restarted.Endpoint, controller, current, TimeSpan.FromSeconds(10), None));
        Assert.Equal(next.Pin, unexpected.Presented);
        using var stranger = RemoteIdentity.Create("agent");
        RemoteAgentException notTold = await Assert.ThrowsAsync<RemoteAgentException>(() => RemoteAgentConnection.ConnectAsync(
            restarted.Endpoint, controller, current, TimeSpan.FromSeconds(10), None, stranger.Pin));
        Assert.Equal(next.Pin, notTold.Presented);
    }
}
