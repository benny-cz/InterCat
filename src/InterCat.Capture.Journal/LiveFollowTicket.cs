using System.Text.Json;

namespace InterCat.Capture.Journal;

/// <summary>
/// Where a live session's evidence is, written while a viewer follows it (plan §3.1 step 6). A viewer that crashes loses
/// no evidence: the broker stops the capture when its owner lease lapses and keeps it finalized. The viewer's own
/// session, though, holds only what it had followed, and this ticket lets a later launch find the rest.
/// </summary>
/// <remarks>
/// The ticket sits beside the session directory, never in it: a session directory holds only files its store names, and
/// an orphan sweep reports anything else. The follow holds the ticket open while it runs, so a ticket another process
/// can open belongs to a follow that ended early. It is removed once the session holds everything the capture published.
/// </remarks>
public sealed record LiveFollowTicket
{
    public const string ContractName = "live-follow-v1";

    /// <summary>What a ticket's file name adds to its session directory's name.</summary>
    public const string Suffix = ".follow.json";

    private const int MaximumTicketBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public required string Contract { get; init; }

    public required Guid CaptureId { get; init; }

    /// <summary>The broker-owned evidence session the capture published into.</summary>
    public required string EvidenceDirectory { get; init; }

    /// <summary>The user's own session the viewer derives into.</summary>
    public required string SessionDirectory { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    /// When the broker stops the capture unless its owner renews the lease, as of the last renewal the follow recorded.
    /// A capture with no finalization yet can still be recording until then; a later launch judges from it (broker-v1).
    /// </summary>
    public required DateTimeOffset OwnerLeaseExpiresUtc { get; init; }

    public static LiveFollowTicket For(
        Guid captureId,
        string evidenceDirectory,
        string sessionDirectory,
        DateTimeOffset startedUtc,
        DateTimeOffset ownerLeaseExpiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        return captureId == Guid.Empty
            ? throw new ArgumentException("A follow ticket names the capture it follows.", nameof(captureId))
            : new()
            {
                Contract = ContractName,
                CaptureId = captureId,
                EvidenceDirectory = Path.GetFullPath(evidenceDirectory),
                SessionDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)),
                StartedUtc = startedUtc,
                OwnerLeaseExpiresUtc = ownerLeaseExpiresUtc,
            };
    }

    /// <summary>The ticket file for a session directory.</summary>
    public static string PathFor(string sessionDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory)) + Suffix;

    /// <summary>
    /// Writes the ticket and holds it open until the hold is completed or disposed. While it is held, no other process
    /// takes the follow for an interrupted one.
    /// </summary>
    public LiveFollowHold Hold()
    {
        string path = PathFor(SessionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        try
        {
            var hold = new LiveFollowHold(stream, path, this);
            hold.Write();
            return hold;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The tickets under a session root whose follow is not running, newest first. A ticket held by a running follow,
    /// one that cannot be read as a ticket, and one that names another session directory than its own file's are left
    /// alone: a ticket can only ever send a finish into the session beside it.
    /// </summary>
    public static IReadOnlyList<LiveFollowTicket> FindInterrupted(string sessionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        if (!Directory.Exists(sessionRoot))
        {
            return [];
        }

        var found = new List<LiveFollowTicket>();
        foreach (string path in Directory.EnumerateFiles(sessionRoot, "*" + Suffix, SearchOption.TopDirectoryOnly))
        {
            using FileStream? stream = TryOpenUnheld(path);
            if (stream is not null && Read(stream, path) is { } ticket)
            {
                found.Add(ticket);
            }
        }

        return [.. found.OrderByDescending(ticket => ticket.StartedUtc)];
    }

    /// <summary>
    /// Takes an interrupted follow's ticket for a finish, as the follow held it; null while another process holds it or
    /// when it no longer reads as this ticket. The finish completes the hold once the session holds everything.
    /// </summary>
    public static LiveFollowHold? TryTake(LiveFollowTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        string path = PathFor(ticket.SessionDirectory);
        FileStream? stream = TryOpenUnheld(path);
        if (stream is null)
        {
            return null;
        }

        if (Read(stream, path) != ticket)
        {
            stream.Dispose();
            return null;
        }

        return new(stream, path, ticket);
    }

    /// <summary>Removes a session's ticket, if it has one and no follow holds it. Returns whether it was removed.</summary>
    public static bool Remove(string sessionDirectory)
    {
        string path = PathFor(sessionDirectory);
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static JsonSerializerOptions Options => Json;

    /// <summary>
    /// Opens a ticket for writing, which is what finds a running follow: it holds the ticket and shares it only for
    /// reading. Null while it is held, or when it cannot be opened at all.
    /// </summary>
    private static FileStream? TryOpenUnheld(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static LiveFollowTicket? Read(FileStream stream, string path)
    {
        try
        {
            if (stream.Length is 0 or > MaximumTicketBytes)
            {
                return null;
            }

            stream.Position = 0;
            LiveFollowTicket? ticket = JsonSerializer.Deserialize<LiveFollowTicket>(stream, Json);
            return ticket is { Contract: ContractName } && ticket.CaptureId != Guid.Empty
                && Path.IsPathFullyQualified(ticket.EvidenceDirectory) && Path.IsPathFullyQualified(ticket.SessionDirectory)
                && string.Equals(PathFor(ticket.SessionDirectory), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                ? ticket
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>A running follow's hold on its ticket, or a finish's.</summary>
public sealed class LiveFollowHold : IDisposable
{
    private readonly string path;
    private FileStream? stream;

    internal LiveFollowHold(FileStream stream, string path, LiveFollowTicket ticket)
    {
        this.stream = stream;
        this.path = path;
        Ticket = ticket;
    }

    public LiveFollowTicket Ticket { get; private set; }

    /// <summary>
    /// Records the owner lease the broker just renewed, so a later launch knows until when the capture could still be
    /// recording. A ticket that could not be rewritten keeps its earlier expiry, which only makes a launch wait less.
    /// </summary>
    public void Renew(DateTimeOffset ownerLeaseExpiresUtc)
    {
        if (stream is null || ownerLeaseExpiresUtc <= Ticket.OwnerLeaseExpiresUtc)
        {
            return;
        }

        Ticket = Ticket with { OwnerLeaseExpiresUtc = ownerLeaseExpiresUtc };
        try
        {
            Write();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The capture goes on either way; the ticket on disk keeps the earlier expiry.
        }
    }

    /// <summary>The session holds everything the capture published: the ticket goes with the hold.</summary>
    public void Complete()
    {
        Release();
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A ticket left behind is found at the next launch, which sees a finished session and removes it.
        }
    }

    /// <summary>The follow ended early: the ticket stays, released, for a later launch to finish from.</summary>
    public void Dispose() => Release();

    internal void Write()
    {
        FileStream held = stream ?? throw new ObjectDisposedException(nameof(LiveFollowHold));
        held.Position = 0;
        held.SetLength(0);
        JsonSerializer.Serialize(held, Ticket, LiveFollowTicket.Options);
        held.Flush(flushToDisk: true);
    }

    private void Release()
    {
        stream?.Dispose();
        stream = null;
    }
}
