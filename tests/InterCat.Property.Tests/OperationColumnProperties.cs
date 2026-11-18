using System.Globalization;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// §21.1's counts of an interval's operations as a lane's density columns hold them (I20), generated rather than shown on
/// one example: each column's overlap, flagged and untimed counts and its clipped occupancy are compared with a scan of
/// the operations against the column's own interval, which never asks which column a reading falls in. Every case comes
/// from a printed seed, so a failure is reproducible (§13.6).
/// </summary>
public sealed class OperationColumnProperties
{
    private const int Cases = 300;

    /// <summary>Seeds are fixed so a run is deterministic; a failure message prints the case that broke.</summary>
    public static TheoryData<int> Seeds => [1, 7, 19, 4242, 20_261_118];

    [Theory(DisplayName = "I20: over generated operations, each density column's overlap, flagged and untimed counts and clipped occupancy equal a scan of the operations, and the columns hold each one's time inside the interval once")]
    [MemberData(nameof(Seeds))]
    public void ColumnsEqualAScanOfTheOperations(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            // Spans of a few ticks, of ordinary sessions, and past what a 64-bit product of a span and a column count
            // holds - there with one operation at most, so its time stays a tick count.
            bool wide = random.Next(8) == 0;
            long span = wide ? random.NextInt64(1L << 56, 1L << 62)
                : random.Next(4) == 0 ? random.Next(1, 64)
                : random.NextInt64(64, 1_000_000_000_000);
            long start = random.NextInt64(-(1L << 60), 1L << 60);
            var interval = new TimeRange(start, start + span);
            int columns = (int)Math.Min(span, random.Next(1, 80));
            var operations = new List<Operation>();
            for (int count = wide ? random.Next(0, 2) : random.Next(0, 40); operations.Count < count;)
            {
                Operation operation = Operation.Generate(random, interval);
                if (operation.First < interval.EndTicks && operation.Last >= interval.StartTicks)
                {
                    operations.Add(operation);
                }
            }

            var counted = new OperationColumns(interval, columns);
            foreach (Operation operation in operations)
            {
                counted.Add(operation.First, operation.Last, operation.Flagged, operation.Measured);
            }

            (long[] running, long[] flagged, long[] untimed, long[] occupied) =
                (counted.Running(), counted.Flagged(), counted.Untimed(), counted.Occupied());
            string Case(string failure) => string.Create(
                CultureInfo.InvariantCulture,
                $"seed {seed}, case {index}: {failure}. interval [{interval.StartTicks}, {interval.EndTicks}) in {columns}, operations {string.Join("; ", operations)}");
            for (int column = 0; column < columns; column++)
            {
                TimeRange cell = RpcCallDensity.ColumnIntervalOf(interval, columns, column);
                Operation[] inCell = [.. operations.Where(operation => operation.RunsIn(interval, cell))];
                Assert.True(inCell.Length == running[column], Case($"column {column} runs {running[column]}, not {inCell.Length}"));
                Assert.True(inCell.Count(operation => operation.Flagged) == flagged[column], Case($"column {column} flags {flagged[column]}"));
                Assert.True(inCell.Count(operation => operation.Measured is null) == untimed[column], Case($"column {column} leaves {untimed[column]} untimed"));
                Int128 time = operations.Aggregate(Int128.Zero, (sum, operation) => sum + operation.TimeIn(cell));
                Assert.True(time == occupied[column], Case($"column {column} occupies {occupied[column]}, not {time}"));
            }

            Int128 inside = operations.Aggregate(Int128.Zero, (sum, operation) => sum + operation.TimeIn(interval));
            Int128 held = occupied.Aggregate(Int128.Zero, (sum, time) => sum + time);
            Assert.True(inside == held, Case($"the columns hold {held} of the {inside} ticks the operations ran inside the interval"));
        }
    }

    /// <summary>
    /// One operation: its first and last readings, whether it is flagged, and the time it ran, null where no reading timed
    /// it. A last reading before the first is counted in the first's column alone.
    /// </summary>
    private readonly record struct Operation(long First, long Last, bool Flagged, (long Start, long Stop)? Measured)
    {
        public static Operation Generate(Random random, TimeRange interval)
        {
            long reach = Math.Max(1, interval.SpanTicks / 2);
            long At() => random.NextInt64(interval.StartTicks - reach, interval.EndTicks + reach);
            long a = At();
            long b = At();
            bool flagged = random.Next(3) == 0;
            return random.Next(4) switch
            {
                0 => new(Math.Min(a, b), Math.Max(a, b), flagged, (Math.Min(a, b), Math.Max(a, b))),
                1 => new(a, long.MaxValue, flagged, (a, long.MaxValue)),
                2 => new(a, a, flagged, null),
                _ => new(Math.Max(a, b), Math.Min(a, b), flagged, (Math.Max(a, b), Math.Min(a, b))),
            };
        }

        /// <summary>Whether a reading from its first to its last lies in <paramref name="cell"/>, within the interval.</summary>
        public bool RunsIn(TimeRange interval, TimeRange cell)
        {
            long from = Math.Max(First, interval.StartTicks);
            long to = Math.Min(Math.Max(First, Last), interval.EndTicks - 1);
            return from < cell.EndTicks && to >= cell.StartTicks;
        }

        /// <summary>The time it ran inside <paramref name="range"/>: none when no reading timed it.</summary>
        public Int128 TimeIn(TimeRange range) => Measured is { } time
            ? Int128.Max(Int128.Zero, (Int128)Math.Min(time.Stop, range.EndTicks) - Math.Max(time.Start, range.StartTicks))
            : Int128.Zero;

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{First}..{Last}{(Flagged ? " flagged" : string.Empty)} {(Measured is { } time ? $"[{time.Start}, {time.Stop})" : "untimed")}");
    }
}
