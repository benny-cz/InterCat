using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>One capture's generation in a query's snapshot vector, pinned by the manifest that names its contents.</summary>
public sealed record SnapshotEntry(CaptureId CaptureId, long Generation, string ManifestDigest);

/// <summary>
/// The values of the §24 version axes a metric answer can depend on, as the canonical form writes them. The form is a
/// function of these values, so a new binding or relation rule changes identities without changing the form.
/// </summary>
public sealed record AnalysisAxes(uint NormalizerContract, string EntityRevision, string CorrelationRevision, string MetricsContract)
{
    /// <summary>The axes this build answers under, over segments derived by the given normalizer contract.</summary>
    public static AnalysisAxes Current(NormalizerContractVersion normalizer) => new(
        normalizer.Value,
        ProcessInstanceIndex.BindingRule,
        TransportRelationIndex.RelationRule,
        AnalysisSpecification.MetricsContract);
}

/// <summary>
/// §10.4's `QueryIdentity` for a metric request: the canonicalization version, the SHA-256 of the canonical
/// specification, and the requested rows, which cut a result without changing what it aggregates
/// (`contracts/query-identity-v1.md`).
/// </summary>
public sealed record QueryIdentity
{
    /// <summary>The canonical form's version. It prefixes every identity, so a new form cannot collide with an old one.</summary>
    public const int CanonicalizationVersion = 1;

    /// <summary>The canonical specification: UTF-8 JSON with no insignificant whitespace, members in §23's order.</summary>
    public required string CanonicalSpecification { get; init; }

    /// <summary>`sha256:` and the lowercase hex SHA-256 of the canonical specification's UTF-8 bytes.</summary>
    public required string Hash { get; init; }

    /// <summary>How many ranked groups the result keeps; outside the hash, because it cuts the same aggregation.</summary>
    public int? RequestedRows { get; init; }

    /// <summary>The identity as one token: the canonicalization version, then the hash.</summary>
    public string Token => string.Create(CultureInfo.InvariantCulture, $"v{CanonicalizationVersion}:{Hash}");

    public override string ToString() => RequestedRows is { } rows
        ? string.Create(CultureInfo.InvariantCulture, $"{Token} (top {rows})")
        : Token;
}

/// <summary>
/// Builds the canonical form of §10.5 for the members `metrics-v1` implements. Two requests that mean the same thing
/// produce the same bytes, whatever they left implicit, and two that differ in any meaningful member produce
/// different ones.
/// </summary>
/// <remarks>
/// The form is written token by token rather than through a general JSON serializer, so its bytes depend only on this
/// contract and never on a library's escaping or number formatting. Every token is an enumeration name, a lowercase
/// hexadecimal identity, a digest or a decimal integer, none of which needs escaping; anything else is refused.
/// </remarks>
public static class AnalysisSpecification
{
    /// <summary>The `metricsContract` axis: what the numbers of a metric result mean (§24).</summary>
    public const string MetricsContract = "metrics-v1";

    /// <summary>
    /// The identity of a request that <see cref="MetricRequest.Check"/> accepted, over the snapshot it reads. The
    /// request is materialized first, so a default left implicit and the same default written out are one request.
    /// </summary>
    public static QueryIdentity IdentityOf(
        MetricRequest request,
        IReadOnlyList<SnapshotEntry> snapshot,
        AnalysisAxes axes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(axes);
        if (request.Check() is { } rejection)
        {
            throw new ArgumentException($"A refused request has no identity: {rejection.Reason}", nameof(request));
        }

        string canonical = Canonicalize(request.Materialized(), snapshot, axes);
        return new()
        {
            CanonicalSpecification = canonical,
            Hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            RequestedRows = request.RequestedRows,
        };
    }

    private static string Canonicalize(MetricRequest request, IReadOnlyList<SnapshotEntry> snapshot, AnalysisAxes axes)
    {
        var json = new CanonicalWriter();
        json.BeginObject();
        json.Number("specVersion", 1);

        json.BeginArray("snapshotVector");
        foreach (SnapshotEntry entry in snapshot.OrderBy(entry => entry.CaptureId.ToString(), StringComparer.Ordinal))
        {
            json.BeginObject();
            json.Token("captureId", entry.CaptureId.ToString());
            json.Number("generation", entry.Generation);
            json.Token("manifest", entry.ManifestDigest);
            json.EndObject();
        }

        json.EndArray();

        // The version axes this answer depends on, in §24's order, and only those: a total that binds no record to a
        // process does not depend on the binding rule, so naming it would split one query into two.
        bool bindsProcesses = request.Focus is not null
            || request.Grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable or LaneGrouping.Peer;
        json.BeginObject("versions");
        json.Number("normalizerContract", axes.NormalizerContract);
        if (bindsProcesses)
        {
            json.Token("entityRevision", axes.EntityRevision);
        }

        if (UsesRelations(request))
        {
            json.Token("correlationRevision", axes.CorrelationRevision);
        }

        json.Token("metricsContract", axes.MetricsContract);
        json.EndObject();

        json.Token("basis", request.Basis.ToString());
        json.Token("metric", request.Metric.ToString());
        if (request.RateNumerator is { } numerator)
        {
            json.Token("rateNumerator", numerator.ToString());
        }

        if (request.ByteDomain is { } domain)
        {
            json.Token("byteDomain", domain.ToString());
        }

        if (request.AccountingSide is { } side)
        {
            json.Token("accountingSide", side.ToString());
        }

        if (bindsProcesses)
        {
            json.Token("evidencePolicy", request.EvidencePolicy.ToString());
        }

        json.BeginObject("timeScope");
        json.Token("kind", request.TimeScope.ToString());
        if (request.Interval is { } interval)
        {
            json.Token("startTicks", interval.StartTicks.ToString(CultureInfo.InvariantCulture));
            json.Token("endTicks", interval.EndTicks.ToString(CultureInfo.InvariantCulture));
        }

        json.EndObject();
        if (request.Grouping is { } grouping)
        {
            json.Token("grouping", grouping.ToString());
        }

        WriteFilter(json, request);
        json.EndObject();
        return json.ToString();
    }

