using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>A stand-in for the host's preview: what a test says it compiled, and every request it was asked.</summary>
internal sealed class StandInPreview(RemotePreviewed answer) : IRemoteCapturePreview
{
    private readonly List<RemotePreviewRequest> asked = [];

    public IReadOnlyList<RemotePreviewRequest> Asked
    {
        get
        {
            lock (asked)
            {
                return [.. asked];
            }
        }
    }

    public RemotePreviewed Preview(RemotePreviewRequest request)
    {
        lock (asked)
        {
            asked.Add(request);
        }

        return answer;
    }
}

/// <summary>
/// M11's remote content scope preview (`remote-agent-v1` §4.10, ADR-117): an enrolled controller holding metadata capture
/// asks what a capture request would record on the agent's host - the host's preview, and whether the agent would prepare
/// it as asked - and nothing is prepared. Content is refused for a key not granted it, then by an agent whose policy keeps
/// none, then in the host's own words. The host's preview is a stand-in: no test here compiles one.
/// </summary>
public sealed class RemotePreviewTests
{
    private const string Compiled = "{\"requestedProfileId\":\"explore\",\"canStart\":true}";

    [Fact(DisplayName = "§15: a controller previews a capture on the agent's host - the host's preview, and that the agent would prepare it - every option of a focus or content scope carried, nothing prepared, and the audit naming the catalog's profile alone")]
    public async Task AControllerPreviewsACapture()
    {
        var host = new StandInPreview(new RemotePreviewed("explore", Compiled, false, null));
        await using CaptureAgent agent = CaptureAgent.Start(preview: host);
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture);
        var request = new RemotePreviewRequest("Explore ", 30, 512)
        {
            Mechanism = "http",
            ProcessIds = [4242, 7],
            AllowBroaderCapture = true,
            ContentSource = "etw/manifest/Microsoft-Windows-WinINet-Capture",
            Channels = ["*", "a.example"],
            MaximumRecordBytes = 4_096,
            MaximumSessionBytes = 1L << 33,
            Retention = "stop-at-limit",
            Inspection = "hex-text",
        };
        RemoteCapturePreview answer = await connection.PreviewCaptureAsync(request, None);
        Assert.Equal((true, (string?)null, Compiled), (answer.Served, answer.Reason, answer.Preview));

        RemotePreviewRequest asked = Assert.Single(host.Asked);
        Assert.Equal(("Explore ", 30u, 512u, "http", true, "etw/manifest/Microsoft-Windows-WinINet-Capture"),
            (asked.Profile, asked.Seconds, asked.JournalMebibytes, asked.Mechanism, asked.AllowBroaderCapture, asked.ContentSource));
        Assert.Equal(((uint?)4_096, (long?)(1L << 33), "stop-at-limit", "hex-text"),
            (asked.MaximumRecordBytes, asked.MaximumSessionBytes, asked.Retention, asked.Inspection));
        Assert.Equal([4242, 7], asked.ProcessIds);
        Assert.Equal(["*", "a.example"], asked.Channels);

        // A preview prepares nothing; the audit names the catalog's profile, never the request's words or a reason.
        Assert.Empty(agent.Driver.Calls);
        RemoteAuditEntry previewed = Assert.Single(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.PreviewCapture);
        Assert.Equal("previewed: explore; would prepare it", previewed.Outcome);

