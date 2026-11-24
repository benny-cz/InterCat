using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>Where following a capture stands after a round: its number, the generation held, its files and bytes, what the round fetched, and whether the capture still records.</summary>
public sealed record RemoteFollowRound(int Round, long Generation, int Files, long Bytes, long FetchedBytes, bool StillRecording);

/// <summary>
/// What following a capture did: its rounds, the last generation held, its files and bytes, the bytes read from the agent,
/// and whether the folder held that generation before it began.
/// </summary>
public sealed record RemoteFollowResult(Guid CaptureId, int Rounds, long Generation, int Files, long Bytes, long FetchedBytes, bool AlreadyHeld);

/// <summary>
/// A controller following the evidence of a capture it started while the capture records (`remote-agent-v1` §4.11): round
/// after round, the evidence's newest generation is listed, every file of it the folder does not hold whole is fetched and
/// counted only once its bytes hash to its listed SHA-256, and the generation's pointer is replaced last - so at every round
/// the folder holds one whole generation of the capture's evidence, newer each time, which `icat follow` derives as it goes.
/// Once a listing says the capture closed, its last generation is held, only its files are kept, and the folder is checked
/// as the capture's evidence. Run again after Ctrl+C or a failure, it goes on: files held whole are kept.
/// </summary>
public static class RemoteEvidenceFollow
{
    private const string MarkContract = "remote-follow-v1";

    /// <summary>
    /// Follows the evidence of <paramref name="capture"/> from <paramref name="agent"/> into <paramref name="folder"/>,
    /// listing it every <paramref name="interval"/> while it records. <paramref name="check"/> reads the folder once the
    /// capture closed and its last generation is held, and says why it is not the capture's evidence, or null where it is.
    /// </summary>
    /// <exception cref="RemoteFetchRefusedException">The folder holds something else, or the evidence is not what its listing says.</exception>
    /// <exception cref="RemoteAgentException">The agent refused, or answered what version 1 does not write.</exception>
    public static async Task<RemoteFollowResult> FollowAsync(
        IRemoteEvidenceAgent agent,
        Guid capture,
        string folder,
        Func<string, string?>? check,
        IProgress<RemoteFollowRound>? progress,
        TimeSpan interval,
        CancellationToken cancellationToken,
        uint pageFiles = 0,
        uint readBytes = RemoteFileBytes.MaximumBytes,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        readBytes = Math.Clamp(readBytes, 1u, (uint)RemoteFileBytes.MaximumBytes);
        if (!Begin(target, capture, agent.Agent))
        {
            // A folder this follow did not mark is taken only where it holds the closed capture's evidence already.
            (RemoteEvidenceSnapshot closed, bool recording) = await RemoteEvidenceFetch.ListAsync(agent, capture, pageFiles, cancellationToken,
                following: true).ConfigureAwait(false);
            return !recording && RemoteEvidenceFetch.Holds(target, closed)
                ? new RemoteFollowResult(capture, 0, closed.Generation, closed.Files.Count, closed.TotalBytes, 0, AlreadyHeld: true)
                : throw new RemoteFetchRefusedException(
                    $"{target} holds something else already; a capture is followed into a folder of its own, or into one following it left.");
        }

        // The files held whole this run, by name and SHA-256: a round fetches only what its generation adds or changes.
        var whole = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        RemoteEvidenceSnapshot? held = null;
        int rounds = 0;
        long fetched = 0;
        while (true)
        {
            (RemoteEvidenceSnapshot listing, bool recording) = await RemoteEvidenceFetch.ListAsync(agent, capture, pageFiles, cancellationToken,
                following: true).ConfigureAwait(false);
            if (held is null || listing.Generation != held.Generation)
            {
                long present = listing.Files.Where(file => whole.ContainsKey(file.Name)).Sum(file => file.Length);
                if (RemoteEvidenceFetch.FreeBytes(target) is { } free && free < listing.TotalBytes - present + RemoteEvidenceFetch.FreeSpaceMargin)
                {
                    throw new RemoteFetchRefusedException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"The capture's evidence is {listing.TotalBytes / 1_048_576.0:0.0} MiB; the disk under {target} has ")
                        + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{free / 1_048_576.0:0.0} MiB free, short of it and a margin of 64 MiB. ")
                        + "The folder holds the generation fetched before.");
                }

                long before = fetched;
                foreach (RemoteEvidenceFile file in listing.Files)
                {
                    string path = Path.Combine(target, file.Name);
                    if (whole.TryGetValue(file.Name, out byte[]? kept) && kept.AsSpan().SequenceEqual(file.Sha256)
                        && new FileInfo(path) is { Exists: true } existing && existing.Length == file.Length)
                    {
                        continue;
                    }

                    _ = await RemoteEvidenceFetch.FetchFileAsync(agent, capture, file, path, readBytes, read => fetched += read,
                        cancellationToken).ConfigureAwait(false);
                    whole[file.Name] = file.Sha256;
                }

                // The pointer last, replaced whole: a reader of the folder meets one generation or the next, never part of one.
                await ReplaceDurablyAsync(Path.Combine(target, RemoteEvidenceNames.PointerFile), listing.PointerBytes, cancellationToken)
                    .ConfigureAwait(false);
                held = listing;
                rounds++;
                progress?.Report(new RemoteFollowRound(rounds, listing.Generation, listing.Files.Count, listing.TotalBytes, fetched - before, recording));
            }