    /// <summary>
    /// The filter as AND terms sorted by `EN-FilterDimension` code: the process term (2), mechanism (4), layer (5).
    /// A peer is a member of the process term, because it is relative to its focus and never an independent term.
    /// </summary>
    private static void WriteFilter(CanonicalWriter json, MetricRequest request)
    {
        if (request.Focus is null && request.Mechanism is null && request.Layer is null)
        {
            return;
        }

        json.BeginObject("filter");
        json.BeginArray("and");
        if (request.Focus is { } focus)
        {
            json.BeginObject();
            json.Token("facet", nameof(FilterDimension.ProcessInstance));
            json.BeginArray(focus.Role switch
            {
                ProcessRole.Owner => "owner",
                ProcessRole.Participant => "participant",
                ProcessRole.Sender => "sender",
                _ => "receiver",
            });
            json.Token(focus.Instance.ToString());
            json.EndArray();
            if (request.Peer is { } peer)
            {
                json.BeginArray("peer");
                json.Token(peer.ToString());
                json.EndArray();
            }

            json.EndObject();
        }

        if (request.Mechanism is { } mechanism)
        {
            Include(json, FilterDimension.Mechanism, mechanism.ToString());
        }

        if (request.Layer is { } layer)
        {
            Include(json, FilterDimension.Layer, layer.ToString());
        }

        json.EndArray();
        json.EndObject();
    }

    private static void Include(CanonicalWriter json, FilterDimension dimension, string value)
    {
        json.BeginObject();
        json.Token("facet", dimension.ToString());
        json.BeginArray("include");
        json.Token(value);
        json.EndArray();
        json.EndObject();
    }

    /// <summary>
    /// Whether answering needs records' other ends: any focus but a bare owner, a peer narrowing, a peer grouping, a
    /// count of peers, or a directional total grouped by process, whose records belong to their sender or receiver.
    /// When it does, the relation rule is one of the result's version axes.
    /// </summary>
    internal static bool UsesRelations(MetricRequest request)
    {
        Metric effective = request.Metric == Metric.Rate && request.RateNumerator is { } numerator ? numerator : request.Metric;
        return request.Focus is { Role: not ProcessRole.Owner }
            || request.Peer is not null
            || request.Grouping == LaneGrouping.Peer
            || effective == Metric.ActivePeers
            || (request.Grouping is LaneGrouping.InstanceOnly or LaneGrouping.Executable
                && effective is Metric.BytesSent or Metric.BytesReceived);
    }

    /// <summary>
    /// A minimal JSON writer for the canonical form: no whitespace, members in the order they are written, and only
    /// tokens that need no escaping. A token outside that alphabet is a defect in the caller, so it is refused.
    /// </summary>
    private sealed class CanonicalWriter
    {
        private readonly StringBuilder text = new();
        private readonly Stack<bool> first = new();

        public void BeginObject(string? name = null) => Open(name, '{');

        public void EndObject() => Close('}');

        public void BeginArray(string? name = null) => Open(name, '[');

        public void EndArray() => Close(']');

        public void Token(string name, string value)
        {
            Member(name);
            Quoted(value);
        }

        public void Token(string value)
        {
            Separate();
            Quoted(value);
        }

        public void Number(string name, long value)
        {
            Member(name);
            text.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public override string ToString() => text.ToString();

        private void Open(string? name, char bracket)
        {
            if (name is null)
            {
                Separate();
            }
            else
            {
                Member(name);
            }

            text.Append(bracket);
            first.Push(true);
        }

        private void Close(char bracket)
        {
            first.Pop();
            text.Append(bracket);
        }

        private void Member(string name)
        {
            Separate();
            Quoted(name);
            text.Append(':');
        }

        private void Separate()
        {
            if (first.Count == 0)
            {
                return;
            }

            if (!first.Pop())
            {
                text.Append(',');
            }

            first.Push(false);
        }

        private void Quoted(string value)
        {
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character is not (':' or '-'))
                {
                    throw new InvalidOperationException(
                        $"'{value}' holds a character the canonical form does not carry without escaping.");
                }
            }

            text.Append('"').Append(value).Append('"');
        }
    }
}
