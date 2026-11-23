using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>An agent as a fetch asks it: its connection in InterCat, a stand-in in tests.</summary>
public interface IRemoteEvidenceAgent
{
    /// <summary>The pin of the agent asked.</summary>
    RemotePin Agent { get; }

    Task<RemoteEvidencePage> ListCaptureAsync(Guid capture, long generation, uint first, uint most, CancellationToken cancellationToken);

    Task<RemoteFileBytes> ReadCaptureFileAsync(Guid capture, string name, long offset, uint count, CancellationToken cancellationToken);
}

/// <summary>A fetch the controller does not do, in its own words.</summary>
public sealed class RemoteFetchRefusedException : Exception
{
    public RemoteFetchRefusedException()
    {
    }

    public RemoteFetchRefusedException(string message)
        : base(message)
    {
    }

    public RemoteFetchRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Where a fetch stands: the files and bytes it holds whole of all the evidence lists.</summary>
public sealed record RemoteFetchProgress(int FilesDone, int Files, long BytesDone, long Bytes);

/// <summary>
/// What a fetch did: the generation it fetched, the files and bytes the evidence holds, the bytes it read from the agent and
/// those it already held whole, and whether the folder held this very evidence before it began.
/// </summary>
public sealed record RemoteFetchResult(Guid CaptureId, long Generation, int Files, long Bytes, long FetchedBytes, long KeptBytes, bool AlreadyFetched);

/// <summary>
/// A controller's fetch of a capture's evidence from the agent that recorded it (`remote-agent-v1` §4.5): store and
/// forward. The evidence is listed, then read file by file into an unfinished folder beside the one asked for, each file
/// counted only once its bytes hash to the SHA-256 its listing gives; its pointer is written last, and the folder takes the
/// asked name only once every file is whole and the caller's own check of it passes - so the folder asked for holds the
/// capture's evidence whole, or is not there. Run again after a failure or Ctrl+C it goes on: a file already held whole, by
/// its content, is kept and not fetched again, and one fetched part way goes on from where it stopped.
/// </summary>
public static class RemoteEvidenceFetch
{
    /// <summary>What a fetch's unfinished folder is called beside the folder asked for.</summary>
    public const string UnfinishedSuffix = ".partial";

    private const string MarkContract = "remote-fetch-v1";
    private const int ListingAttempts = 3;
    private const long FreeSpaceMargin = 64L * 1_024 * 1_024;

    /// <summary>The unfinished folder of a fetch into <paramref name="folder"/>.</summary>
    public static string UnfinishedFor(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + UnfinishedSuffix;

    /// <summary>
    /// Fetches the evidence of <paramref name="capture"/> from <paramref name="agent"/> into <paramref name="folder"/>.
    /// <paramref name="check"/> reads the unfinished folder once every file is whole, and says why it is not the capture's
    /// evidence, or null where it is; <paramref name="pageFiles"/> and <paramref name="readBytes"/> bound each listing page
    /// and each read below what the agent allows.
    /// </summary>
    /// <exception cref="RemoteFetchRefusedException">The folder holds something else, or the evidence is not what its listing says.</exception>
    /// <exception cref="RemoteAgentException">The agent refused, or answered what version 1 does not write.</exception>
    public static async Task<RemoteFetchResult> FetchAsync(
        IRemoteEvidenceAgent agent,
        Guid capture,
        string folder,
        Func<string, string?>? check,
        IProgress<RemoteFetchProgress>? progress,
        CancellationToken cancellationToken,
        uint pageFiles = 0,
        uint readBytes = RemoteFileBytes.MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string unfinished = UnfinishedFor(target);
        readBytes = Math.Clamp(readBytes, 1u, (uint)RemoteFileBytes.MaximumBytes);

        RemoteEvidenceSnapshot listing = await ListAsync(agent, capture, pageFiles, cancellationToken).ConfigureAwait(false);
        int count = listing.Files.Count;
        long total = listing.TotalBytes;
        if (Holds(target, listing))
        {
            return new RemoteFetchResult(capture, listing.Generation, count, total, 0, total, AlreadyFetched: true);
        }

        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
        {
            throw new RemoteFetchRefusedException(
                $"{target} holds something else already; a capture's evidence is fetched into a folder of its own.");
        }

        Begin(unfinished, capture, agent.Agent);
        long present = Directory.EnumerateFiles(unfinished).Sum(path => new FileInfo(path).Length);
        if (FreeBytes(unfinished) is { } free && free < total - present + FreeSpaceMargin)
        {
            throw new RemoteFetchRefusedException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The capture's evidence is {total / 1_048_576.0:0.0} MiB; the disk under {unfinished} has {free / 1_048_576.0:0.0} MiB ")
                + "free, short of it and a margin of 64 MiB. Nothing more was fetched.");
        }

        long kept = 0;
        long fetched = 0;
        int done = 0;
        foreach (RemoteEvidenceFile file in listing.Files)
        {
            string path = Path.Combine(unfinished, file.Name);
            long whole = await FetchFileAsync(agent, capture, file, path, readBytes,
                read =>
                {
                    fetched += read;
                    progress?.Report(new RemoteFetchProgress(done, count, Math.Min(total, kept + fetched), total));
                },
                cancellationToken).ConfigureAwait(false);
            kept += whole;
            done++;
            progress?.Report(new RemoteFetchProgress(done, count, Math.Min(total, kept + fetched), total));
        }

        // Only the files the listing names, its pointer last, and nothing an earlier listing left.
        var listed = new HashSet<string>(listing.Files.Select(file => file.Name), StringComparer.OrdinalIgnoreCase) { RemoteEvidenceNames.FetchMark };
        foreach (string leftover in Directory.EnumerateFiles(unfinished).Where(path => !listed.Contains(Path.GetFileName(path))))
        {
            File.Delete(leftover);
        }

        string pointer = Path.Combine(unfinished, RemoteEvidenceNames.PointerFile);
        await WriteDurablyAsync(pointer, listing.PointerBytes, cancellationToken).ConfigureAwait(false);
        if (check?.Invoke(unfinished) is { } problem)
        {
            File.Delete(pointer);
            throw new RemoteFetchRefusedException(
                $"Every file of the evidence is as its listing says, yet it does not verify as the capture's evidence: {problem} "
                + $"It is kept unfinished at {unfinished}.");
        }

        File.Delete(Path.Combine(unfinished, RemoteEvidenceNames.FetchMark));
        if (Directory.Exists(target))
        {
            Directory.Delete(target);
        }

        await PublishAsync(unfinished, target, cancellationToken).ConfigureAwait(false);
        return new RemoteFetchResult(capture, listing.Generation, count, total, fetched, kept, AlreadyFetched: false);
    }

