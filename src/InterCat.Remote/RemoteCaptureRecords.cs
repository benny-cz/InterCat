using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>
/// A capture an agent started for a controller (`remote-agent-v1` §4.5): whose it is, where its host's broker keeps its
/// evidence, what it recorded and when it started. The agent serves its evidence to that controller alone.
/// </summary>
public sealed record RemoteCaptureRecord(Guid CaptureId, RemotePin Owner, string Evidence, string Profile, DateTimeOffset StartedUtc);

/// <summary>Where an agent keeps the captures it started, so it serves their evidence after it restarts.</summary>
public interface IRemoteCaptureRecords
{
    IReadOnlyList<RemoteCaptureRecord> Read();

    /// <summary>Keeps <paramref name="record"/>, the newest; past <see cref="RemoteCaptureRecordsRule.MaximumRecords"/> the oldest is forgotten.</summary>
    void Add(RemoteCaptureRecord record);
}

/// <summary>What every store of capture records keeps alike.</summary>
public static class RemoteCaptureRecordsRule
{
    /// <summary>
    /// The most captures an agent remembers. The one it forgets is not removed: its evidence stays in the broker's root,
    /// where the host's own icat finds it; the agent just serves it no more.
    /// </summary>
    public const int MaximumRecords = 1_024;

    internal static IReadOnlyList<RemoteCaptureRecord> With(IReadOnlyList<RemoteCaptureRecord> known, RemoteCaptureRecord record) =>
        [.. known.Where(kept => kept.CaptureId != record.CaptureId).Append(record).TakeLast(MaximumRecords)];
}

/// <summary>Capture records held in memory, for one process.</summary>
public sealed class MemoryCaptureRecords : IRemoteCaptureRecords
{
    private readonly Lock gate = new();
    private IReadOnlyList<RemoteCaptureRecord> records = [];

    public IReadOnlyList<RemoteCaptureRecord> Read()
    {
        lock (gate)
        {
            return records;
        }
    }

    public void Add(RemoteCaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            records = RemoteCaptureRecordsRule.With(records, record);
        }
    }
}

/// <summary>
/// Capture records kept in a file, <c>captures-v1.json</c>, in the agent's folder: read and written under a lock file as
/// its enrollments are, replaced whole. A file that does not read as one InterCat wrote is refused, not reset.
/// </summary>
public sealed class FileCaptureRecords : IRemoteCaptureRecords
{
    public const string FileName = "captures-v1.json";
    private const string Contract = "remote-agent-captures-v1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string path;
    private readonly string lockPath;

    public FileCaptureRecords(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        path = Path.Combine(folder, FileName);
        lockPath = Path.Combine(folder, "captures-v1.lock");
    }

    public IReadOnlyList<RemoteCaptureRecord> Read()
    {
        using FileStream held = AgentFiles.Hold(lockPath);
        return Load();
    }

    public void Add(RemoteCaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using FileStream held = AgentFiles.Hold(lockPath);
        IReadOnlyList<RemoteCaptureRecord> next = RemoteCaptureRecordsRule.With(Load(), record);
        var root = new JsonObject
        {
            ["contract"] = Contract,
            ["captures"] = new JsonArray([.. next.Select(kept => (JsonNode)new JsonObject
            {
                ["captureId"] = kept.CaptureId.ToString("D"),
                ["owner"] = kept.Owner.Hex,
                ["evidence"] = kept.Evidence,
                ["profile"] = kept.Profile,
                ["startedUtc"] = kept.StartedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            })]),
        };
        AgentFiles.Replace(path, root.ToJsonString(Indented));
    }

    private IReadOnlyList<RemoteCaptureRecord> Load()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            JsonObject root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
                ?? throw new InvalidDataException("it is not a JSON object");
            if ((string?)root["contract"] != Contract)
            {
                throw new InvalidDataException($"it is not {Contract}");
            }

            return [.. (root["captures"] as JsonArray ?? throw new InvalidDataException("it lists no captures"))
                .Select(item => item as JsonObject ?? throw new InvalidDataException("an entry is not an object"))
                .Select(item => new RemoteCaptureRecord(
                    Guid.Parse(Text(item, "captureId")),
                    RemotePin.TryParse(Text(item, "owner"), out RemotePin owner) ? owner : throw new InvalidDataException("an entry's owner is not a pin"),
                    Text(item, "evidence"),
                    Text(item, "profile"),
                    DateTimeOffset.Parse(Text(item, "startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException or InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"The agent's capture records at {path} do not read as InterCat wrote them ({exception.Message}); they are not "
                + "reset. Its administrator restores or removes the file.", exception);
        }
    }

    private static string Text(JsonObject item, string name) =>
        (string?)item[name] ?? throw new InvalidDataException($"an entry has no {name}");
}

/// <summary>How an agent's files are read and written alike: under a lock every process takes, and replaced whole.</summary>
internal static class AgentFiles
{
    /// <summary>The lock every reader and writer holds, waiting up to ten seconds for another process's.</summary>
    public static FileStream Hold(string lockPath)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < giveUp)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Writes <paramref name="text"/> beside <paramref name="path"/> and moves it over, so a reader never meets half a file.</summary>
    public static void Replace(string path, string text)
    {
        string written = path + ".new";
        File.WriteAllText(written, text);
        File.Move(written, path, overwrite: true);
    }
}
