using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>Re-deriving after an interval release (ADR-043): refused while it keeps rows whose records it gave up.</summary>
public sealed class IntervalRederivationTests
{
    private static readonly DateTimeOffset Committed = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Fact(DisplayName = "I15: an interval release that kept rows of the records it gave up is not re-derived, by itself or a later generation")]
    public void AnIntervalReleaseThatKeptRowsIsNotReDerived()
    {
        SessionManifestV1 kept = Released(new ReleasedInterval(2_500_000_000, 40, 3), generation: 4);
        JournalRederivationReadiness refused = JournalRederivation.Assess(kept);
        Assert.False(refused.CanAttempt);
        Assert.StartsWith(
            "Generation 4 released the records read before 2.500 s and kept 3 of their rows as the identity evidence of the "
            + "processes and connections after it, which it still holds.",
            refused.Explanation,
            StringComparison.Ordinal);
        Assert.Contains("ADR-024", refused.Explanation, StringComparison.Ordinal);

        // A later generation carries the release and says which generation holds its rows.
        SessionManifestV1 later = Manifest(5, retention: null, [new GenerationRelease(4, kept.Retention!)]);
        Assert.Contains("which generation 5 still holds", JournalRederivation.Assess(later).Explanation, StringComparison.Ordinal);

        // One that kept none left only rows a replay rebuilds, so the release itself refuses nothing.
        JournalRederivationReadiness clean = JournalRederivation.Assess(Released(new ReleasedInterval(2_500_000_000, 43, 0), generation: 4));
        Assert.DoesNotContain("released the records", clean.Explanation, StringComparison.Ordinal);
    }

    private static SessionManifestV1 Released(ReleasedInterval interval, long generation) => Manifest(
        generation,
        new RetentionRecord(RetentionExtentKind.Interval, Committed, "rolling window", ["journal-0000000001.icatj"], 1_024, 43, Digest)
        {
            Interval = interval,
        },
        null);

    private static SessionManifestV1 Manifest(long generation, RetentionRecord? retention, IReadOnlyList<GenerationRelease>? earlier) =>
        SessionManifestV1.Create(
            generation,
            Guid.Parse("0b1c2d3e-4f50-4617-8829-3a4b5c6d7e8f"),
            Committed,
            "interval-rederivation-tests",
            generation - 1,
            new CommittedBoundary("journal-0000000003.icatj", 2_048, 12, Digest),
            [new StoreDependency("journal-0000000003.icatj", StoreDependencyKind.Journal, 2_048, Digest)],
            retention,
            earlier);
}