            if (!recording)
            {
                break;
            }

            await Task.Delay(interval, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }

        // Closed, its last generation held: only its files are kept, and the folder is checked as the capture's evidence.
        RemoteEvidenceSnapshot last = held!;
        var listed = new HashSet<string>(last.Files.Select(file => file.Name), StringComparer.OrdinalIgnoreCase)
        {
            RemoteEvidenceNames.PointerFile,
            RemoteEvidenceNames.FetchMark,
        };
        foreach (string leftover in Directory.EnumerateFiles(target).Where(path => !listed.Contains(Path.GetFileName(path))))
        {
            File.Delete(leftover);
        }

        if (check?.Invoke(target) is { } problem)
        {
            throw new RemoteFetchRefusedException(
                $"Every file of the evidence is as its listing says, yet it does not verify as the capture's evidence: {problem}");
        }

        File.Delete(Path.Combine(target, RemoteEvidenceNames.FetchMark));
        return new RemoteFollowResult(capture, rounds, last.Generation, last.Files.Count, last.TotalBytes, fetched, AlreadyHeld: false);
    }

    /// <summary>
    /// Opens <paramref name="target"/> for following <paramref name="capture"/> from <paramref name="agent"/>: a new or empty
    /// folder, marked so, or one an earlier follow of the same capture from the same agent left. False where the folder
    /// holds something this follow did not mark.
    /// </summary>
    private static bool Begin(string target, Guid capture, RemotePin agent)
    {
        string mark = Path.Combine(target, RemoteEvidenceNames.FetchMark);
        if (File.Exists(target))
        {
            return false;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return File.Exists(mark) && RemoteEvidenceFetch.Marked(mark, MarkContract) is { } marked && marked.Capture == capture && marked.Agent == agent;
        }

        _ = Directory.CreateDirectory(target);
        File.WriteAllText(mark, new JsonObject
        {
            ["contract"] = MarkContract,
            ["captureId"] = capture.ToString("D"),
            ["agent"] = agent.Hex,
        }.ToJsonString());
        return true;
    }

    /// <summary>Writes <paramref name="bytes"/> beside <paramref name="path"/>, durably, and moves them over it in one step.</summary>
    private static async Task ReplaceDurablyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string written = path + ".new";
        await RemoteEvidenceFetch.WriteDurablyAsync(written, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(written, path, overwrite: true);
    }
}
