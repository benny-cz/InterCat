using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A live capture publishes a generation per chunk. Its HTTP exchanges are extended from the previous generation's rather
/// than grouped again from every segment, and an extension must equal a full grouping of the same segments exactly, or
/// decline so a full grouping answers (I14, ADR-037).
/// </summary>
public sealed class IncrementalHttpTests
{
    [Theory(DisplayName = "I14: HTTP exchanges extended chunk by chunk equal a grouping of every chunk at once")]
    [InlineData(5, false)]
    [InlineData(41, false)]
    [InlineData(20_261_022, false)]
    [InlineData(13, true)]
    [InlineData(20_261_023, true)]
    public void ExtendingExchangesEqualsGroupingAgain(int seed, bool sparse)
    {
        var random = new Random(seed);
        int extended = 0;
        int declined = 0;
        for (int trial = 0; trial < 12; trial++)
        {
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = RandomHttpCaptures.Chunks(random, sparse);
            var sessions = new List<TemporarySession>();
            try
            {
                var observations = new List<SegmentReaderV1>();
                var fields = new List<SegmentReaderV1>();
                HttpExchangeIndex? exchanges = null;
                foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fieldRows) in chunks)
                {
                    var session = new TemporarySession();
                    sessions.Add(session);
                    Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1), fields: fieldRows);
                    (SegmentReaderV1[] chunkObservations, SegmentReaderV1[] chunkFields) = IncrementalDerivationTests.SegmentsOf(session.Store);
                    observations.AddRange(chunkObservations);
                    fields.AddRange(chunkFields);

                    ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
                    HttpExchangeIndex full = HttpExchangeIndex.Derive(observations, fields, processes, TestClock);
                    if (exchanges is null)
                    {
                        exchanges = full;
                        continue;
                    }

                    HttpExchangeIndex? next = exchanges.Extend(observations, fields, processes);
                    if (next is null)
                    {
                        declined++;
                        next = full;
                    }
                    else
                    {
                        extended++;
                    }

