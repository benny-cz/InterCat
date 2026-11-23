namespace InterCat.Remote;

/// <summary>
/// One remote action as the agent records it (`remote-agent-v1` §7): when, which controller's key, what it asked and what
/// came of it. Never an address, a port, a path or a command line (P16): a controller is named by its pin alone.
/// </summary>
public sealed record RemoteAuditEntry(DateTimeOffset Utc, RemotePin Controller, RemoteMessageType Action, string Outcome);

/// <summary>Where an agent records what was asked of it.</summary>
public interface IRemoteAudit
{
    void Record(RemoteAuditEntry entry);
}

/// <summary>The agent's audit kept in memory, the newest <see cref="Capacity"/> entries, for a listing or a test.</summary>
public sealed class RemoteAuditLog : IRemoteAudit
{
    private readonly Lock gate = new();
    private readonly Queue<RemoteAuditEntry> entries = new();

    public RemoteAuditLog(int capacity = 4_096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public void Record(RemoteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            if (entries.Count == Capacity)
            {
                _ = entries.Dequeue();
            }

            entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<RemoteAuditEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }
}
