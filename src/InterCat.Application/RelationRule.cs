using System.Globalization;
using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>
/// The rule a relationship was derived by, named as its derivation names it (R4): an identity and a version, so a mark on
/// the graph can say how it was made. A change to what the rule relates, how, or how strongly is a new version (§24), and
/// a relationship derived by an earlier one is never read as the later one's.
/// </summary>
public readonly record struct RelationRule(string Identity, int Version)
{
    /// <summary>A TCP relationship: connection incarnations whose two ends mirror each other (`contracts/relations-v1.md`).</summary>
    public static RelationRule TransportEndpoint { get; } = Parse(TransportRelationIndex.RelationRule);

    /// <summary>An RPC relationship: client calls linked through ALPC to the calls that served them (ADR-034).</summary>
    public static RelationRule RpcCallPeer { get; } = Parse(RpcPeerIndex.PeerRule);

    /// <summary>The tour's relationships, which no rule derived from a capture: they illustrate, and say so.</summary>
    public static RelationRule Tour { get; } = new("synthetic-tour", 1);

    /// <summary>The rule as its derivation records it, such as "transport-endpoint-relation-v4".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Identity}-v{Version}");

    /// <summary>The rule a recorded name - an identity, "-v" and a version from 1, written with no leading zero - names.</summary>
    public static RelationRule Parse(string recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        int at = recorded.LastIndexOf("-v", StringComparison.Ordinal);
        return at > 0 && recorded.Length > at + 2 && recorded[at + 2] != '0'
            && int.TryParse(recorded.AsSpan(at + 2), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            ? new(recorded[..at], version)
            : throw new FormatException($"\"{recorded}\" names no rule and version: a rule is named by its identity, \"-v\" and its version.");
    }
}