                    AssertSameExchanges(full, next);
                    exchanges = next;
                }
            }
            finally
            {
                foreach (TemporarySession session in sessions)
                {
                    session.Dispose();
                }
            }
        }

        // Every chunk extends but where a buffer arrives long after its number was last read, which in a sparse capture
        // some do: those are grouped again in full.
        Assert.True(extended > 3 * declined, $"{extended} extensions and {declined} declined.");
        Assert.True(sparse ? declined > 0 : declined == 0, $"{declined} declined.");
    }

    [Fact(DisplayName = "I14: an HTTP extension declines, for a full grouping, whatever could change an exchange it cannot regroup")]
    public void HttpExtensionDeclinesWhatItCannotAdd()
    {
        // Exchange 1 of process 4242, whole and ended at the first second; then exchange 2, still open twenty seconds on.
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] first =
        [
            Buffer(10_000_000, 2001, 4_242, 1, 0, 3, 11),
            Buffer(10_000_010, 2003, 4_242, 1, 0, 3, 12),
            Buffer(10_000_020, 2004, 4_242, 1, 0, 3, 13),
            Buffer(200_000_000, 2001, 4_242, 2, 0, 3, 14),
        ];

        // A buffer of the ended exchange, read past the late window: its number is no longer kept, so the exchanges are
        // grouped again in full.
        Assert.Null(Extended(first, [Buffer(10_000_030, 2002, 4_242, 1, 0, 2, 20)]));

        // A buffer of the open exchange, read as late: its number is kept while its response is open, so it extends, and
        // a new number extends too.
        HttpExchangeIndex? open = Extended(first,
            [Buffer(200_000_010, 2003, 4_242, 2, 0, 3, 21), Buffer(200_000_020, 2001, 4_242, 3, 0, 3, 22)]);
        Assert.NotNull(open);
        Assert.Equal(3, Assert.Single(open.Groups).Exchanges);

        // A number used twice, whose second use began at the second second: a buffer read before that use began could
        // belong to the first use, and declines.
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] reused =
        [
            Buffer(10_000_000, 2001, 4_242, 7, 0, 3, 31),
            Buffer(20_000_000, 2001, 4_242, 7, 0, 3, 32),
        ];
        Assert.Null(Extended(reused, [Buffer(15_000_000, 2003, 4_242, 7, 0, 3, 33)]));
        Assert.NotNull(Extended(reused, [Buffer(25_000_000, 2003, 4_242, 7, 0, 3, 34)]));

        // A source field naming a record read before: it could move a buffer these grouped.
        (ObservationRowV1 row, SourceFieldRowV1[] _) = first[1];
        Assert.Null(Extended(first, [Buffer(200_000_030, 2003, 4_242, 2, 0, 3, 23)],
            [Field(row, SourceField.ContentBufferFlags, 1)]));
    }

    [Fact(DisplayName = "I14: an HTTP extension declines segments that do not begin with those it grouped")]
    public void HttpExtensionDeclinesOtherSegments()
    {
        using var earlier = new TemporarySession();
        using var later = new TemporarySession();
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] records =
            [Buffer(10_000_000, 2001, 4_242, 1, 0, 3, 11), Buffer(10_000_010, 2003, 4_242, 1, 0, 3, 12)];
        Publish(earlier.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1), .. records.Select(record => record.Row)],
            fields: [.. records.SelectMany(record => record.Fields)]);
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] otherRecords =
            [Buffer(10_000_000, 2001, 4_242, 1, 0, 3, 11), Buffer(10_000_020, 2003, 4_242, 1, 0, 3, 12)];
        Publish(later.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1), .. otherRecords.Select(record => record.Row)],
            fields: [.. otherRecords.SelectMany(record => record.Fields)]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(earlier.Store);
        (SegmentReaderV1[] others, SegmentReaderV1[] otherFields) = IncrementalDerivationTests.SegmentsOf(later.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        HttpExchangeIndex grouped = HttpExchangeIndex.Derive(observations, fields, processes, TestClock);

        // Another session's segments, of other rows, are other files; and a derivation without its clock keeps nothing.
        Assert.Null(grouped.Extend(others, otherFields, ProcessInstanceIndex.Derive(others, TestClock, otherFields)));
        Assert.Null(HttpExchangeIndex.Derive(observations, fields, processes).Extend(observations, fields, processes));
        Assert.NotNull(grouped.Extend(observations, fields, processes));
    }

    /// <summary>
    /// The exchanges of <paramref name="first"/> extended by <paramref name="later"/>, each a chunk of its own, after a full
    /// grouping of both has been checked against the extension where there is one.
    /// </summary>
    private static HttpExchangeIndex? Extended(
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] first,
        (ObservationRowV1 Row, SourceFieldRowV1[] Fields)[] later,
        SourceFieldRowV1[]? laterFields = null)
    {
        using var earlier = new TemporarySession();
        using var next = new TemporarySession();
        Publish(earlier.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1), .. first.Select(record => record.Row)],
            fields: [.. first.SelectMany(record => record.Fields)]);
        Publish(next.Store, [.. later.Select(record => record.Row)],
            fields: [.. later.SelectMany(record => record.Fields), .. laterFields ?? []]);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = IncrementalDerivationTests.SegmentsOf(earlier.Store);
        (SegmentReaderV1[] added, SegmentReaderV1[] addedFields) = IncrementalDerivationTests.SegmentsOf(next.Store);
        SegmentReaderV1[] all = [.. observations, .. added];
        SegmentReaderV1[] allFields = [.. fields, .. addedFields];
        HttpExchangeIndex grouped = HttpExchangeIndex.Derive(observations, fields,
            ProcessInstanceIndex.Derive(observations, TestClock, fields), TestClock);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(all, TestClock, allFields);
        HttpExchangeIndex? extended = grouped.Extend(all, allFields, processes);
        if (extended is not null)
        {
            AssertSameExchanges(HttpExchangeIndex.Derive(all, allFields, processes, TestClock), extended);
        }

        return extended;
    }

    /// <summary>One WinINet buffer record of process <paramref name="processId"/> and its three source fields.</summary>
    private static (ObservationRowV1 Row, SourceFieldRowV1[] Fields) Buffer(
        long ticks, ushort eventId, int processId, long number, long sequence, long flags, ulong ordinal)
    {
        ObservationRowV1 row = RandomHttpCaptures.HttpBuffer(ticks, eventId, processId, ordinal, 100);
        return (row,
        [
            Field(row, SourceField.HttpExchangeId, number),
            Field(row, SourceField.ContentBufferSequence, sequence),
            Field(row, SourceField.ContentBufferFlags, flags),
        ]);
    }

    internal static void AssertSameExchanges(HttpExchangeIndex expected, HttpExchangeIndex actual)
    {
        Assert.Equal(expected.WithoutExchange, actual.WithoutExchange);
        Assert.Equal(expected.SegmentNames, actual.SegmentNames);
        Assert.Equal(expected.Groups, actual.Groups);
        foreach (HttpExchangeGroup group in expected.Groups)
        {
            Assert.Equal(expected.ExchangesOf(group), actual.ExchangesOf(group));
            for (int position = 0; position < group.Exchanges; position++)
            {
                Assert.Equal(expected.RecordsOf(group, position), actual.RecordsOf(group, position));
            }
        }
    }
}

/// <summary>
/// Random WinINet captures of three processes' exchanges, split into the chunks a live capture publishes: each buffer in
/// the chunk of its reading, or a chunk or two late, with its source fields beside it.
/// </summary>
internal static class RandomHttpCaptures
{
    private static readonly int[] Clients = [4_242, 5_000, 6_100];

