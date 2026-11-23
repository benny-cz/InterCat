using System.Buffers.Binary;
using System.Net.Security;
using Xunit;

namespace InterCat.Remote.Tests;

/// <summary>
/// M11's first part (ADR-117, `remote-agent-v1`): a controller enrolls with an agent by a token its administrator issued,
/// over TLS in which each side knows the other by its key's pin, and is then known by that key alone, within the token's
/// grants and the agent's own policy. Everything runs on loopback, each party's key made for the test and installed in no
/// store.
/// </summary>
public sealed class RemoteEnrollmentTests
{
    [Fact(DisplayName = "§15: a controller enrolls with an agent's token over TLS pinned to each side's key, and is then known by its key alone, holding the token's grants within the agent's policy, its actions audited by pin with no address")]
    public async Task AControllerEnrollsAndIsKnownByItsKey()
    {
        await using AgentFixture agent = AgentFixture.Start();
        using var controller = RemoteIdentity.Create("controller");
        EnrollmentToken token = agent.Enrollments.Issue(agent.Identity.Pin,
            RemoteGrants.MetadataCapture | RemoteGrants.Contents | RemoteGrants.Download);

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(controller))
        {
            RemoteHello stranger = await connection.HelloAsync(CancellationToken.None);
            Assert.Equal((RemoteFrameCodec.Major, false, (Guid?)null, RemoteGrants.None),
                (stranger.Major, stranger.Enrolled, stranger.HostId, stranger.Grants));

            // Contents were granted, but the agent's own policy allows them only where its administrator says so.
            RemoteEnrolled enrolled = await connection.EnrollAsync(token, CancellationToken.None);
            Assert.Equal(new RemoteEnrolled(agent.Host.HostId, RemoteGrants.MetadataCapture | RemoteGrants.Download), enrolled);

            RemoteInventory inventory = await connection.InventoryAsync(CancellationToken.None);
            Assert.Equal(new RemoteInventory(agent.Host.HostId, "test-host", "Windows (test)", "1.0.0-test", ServesCapture: false), inventory);
        }

        // Known by its key on a connection of its own, the token spent.
        await using (RemoteAgentConnection again = await agent.ConnectAsync(controller))
        {
            RemoteHello known = await again.HelloAsync(CancellationToken.None);
            Assert.Equal((true, (Guid?)agent.Host.HostId, RemoteGrants.MetadataCapture | RemoteGrants.Download),
                (known.Enrolled, known.HostId, known.Grants));
        }

        Assert.Equal(0, agent.Enrollments.PendingTokens);
        EnrolledController admitted = Assert.Single(agent.Enrollments.Controllers);
        Assert.Equal((controller.Pin, RemoteGrants.MetadataCapture | RemoteGrants.Contents | RemoteGrants.Download),
            (admitted.Pin, admitted.Grants));

        // Each request audited by the controller's pin, in order, and no entry names an address or a port.
        Assert.Equal(
            [
                (RemoteMessageType.Hello, "answered: not enrolled"),
                (RemoteMessageType.Enroll, "admitted: metadata-capture,contents,download"),
                (RemoteMessageType.Inventory, "answered"),
                (RemoteMessageType.Hello, "answered"),
            ],
            agent.Audit.Entries.Select(entry => (entry.Action, entry.Outcome)));
        Assert.All(agent.Audit.Entries, entry => Assert.Equal(controller.Pin, entry.Controller));
        Assert.DoesNotContain(agent.Audit.Entries, entry => entry.Outcome.Contains("127.0.0.1", StringComparison.Ordinal)
            || entry.Outcome.Contains(agent.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§15: a token enrolls one controller once and lapses after its lifetime - a second use, an expired token and a secret never issued are refused alike, and a refused enrollment ends the connection")]
    public async Task ATokenEnrollsOnceAndLapses()
    {
        await using AgentFixture agent = AgentFixture.Start();
        using var first = RemoteIdentity.Create("controller");
        using var second = RemoteIdentity.Create("controller");
        EnrollmentToken token = agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.MetadataCapture);

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(first))
        {
            _ = await connection.EnrollAsync(token, CancellationToken.None);
        }

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(second))
        {
            RemoteAgentException used = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.EnrollAsync(token, CancellationToken.None));
            Assert.Equal(RemoteErrorCode.TokenRefused, used.Code);

