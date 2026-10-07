using System.Globalization;
using System.Net;

namespace InterCat.Application;

/// <summary>What a person stated of an address translation (`contracts/workspace-v17.md` §6).</summary>
public enum WorkspaceTranslationDecision
{
    /// <summary>A person stated that an endpoint one capture sees is an endpoint the other holds.</summary>
    Stated = 1,

    /// <summary>A person withdrew the statement: from this revision the two are two endpoints again.</summary>
    Withdrawn = 2,
}

/// <summary>
/// One revision of a known address translation (§8.3): a person's statement that an endpoint one capture sees -
/// <see cref="Seen"/>, such as a port forward's or a NAT's public address - is <see cref="Is"/> for the other end, or its
/// withdrawal. Both are an address with a port, or both an address alone, whose ports then pass through unchanged. It is
/// a person's statement, never evidence of its own, and kept when a later revision withdraws it.
/// </summary>
public sealed record WorkspaceAddressTranslation
{
    public required int Revision { get; init; }

    public required WorkspaceTranslationDecision Decision { get; init; }

    public required string Seen { get; init; }

    public required string Is { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>
    /// Records a person's statement that the endpoint <paramref name="seen"/> is <paramref name="actual"/> for the other end
    /// of a connection, as a revision of the investigation: candidate joins then mirror through it (§8.3).
    /// </summary>
    public static WorkspaceAddressTranslation StateTranslation(string workspacePath, string seen, string actual, string? note, DateTimeOffset now) =>
        DecideTranslation(workspacePath, seen, actual, WorkspaceTranslationDecision.Stated, note, now);

    /// <summary>Withdraws a person's statement of an address translation, kept as a revision of its own.</summary>
    public static WorkspaceAddressTranslation WithdrawTranslation(string workspacePath, string seen, string actual, DateTimeOffset now) =>
        DecideTranslation(workspacePath, seen, actual, WorkspaceTranslationDecision.Withdrawn, null, now);

    /// <summary>Every translation in force, one per pair of endpoints, in the order first stated.</summary>
    public static IReadOnlyList<WorkspaceAddressTranslation> TranslationsInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return TranslationsInForce(workspace.AddressTranslations);
    }

    /// <summary>Of translation revisions, those in force: each pair's latest, when it states rather than withdraws.</summary>
    public static IReadOnlyList<WorkspaceAddressTranslation> TranslationsInForce(IReadOnlyList<WorkspaceAddressTranslation> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        return [.. revisions
            .GroupBy(translation => TranslationPair(translation.Seen, translation.Is))
            .Select(group => group.MaxBy(translation => translation.Revision)!)
            .Where(translation => translation.Decision == WorkspaceTranslationDecision.Stated)
            .OrderBy(translation => translation.Revision)];
    }