    /// <summary>
    /// A dense capture places exchanges tens of ticks apart, all within the late window; a sparse one three seconds apart,
    /// numbers sometimes used again as WinINet loaded again would, and some buffers delivered long after the window.
    /// </summary>
    public static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Chunks(Random random, bool sparse)
    {
        ulong ordinal = 0;
        var records = new List<(ObservationRowV1 Row, SourceFieldRowV1[] Fields)>();
        foreach (int client in Clients)
        {
            records.Add((Lifecycle(1, ObservationKind.Create, client, ++ordinal), []));
        }

        long spacing = sparse ? 30_000_000 : 50;
        var numbers = Clients.ToDictionary(client => client, _ => 0L);
        int exchanges = random.Next(6, 24);
        for (int exchange = 0; exchange < exchanges; exchange++)
        {
            long at = 100 + (exchange * spacing) + random.Next(0, 30);
            int client = Clients[random.Next(Clients.Length)];
            long number = sparse && numbers[client] > 0 && random.NextDouble() < 0.05
                ? random.Next(1, (int)numbers[client] + 1)
                : ++numbers[client];

            // A request head, sometimes a request body, a response head and a response body; a buffer is sometimes lost,
            // and a request body's last buffer sometimes comes after the response, as WinINet raises it.
            var parts = new List<(ushort Event, int Buffers)> { (2001, 1 + random.Next(2)) };
            if (random.NextDouble() < 0.4)
            {
                parts.Add((2002, 1 + random.Next(3)));
            }

            parts.Add((2003, 1));
            parts.Add((2004, 1 + random.Next(4)));
            bool bodyLast = random.NextDouble() < 0.3;
            long ticks = at;
            (ObservationRowV1 Row, SourceFieldRowV1[] Fields)? held = null;
            foreach ((ushort part, int buffers) in parts)
            {
                for (int sequence = 0; sequence < buffers; sequence++)
                {
                    if (random.NextDouble() < 0.04)
                    {
                        continue;
                    }

                    long flags = (sequence == 0 ? 1 : 0) | (sequence == buffers - 1 ? 2 : 0);
                    ObservationRowV1 row = HttpBuffer(ticks, part, client, ++ordinal, random.Next(0, 2_000));
                    ticks += 1 + random.Next(3);
                    (ObservationRowV1 Row, SourceFieldRowV1[] Fields) record = (row,
                    [
                        Field(row, SourceField.HttpExchangeId, number),
                        Field(row, SourceField.ContentBufferSequence, sequence),
                        Field(row, SourceField.ContentBufferFlags, flags),
                    ]);
                    if (bodyLast && part == 2002 && sequence == buffers - 1)
                    {
                        held = record;
                        continue;
                    }

                    records.Add(record);
                }
            }

            if (held is { } last)
            {
                ObservationRowV1 moved = last.Row with { NativeTicks = ticks + 2, SessionRelativeTicks = (ticks + 2) * 100 };
                records.Add((moved, [.. last.Fields.Select(field => field with { NativeTicks = moved.NativeTicks })]));
            }

            // A WinINet record that names no exchange belongs to none.
            if (random.NextDouble() < 0.05)
            {
                records.Add((HttpBuffer(ticks + 3, 2003, client, ++ordinal, 7), []));
            }
        }

        // Each record is delivered in the chunk of its reading, or a chunk or two late; in a sparse capture, some long
        // after the late window.
        long span = Math.Max(1, ((exchanges * spacing) + 200) / random.Next(3, 9));
        var chunks = new SortedDictionary<long, (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields)>();
        foreach ((ObservationRowV1 row, SourceFieldRowV1[] fields) in records)
        {
            double draw = random.NextDouble();
            long delivered = row.NativeTicks
                + (draw < 0.15 ? span * random.Next(1, 3) : 0)
                + (sparse && draw > 0.97 ? 200_000_000 : 0);
            long chunk = delivered / span;
            if (!chunks.TryGetValue(chunk, out (List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields) chunkHeld))
            {
                chunkHeld = ([], []);
                chunks[chunk] = chunkHeld;
            }

            chunkHeld.Rows.Add(row);
            chunkHeld.Fields.AddRange(fields);
        }

        return [.. chunks.Values.Select(chunk => (chunk.Rows.ToArray(), chunk.Fields.ToArray()))];
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by <paramref name="processId"/>, with a session time.</summary>
    public static ObservationRowV1 HttpBuffer(long ticks, ushort eventId, int processId, ulong ordinal, long bytes) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = processId,
            HeaderThreadId = processId + 1,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
            SessionRelativeTicks = ticks * 100,
        };
}
