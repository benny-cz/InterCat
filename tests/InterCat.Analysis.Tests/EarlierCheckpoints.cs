using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis.Tests;

/// <summary>Checkpoints as an earlier build wrote them, for tests of how a later build reads them.</summary>
internal static class EarlierCheckpoints
{
    /// <summary>The bytes one incarnation's transfers take in format 1.4: whether a sum was refused, and six counts.</summary>
    private const int TransferBytes = 1 + (6 * 8);

    /// <summary>
    /// A checkpoint as revisions 738 to 989 wrote it: format 1.5, which keeps no record's package. Format 1.6 keeps each
    /// among its record's process fields, marked by their present bit 64 and written after the others, and among the fields
    /// each record carried; 1.5 is those bytes with neither, and without a record whose package was all it carried. A
    /// checkpoint of an earlier format is returned as it is.
    /// </summary>
    public static byte[] WithoutPackages(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (BitConverter.ToUInt16(bytes, 10) < 6)
        {
            return [.. bytes];
        }

        // The header and the covered files: the magic, the version, the two rules, the session, the generation, the clock, the
        // host, the capture and the normalizer, then each file's role, name, length and digest.
        int at = 12;
        at += 1 + bytes[at];
        at += 1 + bytes[at];
        at += 16 + 8 + 16 + 16 + 16 + 4;
        uint files = BitConverter.ToUInt32(bytes, at);
        at += 4;
        for (uint file = 0; file < files; file++)
        {
            at += 1;
            at += 1 + bytes[at];
            at += 8;
            at += 1 + bytes[at];
        }

        // The instances: lifecycle records without an owner, then each lifecycle record - its PID, kind, position, flags, exit
        // code and name - all of which every format keeps.
        at += 8;
        uint lifecycle = BitConverter.ToUInt32(bytes, at);
        at += 4;
        for (uint row = 0; row < lifecycle; row++)
        {
            at += 4 + 1 + 40;
            byte flags = bytes[at];
            at += 1 + ((flags & 1) != 0 ? 8 : 0);
            if ((flags & 2) != 0)
            {
                at += 4 + checked((int)BitConverter.ToUInt32(bytes, at));
            }
        }

        var earlier = new List<byte>(bytes.AsSpan(0, at).ToArray());
        uint fields = BitConverter.ToUInt32(bytes, at);
        at += 4;
        var kept = new List<byte>();
        uint keptFields = 0;
        for (uint entry = 0; entry < fields; entry++)
        {
            int address = at;
            byte present = bytes[at + 32];
            at += 32 + 1;
            int values = ((present & 1) != 0 ? 8 : 0) + ((present & 2) != 0 ? 8 : 0) + ((present & 4) != 0 ? 4 : 0)
                + ((present & 8) != 0 ? 8 : 0) + ((present & 16) != 0 ? 4 : 0) + ((present & 32) != 0 ? 8 : 0);
            int valuesAt = at;
            at += values;
            if ((present & 64) != 0)
            {
                at += 4 + checked((int)BitConverter.ToUInt32(bytes, at));
            }

            if ((present & 63) == 0)
            {
                continue;
            }

            kept.AddRange(bytes.AsSpan(address, 32).ToArray());
            kept.Add((byte)(present & 63));
            kept.AddRange(bytes.AsSpan(valuesAt, values).ToArray());
            keptFields++;
        }

        earlier.AddRange(BitConverter.GetBytes(keptFields));
        earlier.AddRange(kept);
        uint seen = BitConverter.ToUInt32(bytes, at);
        at += 4;
        var carried = new List<byte>();
        uint carriedFields = 0;
        for (uint entry = 0; entry < seen; entry++, at += 32 + 2)
        {
            if (BitConverter.ToUInt16(bytes, at + 32) != (ushort)SourceField.ProcessPackageFullName)
            {
                carried.AddRange(bytes.AsSpan(at, 32 + 2).ToArray());
                carriedFields++;
            }
        }

        earlier.AddRange(BitConverter.GetBytes(carriedFields));
        earlier.AddRange(carried);
        earlier.AddRange(bytes.AsSpan(at).ToArray());
        byte[] result = [.. earlier];
        result[10] = 5;
        return result;
    }

    /// <summary>
    /// A checkpoint as revisions 736 and 737 wrote it: format 1.4, which keeps no records by owner. Format 1.5 keeps them
    /// last, ending with how many bytes they take, so 1.4 is those bytes without them; a checkpoint of an earlier format is
    /// returned as it is.
    /// </summary>
    public static byte[] WithoutOwners(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bytes = WithoutPackages(bytes);
        if (BitConverter.ToUInt16(bytes, 10) < 5)
        {
            return [.. bytes];
        }

        long length = BitConverter.ToUInt32(bytes, bytes.Length - 4);
        byte[] earlier = [.. bytes.AsSpan(0, bytes.Length - 4 - checked((int)length))];
        earlier[10] = 4;
        return earlier;
    }

    /// <summary>
    /// A checkpoint as revisions 573 to 735 wrote it: format 1.3, which counts no incarnation's transfers. Format 1.4 keeps
    /// them last, ending with how many there are, so 1.3 is those bytes without them; a checkpoint of an earlier format is
    /// returned as it is.
    /// </summary>
    public static byte[] WithoutTransfers(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bytes = WithoutOwners(bytes);
        if (BitConverter.ToUInt16(bytes, 10) < 4)
        {
            return [.. bytes];
        }

        long entries = BitConverter.ToUInt32(bytes, bytes.Length - 4);
        byte[] earlier = [.. bytes.AsSpan(0, bytes.Length - 4 - checked((int)(entries * TransferBytes)))];
        earlier[10] = 3;
        return earlier;
    }

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

        byte[] earlier = WithoutTransfers(bytes);
        earlier[10] = 1;
        earlier[at + rule.Length - 1] = (byte)'3';
        return earlier;
    }

    /// <summary>
    /// A checkpoint as an earlier binding rule wrote it: `process-binding-v2`, as revisions 166 to 176 did,
    /// `process-binding-v3`, as revisions 177 to 237 did, `process-binding-v4`, as revisions 238 to 913 did,
    /// `process-binding-v5`, as revisions 914 to 938 did, `process-binding-v6`, as revisions 939 to 959 did, or
    /// `process-binding-v7`, as revisions 960 to 977 did. Each differs from one under v8 only in that word when every record
    /// names its owner: each bound fewer records that named none, and v8 binds more of only such records.
    /// </summary>
    public static byte[] UnderEarlierBindingRule(byte[] bytes, int version = 2)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 7);
        byte[] rule = System.Text.Encoding.ASCII.GetBytes(ProcessInstanceIndex.BindingRule);
        int at = bytes.AsSpan().IndexOf(rule);
        if (at < 0 || !ProcessInstanceIndex.BindingRule.EndsWith("-v8", StringComparison.Ordinal))
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
        bytes = WithoutTransfers(bytes);
        int pids = observations.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(segment.Row)
                .Select(row => RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId)))
            .OfType<int>().Distinct().Count();
        int counts = Enumerable.Range(0, processes.Instances.Count)
            .Sum(instance => activity.MechanismsOf(instance, EvidencePolicy.AllIncludingConflicting).Count);
        int rule = 1 + System.Text.Encoding.UTF8.GetByteCount(ProcessActivityIndex.CountRule);
        byte[] older = [.. bytes.AsSpan(0, bytes.Length - (rule + 8 + 4 + (28 * pids) + 4 + (22 * counts)))];
        older[10] = 0;
        return older;
    }
}