    /// <summary>
    /// The endpoints a person stated are one with <paramref name="endpoint"/>, each with the translation that says so: an
    /// address and port translated whole, or an address translated with its port kept. Loopback addresses are never
    /// translated: each names its own host.
    /// </summary>
    public static IReadOnlyList<(string Endpoint, WorkspaceAddressTranslation By)> Translated(
        IReadOnlyList<WorkspaceAddressTranslation> inForce,
        string endpoint)
    {
        ArgumentNullException.ThrowIfNull(inForce);
        if (Endpoint(endpoint) is not { } parsed)
        {
            return [];
        }

        var found = new List<(string, WorkspaceAddressTranslation)>();
        foreach (WorkspaceAddressTranslation translation in inForce)
        {
            if (Endpoint(translation.Seen) is not { } seen || Endpoint(translation.Is) is not { } actual)
            {
                continue;
            }

            foreach (((IPAddress Address, int? Port) from, (IPAddress Address, int? Port) to) in new[] { (seen, actual), (actual, seen) })
            {
                if (from.Port is { } port ? from.Address.Equals(parsed.Address) && port == parsed.Port
                    : from.Address.Equals(parsed.Address) && parsed.Port is not null)
                {
                    found.Add((Format(to.Address, to.Port ?? parsed.Port), translation));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// An endpoint, or an address alone, as InterCat writes one - `192.0.2.7:443`, `[2001:db8::7]:443`, or the address
    /// alone - or null when <paramref name="text"/> is none.
    /// </summary>
    public static string? CanonicalEndpoint(string? text) => Endpoint(text) is { } parsed ? Format(parsed.Address, parsed.Port) : null;

    private static WorkspaceAddressTranslation DecideTranslation(
        string workspacePath,
        string seen,
        string actual,
        WorkspaceTranslationDecision decision,
        string? note,
        DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        string from = CanonicalEndpoint(seen)
            ?? throw new InvalidOperationException($"'{seen}' is no endpoint: write an address, with its port or without, such as 203.0.113.7:8443.");
        string to = CanonicalEndpoint(actual)
            ?? throw new InvalidOperationException($"'{actual}' is no endpoint: write an address, with its port or without, such as 10.0.0.5:443.");
        if (TranslationFault(from, to) is { } fault)
        {
            throw new InvalidOperationException(fault);
        }

        bool inForce = TranslationsInForce(workspace).Any(known => TranslationPair(known.Seen, known.Is) == TranslationPair(from, to));
        if (decision == WorkspaceTranslationDecision.Withdrawn && !inForce)
        {
            throw new InvalidOperationException($"No translation of {from} as {to} is in force, so there is none to withdraw.");
        }

        if (decision == WorkspaceTranslationDecision.Stated && inForce)
        {
            throw new InvalidOperationException($"{from} is stated to be {to} already.");
        }

        var translation = new WorkspaceAddressTranslation
        {
            Revision = workspace.AddressTranslations.Count == 0 ? 1 : checked(workspace.AddressTranslations.Max(known => known.Revision) + 1),
            Decision = decision,
            Seen = from,
            Is = to,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RecordedUtc = now,
        };
        Save(full, workspace with { AddressTranslations = [.. workspace.AddressTranslations, translation], UpdatedUtc = now }, text);
        return translation;
    }

    /// <summary>Why two endpoints make no translation - one of them loopback, the two one, or of two kinds - or null.</summary>
    private static string? TranslationFault(string from, string to)
    {
        (IPAddress Address, int? Port) seen = Endpoint(from)!.Value;
        (IPAddress Address, int? Port) actual = Endpoint(to)!.Value;
        return IPAddress.IsLoopback(seen.Address) || IPAddress.IsLoopback(actual.Address)
            ? "A loopback address names the host it is on, so it is never translated to another."
            : from == to ? "An endpoint is itself already; a translation relates two different ones."
            : (seen.Port is null) != (actual.Port is null)
                ? "Both ends of a translation have a port, or neither has: an address alone keeps its ports."
                : null;
    }

    private static (string, string) TranslationPair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static (IPAddress Address, int? Port)? Endpoint(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('['))
        {
            int close = trimmed.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || !IPAddress.TryParse(trimmed[1..close], out IPAddress? bracketed))
            {
                return null;
            }

            string rest = trimmed[(close + 1)..];
            return rest.Length == 0 ? (bracketed, null)
                : rest.StartsWith(':') && Port(rest[1..]) is { } port ? (bracketed, port)
                : null;
        }

        int colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':', StringComparison.Ordinal) == colon)
        {
            return IPAddress.TryParse(trimmed[..colon], out IPAddress? address) && Port(trimmed[(colon + 1)..]) is { } port
                ? (address, port)
                : null;
        }

        return IPAddress.TryParse(trimmed, out IPAddress? alone) ? (alone, null) : null;
    }

    private static int? Port(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65_535 ? port : null;

    private static string Format(IPAddress address, int? port)
    {
        string text = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return port is { } known ? string.Create(CultureInfo.InvariantCulture, $"{text}:{known}") : text;
    }

    /// <summary>What makes a file's translations contradict themselves, or null (`contracts/workspace-v17.md` §6).</summary>
    private static string? TranslationProblem(InvestigationWorkspaceFile workspace)
    {
        // Known address translations arrived with the eighth version (revision 269).
        if (workspace.AddressTranslations.Count > 0 && VersionOf(workspace) < 8)
        {
            return $"a {workspace.Contract} file holds no address translation";
        }

        if (workspace.AddressTranslations.Any(translation => translation is null))
        {
            return "it lists an empty address translation";
        }

        if (workspace.AddressTranslations.GroupBy(translation => translation.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1)
            is { } revision)
        {
            return $"address translation revision {revision.Key} is not a unique positive number";
        }

        return workspace.AddressTranslations.FirstOrDefault(translation => !Enum.IsDefined(translation.Decision)
            || CanonicalEndpoint(translation.Seen) != translation.Seen || CanonicalEndpoint(translation.Is) != translation.Is
            || TranslationFault(translation.Seen, translation.Is) is not null) is { } wrong
            ? $"address translation revision {wrong.Revision} is of no known decision, names no endpoint as InterCat writes one, "
                + "or relates a loopback address, an endpoint to itself, or an endpoint to an address alone"
            : null;
    }
}
