using System.Security.Cryptography;
using System.Text;
using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>Evidence as plain files in a folder, each listed by name with its SHA-256, its pointer a few bytes naming the generation.</summary>
internal sealed class FolderEvidence : IRemoteEvidenceSource
{
    private int lists;

    /// <summary>How many times the evidence was listed.</summary>
    public int Lists => Volatile.Read(ref lists);

    public long Generation { get; set; } = 3;

    /// <summary>Where set, every read's first byte differs from what the listing says.</summary>
    public bool Corrupts { get; set; }

    public RemoteEvidenceSnapshot List(string directory)
    {
        _ = Interlocked.Increment(ref lists);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The evidence is gone.");
        }

        RemoteEvidenceFile[] files = [.. Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
            .Select(path => new RemoteEvidenceFile(Path.GetFileName(path), new FileInfo(path).Length, SHA256.HashData(File.ReadAllBytes(path))))];
        return new RemoteEvidenceSnapshot(Generation, Encoding.UTF8.GetBytes($"{{\"generation\":{Generation}}}"), files);
    }

    public int Read(string directory, string name, long offset, Span<byte> into)
    {
        using var stream = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        int read = stream.ReadAtLeast(into, into.Length, throwOnEndOfStream: false);
        if (Corrupts && read > 0)
        {
            into[0] ^= 0xFF;
        }

        return read;
    }
}

/// <summary>An agent as a fetch asks it, counting each read, and stopping the fetch as Ctrl+C does after so many.</summary>
internal sealed class CountingAgent(IRemoteEvidenceAgent agent, int stopsAfterReads = int.MaxValue, CancellationTokenSource? stop = null) : IRemoteEvidenceAgent
{
    private readonly List<(string Name, long Offset)> reads = [];

    public IReadOnlyList<(string Name, long Offset)> Reads => reads;

    public RemotePin Agent => agent.Agent;

    public Task<RemoteEvidencePage> ListCaptureAsync(Guid capture, long generation, uint first, uint most, CancellationToken cancellationToken) =>
        agent.ListCaptureAsync(capture, generation, first, most, cancellationToken);

    public Task<RemoteFileBytes> ReadCaptureFileAsync(Guid capture, string name, long offset, uint count, CancellationToken cancellationToken)
    {
        if (reads.Count >= stopsAfterReads)
        {
            stop!.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        reads.Add((name, offset));
        return agent.ReadCaptureFileAsync(capture, name, offset, count, cancellationToken);
    }
}

/// <summary>An agent that lists what a test says, and reads zeros.</summary>
internal sealed class ScriptedAgent(params RemoteEvidenceFile[] files) : IRemoteEvidenceAgent
{
    public RemotePin Agent { get; } = RemotePin.FromBytes(new byte[32]);

    public Task<RemoteEvidencePage> ListCaptureAsync(Guid capture, long generation, uint first, uint most, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteEvidencePage(capture, 1, "{}"u8.ToArray(), (uint)files.Length, files.Sum(file => file.Length), 0, files));

    public Task<RemoteFileBytes> ReadCaptureFileAsync(Guid capture, string name, long offset, uint count, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteFileBytes(offset, new byte[count]));
}

/// <summary>Progress kept as it is reported.</summary>
internal sealed class KeptProgress : IProgress<RemoteFetchProgress>
{
    public List<RemoteFetchProgress> Steps { get; } = [];

