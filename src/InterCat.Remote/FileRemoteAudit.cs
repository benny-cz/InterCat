using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterCat.Remote;

/// <summary>
/// The agent's audit kept in its folder (`remote-agent-v1` §7): one JSON line an entry in <c>audit-v1.log</c>, appended
/// as each request is answered and flushed. Past 4 MiB the log becomes <c>audit-v1.previous.log</c>, replacing the one
/// before, so the audit holds the latest few tens of thousands of requests and no more.
/// </summary>
public sealed class FileRemoteAudit : IRemoteAudit
{
    public const string FileName = "audit-v1.log";
    public const string PreviousFileName = "audit-v1.previous.log";
    public const long MaximumBytes = 4L * 1024 * 1024;

    private readonly Lock gate = new();
    private readonly string path;
    private readonly string previous;

    public FileRemoteAudit(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        path = Path.Combine(folder, FileName);
        previous = Path.Combine(folder, PreviousFileName);
    }

    public void Record(RemoteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string line = new JsonObject
        {
            ["utc"] = entry.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["controller"] = entry.Controller.IsEmpty ? null : entry.Controller.Hex,
            ["action"] = Words(entry.Action),
            ["outcome"] = entry.Outcome,
        }.ToJsonString() + "\n";
        lock (gate)
        {
            if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
            {
                File.Move(path, previous, overwrite: true);
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>The newest <paramref name="last"/> entries, oldest first; a line that does not read is passed over.</summary>
    public static IReadOnlyList<RemoteAuditEntry> Read(string folder, int last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(last, 1);
        var entries = new List<RemoteAuditEntry>();
        foreach (string file in new[] { PreviousFileName, FileName }.Select(name => Path.Combine(folder, name)).Where(File.Exists))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (Parse(line) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries.Count <= last ? entries : entries[^last..];
    }

    /// <summary>A message type in the words the audit and the contract use.</summary>
    public static string Words(RemoteMessageType action) => action switch
    {
        RemoteMessageType.Hello => "hello",
        RemoteMessageType.Enroll => "enroll",
        RemoteMessageType.Inventory => "inventory",
        RemoteMessageType.Error => "error",
        _ => "unknown",
    };

    private static RemoteAuditEntry? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject item
                || (string?)item["utc"] is not { } utc
                || (string?)item["action"] is not { } action
                || (string?)item["outcome"] is not { } outcome)
            {
                return null;
            }

            RemotePin controller = RemotePin.TryParse((string?)item["controller"], out RemotePin pin) ? pin : default;
            RemoteMessageType type = action switch
            {
                "hello" => RemoteMessageType.Hello,
                "enroll" => RemoteMessageType.Enroll,
                "inventory" => RemoteMessageType.Inventory,
                _ => RemoteMessageType.Error,
            };
            return new(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), controller, type, outcome);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
