using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>Progress acted on as it is reported, inline, so a test can move the capture between rounds.</summary>
internal sealed class InlineProgress<T>(Action<T> act) : IProgress<T>
{
    public void Report(T value) => act(value);
}

/// <summary>
/// M11's evidence followed while its capture records (`remote-agent-v1` §4.11, ADR-117): the controller that started a
/// capture lists its newest generation round after round, fetches only what each adds, writes its pointer last, and ends
/// once a listing says the capture closed - so its folder holds one whole generation at every round. The evidence is plain
/// files whose generation a test moves; no test here starts a broker.
/// </summary>
public sealed class RemoteFollowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "icat-remote-follow-" + Guid.NewGuid().ToString("N"));

    public RemoteFollowTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "§15: a controller follows a capture's evidence while it records - each round fetching only what its newest generation adds, its pointer written last, so the folder holds one whole generation at every round - and ends once a listing says the capture closed, its last generation held, two listings audited")]
    public async Task AControllerFollowsACaptureWhileItRecords()
    {
        string evidence = Evidence("source", ("a.bin", 70_000));
        var source = new FolderEvidence();
        await using var agent = CaptureAgent.Start(source);
        agent.Driver.Evidence = evidence;
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Guid capture = await RecordingAsync(connection);

        // A fetch of a capture still recording is refused as it always was; following it is not.
        RemoteAgentException busy = await Assert.ThrowsAsync<RemoteAgentException>(
            () => RemoteEvidenceFetch.FetchAsync(connection, capture, Path.Combine(root, "fetched"), null, null, None));
        Assert.Equal(RemoteErrorCode.Busy, busy.Code);

        string folder = Path.Combine(root, "followed");
        var rounds = new List<(RemoteFollowRound Round, string[] Held, string Pointer)>();
        var progress = new InlineProgress<RemoteFollowRound>(round =>
        {
            rounds.Add((round, Listed(folder), File.ReadAllText(Path.Combine(folder, RemoteEvidenceNames.PointerFile))));
            if (round.Round == 1)
            {
                // The broker publishes a newer generation, which adds a file.
                File.WriteAllBytes(Path.Combine(evidence, "b.bin"), Filled(5_000));
                source.Generation = 4;
            }
            else if (round.Round == 2)
            {
                // The capture closes, and the agent's next tick reads it so; its last generation releases the first file, as a
                // rolling capture releases its oldest.
                File.Delete(Path.Combine(evidence, "a.bin"));
                source.Generation = 5;
                agent.Driver.ClosesAfterStatuses = 0;
                agent.Time.Now += TimeSpan.FromSeconds(1);
            }
        });
        RemoteFollowResult followed = await RemoteEvidenceFollow.FollowAsync(connection, capture, folder, null, progress,
            TimeSpan.FromMilliseconds(20), None);

        Assert.Equal((3, 5L, 1, 5_000L, 75_000L, false),
            (followed.Rounds, followed.Generation, followed.Files, followed.Bytes, followed.FetchedBytes, followed.AlreadyHeld));
        Assert.Equal([(1, 3L, 1, 70_000L), (2, 4L, 2, 5_000L), (3, 5L, 1, 0L)],
            rounds.Select(kept => (kept.Round.Round, kept.Round.Generation, kept.Round.Files, kept.Round.FetchedBytes)));
        Assert.True(rounds[0].Round.StillRecording && rounds[1].Round.StillRecording);

        // At every round the folder held its generation whole, its pointer naming it.
        Assert.Equal(["a.bin", RemoteEvidenceNames.PointerFile, RemoteEvidenceNames.FetchMark], rounds[0].Held);
        Assert.Equal(["a.bin", "b.bin", RemoteEvidenceNames.PointerFile, RemoteEvidenceNames.FetchMark], rounds[1].Held);
        Assert.Equal(["{\"generation\":3}", "{\"generation\":4}", "{\"generation\":5}"], rounds.Select(kept => kept.Pointer));

        // Closed, the folder holds the capture's last generation alone, every file equal to its source, and no mark.
        Assert.Equal(["b.bin", RemoteEvidenceNames.PointerFile], Listed(folder));
        foreach (string name in Listed(evidence))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(evidence, name)), File.ReadAllBytes(Path.Combine(folder, name)));
        }

        // The audit says the following once while it recorded, and the listing once it closed - not every round.
        Assert.Equal(["refused: busy", $"following: {capture:D}, still recording", $"listed: {capture:D}, 1 files, 5000 bytes"],
            agent.Audit.Entries.Where(entry => entry.Action == RemoteMessageType.ListCapture).Select(entry => entry.Outcome));
    }

    [Fact(DisplayName = "§15: a follow stopped goes on where it stopped, keeping the files it held whole; a closed capture is followed in one round; a folder holding the capture's evidence already is held; and a folder of anything else is refused")]
    public async Task AFollowGoesOnAndKeepsToItsOwnFolder()
    {
        string evidence = Evidence("source", ("a.bin", 30_000), ("b.bin", 20_000));
        var source = new FolderEvidence();
        await using var agent = CaptureAgent.Start(source);
        agent.Driver.Evidence = evidence;
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Guid capture = await RecordingAsync(connection);

        // Stopped after its first round, the folder holds that generation and its mark.
        string folder = Path.Combine(root, "followed");
        using (var stop = new CancellationTokenSource())
        {
            var stopping = new InlineProgress<RemoteFollowRound>(_ => stop.Cancel());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteEvidenceFollow.FollowAsync(connection, capture, folder, null,
                stopping, TimeSpan.FromMilliseconds(20), stop.Token));
        }

        Assert.Equal(["a.bin", "b.bin", RemoteEvidenceNames.PointerFile, RemoteEvidenceNames.FetchMark], Listed(folder));

        // The capture closes; followed again, it ends in one round, fetching nothing it holds whole.
        _ = await connection.StopCaptureAsync(capture, None);
        RemoteFollowResult again = await RemoteEvidenceFollow.FollowAsync(connection, capture, folder, null, null, TimeSpan.FromMilliseconds(20), None);
        Assert.Equal((1, 0L, 50_000L), (again.Rounds, again.FetchedBytes, again.Bytes));
        Assert.Equal(["a.bin", "b.bin", RemoteEvidenceNames.PointerFile], Listed(folder));

        // Followed into a folder that holds it already, it is held; followed into one holding anything else, refused.
        RemoteFollowResult held = await RemoteEvidenceFollow.FollowAsync(connection, capture, folder, null, null, TimeSpan.FromMilliseconds(20), None);
        Assert.Equal((0, true), (held.Rounds, held.AlreadyHeld));

        // A folder its caller's check refuses keeps its mark, so following it again goes on there.
        string refusing = Path.Combine(root, "refusing");
        RemoteFetchRefusedException refusal = await Assert.ThrowsAsync<RemoteFetchRefusedException>(() => RemoteEvidenceFollow.FollowAsync(
            connection, capture, refusing, _ => "it is another capture's.", null, TimeSpan.FromMilliseconds(20), None));
        Assert.Contains("it is another capture's.", refusal.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(refusing, RemoteEvidenceNames.FetchMark)));
        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "notes.txt"), "mine");
        RemoteFetchRefusedException refused = await Assert.ThrowsAsync<RemoteFetchRefusedException>(
            () => RemoteEvidenceFollow.FollowAsync(connection, capture, other, null, null, TimeSpan.FromMilliseconds(20), None));
        Assert.Contains("holds something else already", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["notes.txt"], Listed(other));
    }

    [Fact(DisplayName = "§15: a listing's request says it follows, and its page says the capture still records, each only where so")]
    public void AFollowersListingSaysSo()
    {
        var capture = Guid.NewGuid();
        Assert.True(RemoteListRequest.Decode(new RemoteListRequest(capture, 0, 0, 0) { Following = true }.Encode()).Following);
        Assert.False(RemoteListRequest.Decode(new RemoteListRequest(capture, 0, 0, 0).Encode()).Following);
        var page = new RemoteEvidencePage(capture, 3, "{}"u8.ToArray(), 0, 0, 0, []);
        Assert.True(RemoteEvidencePage.Decode((page with { StillRecording = true }).Encode()).StillRecording);
        Assert.False(RemoteEvidencePage.Decode(page.Encode()).StillRecording);
    }

    /// <summary>A capture started and still recording, its lease long enough for the test.</summary>
    private static async Task<Guid> RecordingAsync(RemoteAgentConnection connection)
    {
        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 64), None);
        return (await connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(60), None)).CaptureId;
    }

    private static string[] Listed(string folder) =>
        [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>()];

    private static byte[] Filled(int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A folder of files of the lengths given, each filled with bytes of its own.</summary>
    private string Evidence(string name, params (string Name, int Length)[] files)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        foreach ((string file, int length) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, file), Filled(length));
        }

        return folder;
    }
}
