using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis.Tests;

/// <summary>Checkpoints as an earlier build wrote them, for tests of how a later build reads them.</summary>
internal static class EarlierCheckpoints
{
    /// <summary>
    /// A checkpoint as revisions 166 to 172 wrote it: format 1.1, under `transport-endpoint-relation-v3`. It differs from
    /// format 1.2 under v4 only in those two words when no end is IPv6, which 1.1 cannot hold.
    /// </summary>
    public static byte[] UnderEarlierRelationRule(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[] rule = System.Text.Encoding.ASCII.GetBytes(TransportRelationIndex.RelationRule);
        int at = bytes.AsSpan().IndexOf(rule);
        if (at < 0 || !TransportRelationIndex.RelationRule.EndsWith("-v4", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The checkpoint does not name the relation rule this helper rewrites.");
        }

        byte[] earlier = [.. bytes];
        earlier[10] = 1;
        earlier[at + rule.Length - 1] = (byte)'3';
        return earlier;
    }

    /// <summary>
    /// A checkpoint as an earlier binding rule wrote it: `process-binding-v2`, as revisions 166 to 176 did, or
    /// `process-binding-v3`, as revisions 177 to 237 did. Either differs from one under v4 only in that word when every
    /// record names its owner: each bound fewer records that named none, and v4 binds more of only such records.
    /// </summary>
    public static byte[] UnderEarlierBindingRule(byte[] bytes, int version = 2)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 3);
        byte[] rule = System.Text.Encoding.ASCII.GetBytes(ProcessInstanceIndex.BindingRule);
        int at = bytes.AsSpan().IndexOf(rule);
        if (at < 0 || !ProcessInstanceIndex.BindingRule.EndsWith("-v4", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The checkpoint does not name the binding rule this helper rewrites.");
        }

        byte[] earlier = [.. bytes];
        earlier[at + rule.Length - 1] = (byte)('0' + version);
        return earlier;
    }

    /// <summary>
    /// A checkpoint as revisions 162 to 165 wrote it: format 1.0, ending before the counts section, which is the last one
    /// (`contracts/derivation-checkpoint-v1.md` §3). <paramref name="activity"/> is what <paramref name="bytes"/> holds.
    /// </summary>
    public static byte[] MinorZero(
        byte[] bytes,
        IReadOnlyList<SegmentReaderV1> observations,
        ProcessInstanceIndex processes,
        ProcessActivityIndex activity)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(activity);
        int pids = observations.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(segment.Row)
                .Select(row => RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.HeaderProcessId)))
            .OfType<int>().Distinct().Count();
        int counts = Enumerable.Range(0, processes.Instances.Count)
            .Sum(instance => activity.MechanismsOf(instance, EvidencePolicy.AllIncludingConflicting).Count);
        int rule = 1 + System.Text.Encoding.UTF8.GetByteCount(ProcessActivityIndex.CountRule);
        byte[] older = [.. bytes.AsSpan(0, bytes.Length - (rule + 8 + 4 + (28 * pids) + 4 + (22 * counts)))];
        older[10] = 0;
        return older;
    }
}