    public void Report(RemoteFetchProgress value) => Steps.Add(value);
}

/// <summary>
/// M11's store-and-forward fetch (`remote-agent-v1` §4.5, ADR-117): the controller that started a capture fetches its
/// evidence from the agent once the capture closed - listed in pages, read in pieces, each file checked against the
/// SHA-256 its listing gives - into a folder that holds the evidence whole or is not there, going on where a fetch stopped.
/// The evidence is plain files; no test here starts a broker.
/// </summary>
public sealed class RemoteFetchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "icat-remote-fetch-" + Guid.NewGuid().ToString("N"));

    public RemoteFetchTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "§15: a controller fetches the evidence of a capture it started into a folder of its own, every file as its listing says and its pointer with them, one listing audited, and a fetch of what it holds fetches nothing")]
    public async Task AControllerFetchesTheEvidenceOfItsCapture()
    {
        string evidence = Evidence("source", ("coverage-0000000003.json", 0), ("journal-0000000001.icatj", 150_000), ("manifest-0000000003.json", 900));
        var source = new FolderEvidence();
        await using var agent = CaptureAgent.Start(source);
        agent.Driver.Evidence = evidence;
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Guid capture = await CapturedAsync(connection);

        string folder = Path.Combine(root, "fetched");
        var progress = new KeptProgress();
        RemoteFetchResult fetched = await RemoteEvidenceFetch.FetchAsync(connection, capture, folder, null, progress, None, pageFiles: 1);
        Assert.Equal((3, 150_900L, 150_900L, 0L, false), (fetched.Files, fetched.Bytes, fetched.FetchedBytes, fetched.KeptBytes, fetched.AlreadyFetched));
        foreach (string file in Directory.EnumerateFiles(evidence))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(folder, Path.GetFileName(file))));
        }

        Assert.Equal("{\"generation\":3}", File.ReadAllText(Path.Combine(folder, RemoteEvidenceNames.PointerFile)));
        Assert.Equal(4, Directory.EnumerateFiles(folder).Count());
        Assert.False(Directory.Exists(RemoteEvidenceFetch.UnfinishedFor(folder)));
        Assert.Equal(new RemoteFetchProgress(3, 3, 150_900, 150_900), progress.Steps[^1]);

        // One listing, audited once however many pages it took, and read from the evidence once; reads are not audited.
        Assert.Single(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.ListCapture && entry.Outcome == $"listed: {capture:D}, 3 files, 150900 bytes");
        Assert.DoesNotContain(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.ReadCaptureFile);
        Assert.Equal(1, source.Lists);

        RemoteFetchResult again = await RemoteEvidenceFetch.FetchAsync(connection, capture, folder, null, null, None);
        Assert.Equal((true, 0L, 150_900L), (again.AlreadyFetched, again.FetchedBytes, again.KeptBytes));
    }

    [Fact(DisplayName = "§15: a fetch stopped part way goes on where it stopped, fetching no file it holds whole again, and publishes nothing until every file is whole")]
    public async Task AStoppedFetchGoesOnWhereItStopped()
    {
        string evidence = Evidence("source", ("a.bin", 10_240), ("b.bin", 20_480), ("c.bin", 5_120));
        await using var agent = CaptureAgent.Start(new FolderEvidence());
        agent.Driver.Evidence = evidence;
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Guid capture = await CapturedAsync(connection);
        string folder = Path.Combine(root, "fetched");
        string unfinished = RemoteEvidenceFetch.UnfinishedFor(folder);

        // a.bin takes three reads of 4 KiB and b.bin its first; the fifth read is where Ctrl+C stops it.
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteEvidenceFetch.FetchAsync(
            new CountingAgent(connection, stopsAfterReads: 4, stop), capture, folder, null, null, stop.Token, readBytes: 4_096));
        Assert.False(Directory.Exists(folder));
        Assert.Equal((10_240L, 4_096L), (new FileInfo(Path.Combine(unfinished, "a.bin")).Length, new FileInfo(Path.Combine(unfinished, "b.bin")).Length));

        var resumed = new CountingAgent(connection);
        RemoteFetchResult fetched = await RemoteEvidenceFetch.FetchAsync(resumed, capture, folder, null, null, None, readBytes: 4_096);
        Assert.Equal((14_336L, 35_840L - 14_336L, false), (fetched.KeptBytes, fetched.FetchedBytes, fetched.AlreadyFetched));
        Assert.DoesNotContain(resumed.Reads, read => read.Name == "a.bin");
        Assert.Equal(("b.bin", 4_096L), resumed.Reads[0]);
        foreach (string file in Directory.EnumerateFiles(evidence))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(folder, Path.GetFileName(file))));
        }

        Assert.False(File.Exists(Path.Combine(folder, RemoteEvidenceNames.FetchMark)));
        Assert.False(Directory.Exists(unfinished));
    }

    [Fact(DisplayName = "§15: a capture's evidence is served to the controller that started it alone, holding download, once it closed, file by file as listed, and an agent serving none says so")]
    public async Task EvidenceIsServedToItsCapturesOwnerOnceItClosed()
    {
        string evidence = Evidence("source", ("journal-0000000001.icatj", 1_000));
        var source = new FolderEvidence();
        await using var agent = CaptureAgent.Start(source);
        agent.Driver.Evidence = evidence;
        using var owner = RemoteIdentity.Create("controller");
        using var other = RemoteIdentity.Create("controller");
        using var watcher = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection owning = await agent.EnrolledAsync(owner, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        await using RemoteAgentConnection another = await agent.EnrolledAsync(other, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        await using RemoteAgentConnection watching = await agent.EnrolledAsync(watcher, RemoteGrants.MetadataCapture);

        RemotePreparedCapture prepared = await owning.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 64), None);
        RemoteCaptureStarted started = await owning.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(10), None);
        Guid capture = started.CaptureId;
        RemoteAgentException recording = await Refused(() => owning.ListCaptureAsync(capture, 0, 0, 0, None));
        Assert.Equal(RemoteErrorCode.Busy, recording.Code);
        Assert.Contains("still recording", recording.Message, StringComparison.Ordinal);
        _ = await owning.StopCaptureAsync(capture, None);

        Assert.Equal(RemoteErrorCode.NotPermitted, (await Refused(() => another.ListCaptureAsync(capture, 0, 0, 0, None))).Code);
        Assert.Equal(RemoteErrorCode.NotPermitted, (await Refused(() => another.ReadCaptureFileAsync(capture, "journal-0000000001.icatj", 0, 100, None))).Code);
        RemoteAgentException notGranted = await Refused(() => watching.ListCaptureAsync(capture, 0, 0, 0, None));
        Assert.Equal(RemoteErrorCode.NotPermitted, notGranted.Code);
        Assert.Contains("not granted download", notGranted.Message, StringComparison.Ordinal);
        Assert.Equal(RemoteErrorCode.NotPermitted, (await Refused(() => owning.ListCaptureAsync(Guid.NewGuid(), 0, 0, 0, None))).Code);
        Assert.Equal(0, source.Lists);

        RemoteEvidencePage page = await owning.ListCaptureAsync(capture, 0, 0, 0, None);
        Assert.Equal((3L, 1u, "journal-0000000001.icatj", 1_000L), (page.Generation, page.Total, page.Files[0].Name, page.Files[0].Length));
        Assert.Equal(RemoteErrorCode.NotPermitted, (await Refused(() => owning.ReadCaptureFileAsync(capture, "..\\enrollments-v1.json", 0, 100, None))).Code);
        Assert.Equal(RemoteErrorCode.NotPermitted, (await Refused(() => owning.ReadCaptureFileAsync(capture, "journal-0000000001.icatj", 1_001, 100, None))).Code);
        RemoteFileBytes tail = await owning.ReadCaptureFileAsync(capture, "journal-0000000001.icatj", 990, 100, None);
        Assert.Equal(File.ReadAllBytes(Path.Combine(evidence, "journal-0000000001.icatj"))[990..], tail.Bytes);

        // A page of a generation the evidence no longer names is refused as busy, so its controller lists it again.
        source.Generation = 4;
        Assert.Equal(4L, (await owning.ListCaptureAsync(capture, 0, 0, 0, None)).Generation);
        Assert.Equal(RemoteErrorCode.Busy, (await Refused(() => owning.ListCaptureAsync(capture, 3, 0, 0, None))).Code);
        Assert.Contains(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.ListCapture && entry.Outcome == "refused: busy");
        Assert.Contains(agent.Audit.Entries, entry => entry.Action == RemoteMessageType.ListCapture && entry.Outcome == "refused: not-permitted");

        await using var bare = CaptureAgent.Start();
        await using RemoteAgentConnection asking = await bare.EnrolledAsync(owner, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Assert.Equal(RemoteErrorCode.Unsupported, (await Refused(() => asking.ListCaptureAsync(capture, 0, 0, 0, None))).Code);
    }

    [Fact(DisplayName = "§15: a fetch takes no name a listing should not hold, publishes nothing whose bytes differ from its listing or that its caller's check refuses, and writes over no folder holding something else")]
    public async Task AFetchTakesNothingItShouldNot()
    {
        string folder = Path.Combine(root, "fetched");
        foreach (string name in new[] { "..", "a..b", "a\\b.bin", "con.json", "current-generation.json", "remote-fetch-v1.json", "-x", "x.", new string('a', 65) })
        {
            RemoteAgentException refused = await Assert.ThrowsAsync<RemoteAgentException>(() => RemoteEvidenceFetch.FetchAsync(
                new ScriptedAgent(new RemoteEvidenceFile(name, 1, new byte[32])), Guid.NewGuid(), folder, null, null, None));
            Assert.Equal(RemoteErrorCode.Malformed, refused.Code);
        }

        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(RemoteEvidenceFetch.UnfinishedFor(folder)));

        string evidence = Evidence("source", ("journal-0000000001.icatj", 70_000), ("manifest-0000000003.json", 500));
        var source = new FolderEvidence { Corrupts = true };
        await using var agent = CaptureAgent.Start(source);
        agent.Driver.Evidence = evidence;
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        Guid capture = await CapturedAsync(connection);

        RemoteFetchRefusedException differs = await Assert.ThrowsAsync<RemoteFetchRefusedException>(
            () => RemoteEvidenceFetch.FetchAsync(connection, capture, folder, null, null, None));
        Assert.Contains("'journal-0000000001.icatj' was fetched twice and differed both times", differs.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));

        // The caller's own check of the whole folder decides too: refused, nothing is published and its pointer goes.
        source.Corrupts = false;
        RemoteFetchRefusedException unchecked_ = await Assert.ThrowsAsync<RemoteFetchRefusedException>(
            () => RemoteEvidenceFetch.FetchAsync(connection, capture, folder, _ => "it is another capture's.", null, None));
        Assert.Contains("yet it does not verify as the capture's evidence: it is another capture's.", unchecked_.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(Path.Combine(RemoteEvidenceFetch.UnfinishedFor(folder), RemoteEvidenceNames.PointerFile)));
        string? checkedFolder = null;
        RemoteFetchResult passed = await RemoteEvidenceFetch.FetchAsync(connection, capture, folder, unfinished =>
        {
            checkedFolder = unfinished;
            return null;
        }, null, None);
        Assert.Equal((RemoteEvidenceFetch.UnfinishedFor(folder), 70_500L, 0L), (checkedFolder, passed.KeptBytes, passed.FetchedBytes));

        // A folder holding something else is not written over, and an unfinished fetch of another capture is not taken over.
        string taken = Path.Combine(root, "taken");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "mine.txt"), "mine");
        RemoteFetchRefusedException holds = await Assert.ThrowsAsync<RemoteFetchRefusedException>(
            () => RemoteEvidenceFetch.FetchAsync(connection, capture, taken, null, null, None));
        Assert.Contains("holds something else already", holds.Message, StringComparison.Ordinal);
        Assert.Equal(["mine.txt"], Directory.EnumerateFiles(taken).Select(Path.GetFileName));

        string shared = Path.Combine(root, "shared");
        using (var stop = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteEvidenceFetch.FetchAsync(
                new CountingAgent(connection, stopsAfterReads: 1, stop), capture, shared, null, null, stop.Token));
        }

        Guid second = await CapturedAsync(connection);
        RemoteFetchRefusedException another = await Assert.ThrowsAsync<RemoteFetchRefusedException>(
            () => RemoteEvidenceFetch.FetchAsync(connection, second, shared, null, null, None));
        Assert.Contains("an unfinished fetch of another capture", another.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§15: an agent keeps the captures it started in its folder and serves their evidence after it restarts; a capture it cannot keep a record of is stopped, not left recording")]
    public async Task AnAgentServesItsCapturesAfterItRestarts()
    {
        string evidence = Evidence("source", ("journal-0000000001.icatj", 2_000));
        string kept = Path.Combine(root, "agent");
        Directory.CreateDirectory(kept);
        using var key = RemoteIdentity.Create("agent");
        var enrollments = new AgentEnrollments();
        using var controller = RemoteIdentity.Create("controller");
        Guid capture;
        await using (var first = CaptureAgent.Start(new FolderEvidence(), new FileCaptureRecords(kept), key, enrollments))
        {
            first.Driver.Evidence = evidence;
            await using RemoteAgentConnection connection = await first.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
            capture = await CapturedAsync(connection);
        }

        RemoteCaptureRecord record = Assert.Single(new FileCaptureRecords(kept).Read());
        Assert.Equal((capture, controller.Pin, evidence, "explore"), (record.CaptureId, record.Owner, record.Evidence, record.Profile));

        await using (var restarted = CaptureAgent.Start(new FolderEvidence(), new FileCaptureRecords(kept), key, enrollments))
        {
            await using RemoteAgentConnection connection = await RemoteAgentConnection.ConnectAsync(restarted.Endpoint, controller, key.Pin,
                TimeSpan.FromSeconds(10), None);
            RemoteFetchResult fetched = await RemoteEvidenceFetch.FetchAsync(connection, capture, Path.Combine(root, "fetched"), null, null, None);
            Assert.Equal((1, 2_000L), (fetched.Files, fetched.FetchedBytes));
        }

        await using var forgetful = CaptureAgent.Start(new FolderEvidence(), new UnkeptRecords());
        await using RemoteAgentConnection asking = await forgetful.EnrolledAsync(controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        RemotePreparedCapture prepared = await asking.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 64), None);
        RemoteAgentException unkept = await Refused(() => asking.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(10), None));
        Assert.Equal(RemoteErrorCode.Failed, unkept.Code);
        Assert.Contains("could not keep a record of the capture, so it stopped it", unkept.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(kept, unkept.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["start", "stop"], forgetful.Driver.Calls.Where(call => call is "start" or "stop"));
    }

    [Fact(DisplayName = "§15: an agent's capture records keep the newest 1,024, read back as kept, and a file that does not read as InterCat wrote it is refused, not reset")]
    public void CaptureRecordsAreKeptBoundedAndNeverReset()
    {
        var records = new FileCaptureRecords(root);
        var owner = RemotePin.FromBytes(SHA256.HashData("owner"u8));
        DateTimeOffset started = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        Guid[] captures = [.. Enumerable.Range(0, RemoteCaptureRecordsRule.MaximumRecords + 6).Select(_ => Guid.NewGuid())];
        foreach ((Guid capture, int at) in captures.Select((capture, at) => (capture, at)))
        {
            records.Add(new RemoteCaptureRecord(capture, owner, $"C:\\evidence\\{at}", "explore", started.AddSeconds(at)));
        }

        IReadOnlyList<RemoteCaptureRecord> read = new FileCaptureRecords(root).Read();
        Assert.Equal(captures[6..], read.Select(record => record.CaptureId));
        Assert.Equal(new RemoteCaptureRecord(captures[^1], owner, $"C:\\evidence\\{captures.Length - 1}", "explore", started.AddSeconds(captures.Length - 1)), read[^1]);

        File.WriteAllText(Path.Combine(root, FileCaptureRecords.FileName), "{\"contract\":\"something-else\"}");
        InvalidDataException damaged = Assert.Throws<InvalidDataException>(() => records.Read());
        Assert.Contains("they are not reset", damaged.Message, StringComparison.Ordinal);
    }

    /// <summary>A capture started, then stopped by its controller, so its evidence is served.</summary>
    private static async Task<Guid> CapturedAsync(RemoteAgentConnection connection)
    {
        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 64), None);
        RemoteCaptureStarted started = await connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(10), None);
        _ = await connection.StopCaptureAsync(started.CaptureId, None);
        return started.CaptureId;
    }

    private static async Task<RemoteAgentException> Refused(Func<Task> asking) => await Assert.ThrowsAsync<RemoteAgentException>(asking);

    /// <summary>A folder of files of the lengths given, each filled with bytes of its own.</summary>
    private string Evidence(string name, params (string Name, int Length)[] files)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        foreach ((string file, int length) in files)
        {
            byte[] bytes = new byte[length];
            new Random(file.GetHashCode(StringComparison.Ordinal)).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(folder, file), bytes);
        }

        return folder;
    }

    /// <summary>Records an agent cannot keep, as a full disk or a damaged file leaves them.</summary>
    private sealed class UnkeptRecords : IRemoteCaptureRecords
    {
        public IReadOnlyList<RemoteCaptureRecord> Read() => [];

        public void Add(RemoteCaptureRecord record) => throw new IOException("There is not enough space on the disk.");
    }
}
