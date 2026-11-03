using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A pass over a session's segments reads them side by side, as many as the machine has processors, but only while the
/// columns of the segments being read fit its budget, so a pass over a compacted session's large segments holds a few
/// of them at once rather than one a processor (S2, revision 883). That they answer what a count of every row answers,
/// side by side, is <see cref="SegmentPassTests"/>'.
/// </summary>
public sealed class SegmentPassBudgetTests
{
    [Fact(DisplayName = "S2: a pass reads segments side by side only while their columns fit its budget, one at a time where one does not fit, and counts what a serial pass counts")]
    public void APassReadsSideBySideWithinItsBudget()
    {
        const int Rows = 4_000;
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, Rows).Select(row => Lifecycle(row + 1, ObservationKind.Inventory, 400 + (row % 7), (ulong)row + 1))],
            rowsPerSegment: 500);
        SessionManifestV1 manifest = session.Store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store.Root, manifest, name))];
        Assert.Equal(8, segments.Length);
        long each = segments.Max(SegmentPasses.ColumnBytes);
        Assert.True(each > 0);

        // A budget no segment fits reads one at a time; one two and a half segments fit reads two at most; one every
        // segment fits many times over reads as many as the machine has processors.
        foreach ((long budget, int most) in new[] { (1L, 1), ((each * 5) / 2, 2), (each * 100, segments.Length) })
        {
            int reading = 0;
            int widest = 0;
            long counted = 0;
            SegmentPasses.Run(
                segments,
                () => new long[1],
                (segment, tally) =>
                {
                    int now = Interlocked.Increment(ref reading);
                    for (int seen = Volatile.Read(ref widest); now > seen; seen = Volatile.Read(ref widest))
                    {
                        _ = Interlocked.CompareExchange(ref widest, now, seen);
                    }

                    // Where two fit, the first waits a while for a second beside it, so a busy machine's late worker still
                    // shows them side by side; then each is held long enough for the others to ask for room beside it.
                    if (most > 1 && Environment.ProcessorCount > 1)
                    {
                        _ = SpinWait.SpinUntil(() => Volatile.Read(ref reading) > 1, TimeSpan.FromSeconds(5));
                    }

                    Thread.Sleep(40);
                    tally[0] += segment.RowCount;
                    _ = Interlocked.Decrement(ref reading);
                },
                tally => counted += tally[0],
                CancellationToken.None,
                budget);

            Assert.Equal(Rows, counted);
            Assert.True(widest <= Math.Min(most, Environment.ProcessorCount), $"{widest} segments were read at once under {budget:N0} bytes.");
            if (most > 1 && Environment.ProcessorCount > 1)
            {
                Assert.True(widest > 1, $"No two segments were read at once under {budget:N0} bytes.");
            }
        }
    }
}