    /// <summary>
    /// Every page of the evidence's listing, of one generation: a generation that changes while it is listed - the broker
    /// still finishing the capture - is listed again from its first page, a few times at most.
    /// </summary>
    private static async Task<RemoteEvidenceSnapshot> ListAsync(IRemoteEvidenceAgent agent, Guid capture, uint pageFiles,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            RemoteEvidencePage first = await agent.ListCaptureAsync(capture, 0, 0, pageFiles, cancellationToken).ConfigureAwait(false);
            var files = new List<RemoteEvidenceFile>(first.Files);
            try
            {
                while (files.Count < first.Total)
                {
                    RemoteEvidencePage page = await agent.ListCaptureAsync(capture, first.Generation, (uint)files.Count, pageFiles,
                        cancellationToken).ConfigureAwait(false);
                    if (page.Files.Count == 0 || page.Total != first.Total || page.TotalBytes != first.TotalBytes
                        || !page.PointerBytes.AsSpan().SequenceEqual(first.PointerBytes))
                    {
                        throw new RemoteAgentException(RemoteErrorCode.Malformed, "The agent's listing changed between its pages, or a page held no file.");
                    }

                    files.AddRange(page.Files);
                }
            }
            catch (RemoteAgentException busy) when (busy.Code == RemoteErrorCode.Busy && attempt < ListingAttempts)
            {
                continue;
            }

            var snapshot = new RemoteEvidenceSnapshot(first.Generation, first.PointerBytes, files);
            string? problem = files.Count != first.Total || snapshot.TotalBytes != first.TotalBytes
                ? "its pages do not add up to the files and bytes it says"
                : snapshot.Problem();
            return problem is null
                ? snapshot
                : throw new RemoteAgentException(RemoteErrorCode.Malformed, "The agent's listing of the capture's evidence is not one InterCat takes: " + problem + ".");
        }
    }

    /// <summary>Whether <paramref name="target"/> holds this listing's evidence already: its pointer, and every file at its length.</summary>
    private static bool Holds(string target, RemoteEvidenceSnapshot listing)
    {
        string pointer = Path.Combine(target, RemoteEvidenceNames.PointerFile);
        return File.Exists(pointer)
            && File.ReadAllBytes(pointer).AsSpan().SequenceEqual(listing.PointerBytes)
            && listing.Files.All(file => new FileInfo(Path.Combine(target, file.Name)) is { Exists: true } held && held.Length == file.Length);
    }