            // The agent ended the connection after refusing: the next request finds it closed.
            _ = await Assert.ThrowsAnyAsync<Exception>(() => connection.HelloAsync(CancellationToken.None));
        }

        EnrollmentToken lapsing = agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.Download, TimeSpan.FromMinutes(1));
        agent.Time.Now += TimeSpan.FromMinutes(1);
        await using (RemoteAgentConnection connection = await agent.ConnectAsync(second))
        {
            RemoteAgentException expired = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.EnrollAsync(lapsing, CancellationToken.None));
            Assert.Equal(RemoteErrorCode.TokenRefused, expired.Code);
        }

        EnrollmentToken invented = EnrollmentToken.Create(agent.Identity.Pin);
        await using (RemoteAgentConnection connection = await agent.ConnectAsync(second))
        {
            RemoteAgentException unknown = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.EnrollAsync(invented, CancellationToken.None));
            Assert.Equal(RemoteErrorCode.TokenRefused, unknown.Code);
        }

        Assert.Equal([first.Pin], agent.Enrollments.Controllers.Select(entry => entry.Pin));
        Assert.Throws<ArgumentOutOfRangeException>(() => agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.Download, TimeSpan.FromMinutes(61)));
        Assert.Throws<ArgumentException>(() => agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.None));
    }

    [Fact(DisplayName = "§15: a controller that reaches another agent than its token names stops in the handshake, so the token's secret never leaves it and the token stays unused")]
    public async Task AControllerReachingAnotherAgentSendsNothing()
    {
        await using AgentFixture intended = AgentFixture.Start();
        await using AgentFixture impostor = AgentFixture.Start();
        using var controller = RemoteIdentity.Create("controller");
        EnrollmentToken token = intended.Enrollments.Issue(intended.Identity.Pin, RemoteGrants.MetadataCapture);

        // The person gave the controller the other agent's address with the intended agent's token.
        RemoteAgentException wrong = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            RemoteAgentConnection.ConnectAsync(impostor.Endpoint, controller, token.Agent, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Null(wrong.Code);
        Assert.Contains(impostor.Identity.Pin.Abbreviated, wrong.Message, StringComparison.Ordinal);

        Assert.Equal(1, intended.Enrollments.PendingTokens);
        Assert.Empty(impostor.Audit.Entries);
        Assert.Empty(impostor.Enrollments.Controllers);
    }

    [Fact(DisplayName = "§15: a key not enrolled is refused all but hello and enrollment, and a revoked key everything - a new token included - while hello tells it nothing of the host")]
    public async Task AnUnenrolledOrRevokedKeyIsRefused()
    {
        await using AgentFixture agent = AgentFixture.Start();
        using var controller = RemoteIdentity.Create("controller");

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(controller))
        {
            RemoteAgentException stranger = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.InventoryAsync(CancellationToken.None));
            Assert.Equal(RemoteErrorCode.NotEnrolled, stranger.Code);

            _ = await connection.EnrollAsync(agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.Download), CancellationToken.None);
            _ = await connection.InventoryAsync(CancellationToken.None);

            // Revoked on the agent's host while connected: the very next request is refused.
            Assert.True(agent.Enrollments.Revoke(controller.Pin));
            RemoteAgentException revoked = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.InventoryAsync(CancellationToken.None));
            Assert.Equal(RemoteErrorCode.Revoked, revoked.Code);
            RemoteHello hello = await connection.HelloAsync(CancellationToken.None);
            Assert.Equal((false, (Guid?)null, RemoteGrants.None), (hello.Enrolled, hello.HostId, hello.Grants));
        }

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(controller))
        {
            EnrollmentToken fresh = agent.Enrollments.Issue(agent.Identity.Pin, RemoteGrants.Download);
            RemoteAgentException again = await Assert.ThrowsAsync<RemoteAgentException>(() => connection.EnrollAsync(fresh, CancellationToken.None));
            Assert.Equal(RemoteErrorCode.Revoked, again.Code);
            Assert.Equal(1, agent.Enrollments.PendingTokens);

            // The enrollments refuse the revoked key on their own too, and leave its token unconsumed.
            Assert.Null(agent.Enrollments.Admit(fresh.Secret, controller.Pin));
            Assert.Equal(1, agent.Enrollments.PendingTokens);
        }

        Assert.Empty(agent.Enrollments.Controllers);
    }

    [Fact(DisplayName = "§15: an agent refuses a message it does not answer and keeps the connection, and a frame not version 1's - another magic, a payload past the limit, a field cut short - it refuses saying why, then ends the connection")]
    public async Task AFrameNotVersionOnesEndsTheConnection()
    {
        await using AgentFixture agent = AgentFixture.Start();
        using var controller = RemoteIdentity.Create("controller");

        await using (RemoteAgentConnection connection = await agent.ConnectAsync(controller))
        {
            RemoteAgentException unknown = await Assert.ThrowsAsync<RemoteAgentException>(() =>
                connection.AskAsync((RemoteMessageType)77, [], CancellationToken.None));
            Assert.Equal(RemoteErrorCode.Unsupported, unknown.Code);
            Assert.False((await connection.HelloAsync(CancellationToken.None)).Enrolled);
        }

        byte[] hello = Header(RemoteMessageType.Hello, 0);
        byte[] otherMagic = [.. hello];
        "ICBP"u8.CopyTo(otherMagic);
        byte[] oversized = Header(RemoteMessageType.Hello, RemoteFrameCodec.MaximumPayloadBytes + 1);
        byte[] cutShort = [.. Header(RemoteMessageType.Hello, 4), 1, 0, 16, 0];
        foreach (byte[] malformed in new[] { otherMagic, oversized, cutShort })
        {
            (System.Net.Sockets.TcpClient client, SslStream stream) = await agent.RawAsync(controller);
            using (client)
            await using (stream)
            {
                await stream.WriteAsync(malformed);

                // Answered and ended at once, not when the connection would have idled out.
                using var promptly = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                RemoteFrame? refusal = await RemoteFrameCodec.ReadAsync(stream, promptly.Token);
                Assert.NotNull(refusal);
                Assert.True(refusal.IsError);
                Assert.Equal(RemoteErrorCode.Malformed, RemoteRefusal.Decode(refusal.Payload).Code);
                Assert.Null(await RemoteFrameCodec.ReadAsync(stream, promptly.Token));
            }
        }
    }

    [Fact(DisplayName = "§15: an enrollment token's text carries the agent's pin and its secret, and one cut short or altered is told from one never made")]
    public void ATokensTextRoundTripsAndIsChecked()
    {
        using var agent = RemoteIdentity.Create("agent");
        EnrollmentToken token = EnrollmentToken.Create(agent.Pin);
        string text = token.ToString();
        Assert.StartsWith(EnrollmentToken.Prefix, text, StringComparison.Ordinal);

        Assert.True(EnrollmentToken.TryParse("  " + text + "\r\n", out EnrollmentToken? read, out string? none));
        Assert.Null(none);
        Assert.Equal(agent.Pin, read!.Agent);
        Assert.True(read.Secret.SequenceEqual(token.Secret));

        char[] altered = text.ToCharArray();
        int at = EnrollmentToken.Prefix.Length + 20;
        altered[at] = altered[at] == 'A' ? 'B' : 'A';
        Assert.False(EnrollmentToken.TryParse(new string(altered), out _, out string? alteredProblem));
        Assert.Contains("check", alteredProblem, StringComparison.Ordinal);
        Assert.False(EnrollmentToken.TryParse(text[..^5], out _, out string? cutProblem));
        Assert.Contains("copied whole", cutProblem, StringComparison.Ordinal);
        Assert.False(EnrollmentToken.TryParse(agent.Pin.Hex, out _, out string? prefixProblem));
        Assert.Contains(EnrollmentToken.Prefix, prefixProblem, StringComparison.Ordinal);

        // A pin is the key's, not the certificate's, and reads back from its digits in either case.
        Assert.True(RemotePin.TryParse(agent.Pin.Hex.ToUpperInvariant(), out RemotePin upper));
        Assert.Equal(agent.Pin, upper);
        Assert.Equal(64, agent.Pin.Hex.Length);
        Assert.False(RemotePin.TryParse(agent.Pin.Hex[..63], out _));
    }

    private static byte[] Header(RemoteMessageType type, int length)
    {
        byte[] header = new byte[RemoteFrameCodec.HeaderSize];
        "ICRA"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), RemoteFrameCodec.Major);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)RemoteFrameAttributes.Request);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)length);
        _ = Guid.NewGuid().TryWriteBytes(header.AsSpan(16), bigEndian: true, out _);
        return header;
    }
}