        // A request of no options carries none.
        _ = await connection.PreviewCaptureAsync(new RemotePreviewRequest("explore", 60, 1_024), None);
        RemotePreviewRequest plain = host.Asked[1];
        Assert.Equal(((string?)null, 0, false, (string?)null, 0, (uint?)null, (long?)null, (string?)null, (string?)null),
            (plain.Mechanism, plain.ProcessIds.Count, plain.AllowBroaderCapture, plain.ContentSource, plain.Channels.Count,
                plain.MaximumRecordBytes, plain.MaximumSessionBytes, plain.Retention, plain.Inspection));
    }

    [Fact(DisplayName = "§15: a content capture previewed is refused for a key not granted contents, then by an agent whose policy keeps none, then in its host's own words - each with the host's preview, and none prepared")]
    public async Task ContentIsRefusedInItsOrder()
    {
        const string NotRemotely = "A content capture is not recorded remotely: its host's broker keeps content only of its own client's processes.";
        var host = new StandInPreview(new RemotePreviewed("content", Compiled, true, NotRemotely));
        var keepsContents = new RemoteAgentPolicy(RemoteGrants.MetadataCapture | RemoteGrants.Download | RemoteGrants.Contents);
        await using CaptureAgent allowing = CaptureAgent.Start(preview: host, policy: keepsContents);
        await using CaptureAgent declining = CaptureAgent.Start(preview: host);
        var content = new RemotePreviewRequest("content", 60, 1_024);

        using var metadataOnly = RemoteIdentity.Create("controller");
        await using (RemoteAgentConnection connection = await allowing.EnrolledAsync(metadataOnly, RemoteGrants.MetadataCapture))
        {
            RemoteCapturePreview answer = await connection.PreviewCaptureAsync(content, None);
            Assert.Equal((false, "This controller was not granted contents here.", Compiled), (answer.Served, answer.Reason, answer.Preview));
        }

        using var granted = RemoteIdentity.Create("controller");
        await using (RemoteAgentConnection connection = await declining.EnrolledAsync(granted, RemoteGrants.MetadataCapture | RemoteGrants.Contents))
        {
            RemoteCapturePreview answer = await connection.PreviewCaptureAsync(content, None);
            Assert.Equal((false, "This agent's policy keeps no contents."), (answer.Served, answer.Reason));
        }

        await using (RemoteAgentConnection connection = await allowing.EnrolledAsync(granted, RemoteGrants.MetadataCapture | RemoteGrants.Contents))
        {
            RemoteCapturePreview answer = await connection.PreviewCaptureAsync(content, None);
            Assert.Equal((false, NotRemotely), (answer.Served, answer.Reason));
        }

        Assert.Equal(3, host.Asked.Count);
        Assert.Empty(allowing.Driver.Calls);
        Assert.Empty(declining.Driver.Calls);
        Assert.All(allowing.Audit.Entries.Where(entry => entry.Action == RemoteMessageType.PreviewCapture),
            entry => Assert.Equal("previewed: content; would not prepare it", entry.Outcome));
    }

    [Fact(DisplayName = "§15: a capture previewed past the agent's bounds is said so, the host's refusal is said as it said it, and a key without metadata capture, a key not enrolled and an agent that previews nothing are refused as a capture is")]
    public async Task APreviewIsBoundedAndRefusedAsACaptureIs()
    {
        var host = new StandInPreview(new RemotePreviewed("explore", Compiled, false, null));
        await using CaptureAgent agent = CaptureAgent.Start(preview: host);
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture);
        RemoteCapturePreview longer = await connection.PreviewCaptureAsync(new RemotePreviewRequest("explore", 601, 64), None);
        Assert.Equal((false, "This agent records for 1 to 600 seconds, not 601."), (longer.Served, longer.Reason));
        RemoteCapturePreview larger = await connection.PreviewCaptureAsync(new RemotePreviewRequest("explore", 60, 4_096), None);
        Assert.Equal((false, "This agent keeps a journal of 1 to 2,048 MiB, not 4,096."), (larger.Served, larger.Reason));

        // A host that would not record it says why; no preview where it refused before compiling.
        var refusing = new StandInPreview(new RemotePreviewed(null, null, false, "'nothing' is no capture profile; icat profiles lists every one."));
        await using CaptureAgent unknown = CaptureAgent.Start(preview: refusing);
        await using (RemoteAgentConnection asking = await unknown.EnrolledAsync(controller, RemoteGrants.MetadataCapture))
        {
            RemoteCapturePreview answer = await asking.PreviewCaptureAsync(new RemotePreviewRequest("nothing", 60, 64), None);
            Assert.Equal((false, "'nothing' is no capture profile; icat profiles lists every one.", (string?)null),
                (answer.Served, answer.Reason, answer.Preview));
            Assert.Equal("previewed: no profile; would not prepare it",
                Assert.Single(unknown.Audit.Entries, entry => entry.Action == RemoteMessageType.PreviewCapture).Outcome);
        }

        using var downloader = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection fetching = await agent.EnrolledAsync(downloader, RemoteGrants.Download);
        RemoteAgentException notPermitted = await Assert.ThrowsAsync<RemoteAgentException>(
            () => fetching.PreviewCaptureAsync(new RemotePreviewRequest("explore", 60, 64), None));
        Assert.Equal(RemoteErrorCode.NotPermitted, notPermitted.Code);

        using var stranger = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection unenrolled = await RemoteAgentConnection.ConnectAsync(agent.Endpoint, stranger, agent.Identity.Pin,
            TimeSpan.FromSeconds(10), None);
        RemoteAgentException notEnrolled = await Assert.ThrowsAsync<RemoteAgentException>(
            () => unenrolled.PreviewCaptureAsync(new RemotePreviewRequest("explore", 60, 64), None));
        Assert.Equal(RemoteErrorCode.NotEnrolled, notEnrolled.Code);
        Assert.Equal(2, host.Asked.Count);

        await using var silent = AgentFixture.Start();
        await using RemoteAgentConnection plain = await silent.ConnectAsync(controller);
        _ = await plain.EnrollAsync(silent.Enrollments.Issue(silent.Identity.Pin, RemoteGrants.MetadataCapture), None);
        RemoteAgentException unsupported = await Assert.ThrowsAsync<RemoteAgentException>(
            () => plain.PreviewCaptureAsync(new RemotePreviewRequest("explore", 60, 64), None));
        Assert.Equal(RemoteErrorCode.Unsupported, unsupported.Code);
    }

    [Fact(DisplayName = "§15: a preview's request holds at most 64 processes and 64 channel selectors of 1 to 1,024 bytes each, within one field, and one read otherwise is refused as malformed")]
    public void APreviewsRequestIsBounded()
    {
        Assert.Throws<InvalidOperationException>(() => new RemotePreviewRequest("explore", 60, 64)
        {
            ProcessIds = [.. Enumerable.Range(1, 65)],
        }.Encode());
        Assert.Throws<InvalidOperationException>(() => new RemotePreviewRequest("explore", 60, 64)
        {
            Channels = [.. Enumerable.Range(1, 65).Select(at => $"c{at}")],
        }.Encode());
        Assert.Throws<InvalidOperationException>(() => new RemotePreviewRequest("explore", 60, 64) { Channels = [new string('c', 1_025)] }.Encode());

        byte[] three = new RemoteFieldWriter().WriteText(1, "explore", 64).WriteUInt32(2, 60).WriteUInt32(3, 64).WriteBytes(5, [1, 0, 0]).ToArray();
        Assert.Throws<InvalidDataException>(() => RemotePreviewRequest.Decode(three));
        byte[] cut = new RemoteFieldWriter().WriteText(1, "explore", 64).WriteUInt32(2, 60).WriteUInt32(3, 64).WriteBytes(8, [5, 0, (byte)'a']).ToArray();
        Assert.Throws<InvalidDataException>(() => RemotePreviewRequest.Decode(cut));
        RemotePreviewRequest read = RemotePreviewRequest.Decode(new RemotePreviewRequest("explore", 60, 64)
        {
            ProcessIds = [.. Enumerable.Range(1, 64)],
            Channels = [.. Enumerable.Range(1, 64).Select(at => new string('c', 800) + at)],
        }.Encode());
        Assert.Equal((64, 64), (read.ProcessIds.Count, read.Channels.Count));
    }
}