    /// <summary>
    /// Opens the unfinished folder of a fetch of <paramref name="capture"/> from <paramref name="agent"/>: a new one, or one
    /// an earlier fetch of the same capture from the same agent left. One that is another's is not taken over.
    /// </summary>
    private static void Begin(string unfinished, Guid capture, RemotePin agent)
    {
        string mark = Path.Combine(unfinished, RemoteEvidenceNames.FetchMark);
        if (Directory.Exists(unfinished))
        {
            if (!File.Exists(mark))
            {
                if (Directory.EnumerateFileSystemEntries(unfinished).Any())
                {
                    throw new RemoteFetchRefusedException(
                        $"{unfinished} holds files no fetch of this capture left there; remove it, or fetch into another folder.");
                }
            }
            else if (Marked(mark) is not { } marked || marked.Capture != capture || marked.Agent != agent)
            {
                throw new RemoteFetchRefusedException(
                    $"{unfinished} holds an unfinished fetch of another capture, or from another agent; finish that fetch, remove it, "
                    + "or fetch into another folder.");
            }
            else
            {
                // An earlier fetch that stopped after its pointer was written lists again before it counts.
                File.Delete(Path.Combine(unfinished, RemoteEvidenceNames.PointerFile));
                return;
            }
        }

        _ = Directory.CreateDirectory(unfinished);
        File.WriteAllText(mark, new JsonObject
        {
            ["contract"] = MarkContract,
            ["captureId"] = capture.ToString("D"),
            ["agent"] = agent.Hex,
        }.ToJsonString());
    }

    private static (Guid Capture, RemotePin Agent)? Marked(string mark)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(mark)) is JsonObject root
                && (string?)root["contract"] == MarkContract
                && Guid.TryParse((string?)root["captureId"], out Guid capture)
                && RemotePin.TryParse((string?)root["agent"] ?? string.Empty, out RemotePin agent)
                    ? (capture, agent)
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> the file the listing names: kept where it is whole already, gone on with where it was
    /// fetched part way, and fetched from its start where it is not this file. A file whose bytes do not hash to its listed
    /// SHA-256 is fetched once more from its start, and refused if it differs again. Returns the bytes it already held.
    /// </summary>
    private static async Task<long> FetchFileAsync(IRemoteEvidenceAgent agent, Guid capture, RemoteEvidenceFile file, string path,
        uint readBytes, Action<int> read, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long held = 0;
            if (File.Exists(path))
            {
                held = new FileInfo(path).Length;
                if (held > file.Length)
                {
                    File.Delete(path);
                    held = 0;
                }
                else
                {
                    await HashAsync(path, hash, cancellationToken).ConfigureAwait(false);
                }
            }

            long kept = held;

            // An empty file is made too: the evidence names it, so the folder holds it.
            if (held < file.Length || !File.Exists(path))
            {
                var written = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 64 * 1_024, FileOptions.Asynchronous);
                await using (written.ConfigureAwait(false))
                {
                    written.Position = held;
                    while (held < file.Length)
                    {
                        uint asked = (uint)Math.Min(readBytes, file.Length - held);
                        RemoteFileBytes bytes = await agent.ReadCaptureFileAsync(capture, file.Name, held, asked, cancellationToken).ConfigureAwait(false);
                        if (bytes.Bytes.Length == 0 || bytes.Offset != held || bytes.Bytes.Length > asked)
                        {
                            throw new RemoteAgentException(RemoteErrorCode.Malformed,
                                $"The agent answered a read of '{file.Name}' with nothing, or with other bytes than asked.");
                        }

                        await written.WriteAsync(bytes.Bytes, cancellationToken).ConfigureAwait(false);
                        hash.AppendData(bytes.Bytes);
                        held += bytes.Bytes.Length;
                        read(bytes.Bytes.Length);
                    }

                    written.Flush(flushToDisk: true);
                }
            }

            if (CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), file.Sha256))
            {
                return kept;
            }

            File.Delete(path);
            if (attempt == 2)
            {
                throw new RemoteFetchRefusedException(
                    $"'{file.Name}' was fetched twice and differed both times from the SHA-256 its listing gives: the evidence on "
                    + "the agent's host may be damaged. Nothing was published.");
            }
        }
    }

    /// <summary>The bytes free on the disk under <paramref name=path/>, or null where the system does not say.</summary>
    private static long? FreeBytes(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task HashAsync(string path, IncrementalHash hash, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1_024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            byte[] buffer = new byte[64 * 1_024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
    }

    private static async Task WriteDurablyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4_096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Gives the unfinished folder the asked name in one rename, waiting a few seconds for a scanner that holds a file in it.
    /// </summary>
    private static async Task PublishAsync(string unfinished, string target, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(unfinished, target);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 25)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
