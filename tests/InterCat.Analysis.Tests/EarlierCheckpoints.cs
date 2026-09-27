using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis.Tests;

/// <summary>Checkpoints as an earlier build wrote them, for tests of how a later build reads them.</summary>
internal static class EarlierCheckpoints
{
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
                .Select(row => segment.Slice(SegmentColumnId.OwnerProcessId).SignedAt(row)))
            .OfType<long>().Distinct().Count();
        int counts = Enumerable.Range(0, processes.Instances.Count)
            .Sum(instance => activity.MechanismsOf(instance, EvidencePolicy.AllIncludingConflicting).Count);
        int rule = 1 + System.Text.Encoding.UTF8.GetByteCount(ProcessActivityIndex.CountRule);
        byte[] older = [.. bytes.AsSpan(0, bytes.Length - (rule + 8 + 4 + (28 * pids) + 4 + (22 * counts)))];
        older[10] = 0;
        return older;
    }
}
