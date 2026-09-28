using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>One buffer of a message part: its place, its ends, the record that carries it and what the capture kept of it.</summary>
/// <param name="Sequence">Its place in its part, from 0.</param>
/// <param name="Flags">1 when it is its part's first buffer, 2 when its last.</param>
/// <param name="Record">The record that carries it.</param>
/// <param name="Entry">The content kept of it; null when the session keeps none of it.</param>
public sealed record ContentPartBuffer(long Sequence, long Flags, RawRecordId Record, SessionContentEntry? Entry);

/// <summary>
/// A message part - an HTTP exchange's request or response head or body - as its buffers' records hold it (M8, ADR-037):
/// which buffers were recorded and kept, whether they make the whole part, and, only when they do and a person asks, its
/// bytes in order. A part missing a buffer, or holding one cut, is stated as such and never shown as a whole (I21, P2).
/// </summary>
public sealed record SessionContentPartDetail
{
    /// <summary>Whether the record carries a buffer of a part: an exchange, a place and its ends.</summary>
    public required bool IsPart { get; init; }

    /// <summary>The part, in words: "the response body of exchange 7".</summary>
    public string? Name { get; init; }

    /// <summary>The part's buffers the session holds, in their order.</summary>
    public IReadOnlyList<ContentPartBuffer> Buffers { get; init; } = [];

    /// <summary>Whether every buffer from the one flagged first to the one flagged last was recorded and kept whole.</summary>
    public bool Complete { get; init; }

    /// <summary>What the part holds, or why it is not whole, in words.</summary>
    public required string Statement { get; init; }

    /// <summary>The whole part's bytes, in order; only when it is complete, may be shown, and was asked for.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>The whole part's length when it is complete; null otherwise.</summary>
    public long? Length { get; init; }
}

/// <summary>
/// Finds the part a record's buffer belongs to (M8's first step): the records of the same exchange, the same event and the
/// same raising process, ordered by their place, read under a lease on the current generation. Only a source that keeps a
/// buffer's exchange, place and ends as source fields - WinINet's capture - has parts.
/// <para>
/// An exchange's number is its client process's own count from 1 (ADR-037), so the same number, event and process ID can
/// name two exchanges in one session: a process ID used again by another process, or WinINet loaded again in one. Those
/// buffers are told apart in time - a buffer flagged first, one after a buffer flagged last, or one that numbers from
/// lower than the buffer before it opens another use of the number - and a part is the use its record belongs to, never
/// two merged into one (R22).
/// </para>
/// </summary>
public static class SessionContentPartQuery
{
    /// <summary>The most bytes a whole part is read into memory as; a longer one is stated and not assembled.</summary>
    public const long MaximumPartBytes = 64L * 1024 * 1024;

    public static SessionContentPartDetail Read(
        SessionStore store,
        Guid expectedSessionId,
        ObservationRowV1 row,
        bool revealBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.SessionId != expectedSessionId)
        {
            throw new InvalidOperationException("This directory now holds another session. Reopen it from the "
                + "workspace before inspecting a record's content.");
        }

        var key = (row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal);
        Dictionary<(uint, uint, ulong), PartFields> fields = ReadPartFields(store, manifest, cancellationToken);
        if (!fields.TryGetValue(key, out PartFields own) || own.Exchange is not { } exchange)
        {
            return new() { IsPart = false, Statement = "This record's buffer belongs to no part its source numbers." };
        }

        // The part's other buffers: the same exchange, and - read from their rows - the same event and raising process.
        var sameExchange = fields.Where(pair => pair.Value.Exchange == exchange).Select(pair => pair.Key).ToHashSet();
        var members = new List<(ObservationRowV1 Row, CaptureId Capture)>();
        foreach (string segmentName in SessionSegments.Names(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store, manifest, segmentName);
            SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
            SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
            SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            for (int index = 0; index < segment.RowCount; index++)
            {
                if (!sameExchange.Contains(((uint)streams.UnsignedAt(index)!.Value, (uint)epochs.UnsignedAt(index)!.Value,
                    ordinals.UnsignedAt(index)!.Value)))
                {
                    continue;
                }

                ObservationRowV1 candidate = segment.Row(index);
                if (candidate.EventId == row.EventId && candidate.HeaderProcessId == row.HeaderProcessId
                    && candidate.Mechanism == row.Mechanism)
                {
                    members.Add((candidate, segment.CaptureId));
                }
            }
        }

        SessionContentIndex content = manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content)
            ? SessionDerivationCache.For(manifest).Content(store.Root, cancellationToken)
            : SessionContentIndex.Empty;
        (List<(ObservationRowV1 Row, CaptureId Capture, PartFields Part)> use, int uses) = UseOf(members, fields, key);
        ContentPartBuffer[] buffers =
        [
            .. use
                .OrderBy(member => member.Part.Sequence ?? long.MaxValue)
                .ThenBy(member => member.Row.NativeTicks)
                .Select(member => new ContentPartBuffer(member.Part.Sequence ?? -1, member.Part.Flags ?? 0,
                    new RawRecordId(member.Capture, member.Row.RawStreamId, member.Row.RawSourceEpoch, member.Row.RawRecordOrdinal),
                    content.Find(member.Capture, member.Row))),
        ];

        string name = string.Create(CultureInfo.CurrentCulture, $"the {PartName(row.EventId)} of exchange {exchange:N0}");
        string reused = uses > 1
            ? string.Create(CultureInfo.CurrentCulture,
                $" Exchange {exchange:N0} names {uses:N0} exchanges of this process ID in the session - it was used again, by another process or a restarted client - and this part is the one this record belongs to, in time.")
            : string.Empty;
        string? problem = Problem(buffers);
        long length = buffers.Sum(buffer => (long)(buffer.Entry?.Fragment.Kept ?? 0));
        if (problem is not null)
        {
            return new()
            {
                IsPart = true,
                Name = name,
                Buffers = buffers,
                Complete = false,
                Statement = Capital(name) + " is not whole: " + problem + ". Its buffers are shown one at a time, and "
                    + "nothing stands in for what is missing." + reused,
            };
        }

        string statement = string.Create(CultureInfo.CurrentCulture,
            $"{Capital(name)}: {buffers.Length:N0} {(buffers.Length == 1 ? "buffer" : "buffers")}, from its first to its last, all kept whole - {length:N0} {(length == 1 ? "byte" : "bytes")}.");
        bool inspectable = buffers.All(buffer => buffer.Entry!.Inspectable);
        byte[]? bytes = null;
        if (revealBytes && inspectable && length <= MaximumPartBytes)
        {
            bytes = new byte[length];
            int at = 0;
            foreach (ContentPartBuffer buffer in buffers)
            {
                byte[] kept = SessionContentIndex.ReadBytes(store.Root, buffer.Entry!, buffer.Entry!.Fragment.Kept)!;
                kept.CopyTo(bytes, at);
                at += kept.Length;
            }
        }

        return new()
        {
            IsPart = true,
            Name = name,
            Buffers = buffers,
            Complete = true,
            Length = length,
            Bytes = bytes,
            Statement = statement + (inspectable ? string.Empty
                : " Its bytes are not shown: the capture kept them without consent to inspect them.")
                + (length > MaximumPartBytes
                    ? string.Create(CultureInfo.CurrentCulture, $" It is longer than the {MaximumPartBytes:N0} bytes a part is assembled up to.")
                    : string.Empty)
                + reused,
        };
    }

    /// <summary>
    /// The use of an exchange's number that the record <paramref name="key"/> belongs to, among the buffers of that number,
    /// event and process ID, and how many uses there are. In time order, a buffer opens another use when it is flagged first,
    /// when the use before it already ended with a buffer flagged last, or when its place is not after the place before it.
    /// </summary>
    private static (List<(ObservationRowV1 Row, CaptureId Capture, PartFields Part)> Use, int Uses) UseOf(
        List<(ObservationRowV1 Row, CaptureId Capture)> members,
        Dictionary<(uint, uint, ulong), PartFields> fields,
        (uint, uint, ulong) key)
    {
        var uses = new List<List<(ObservationRowV1 Row, CaptureId Capture, PartFields Part)>>();
        List<(ObservationRowV1 Row, CaptureId Capture, PartFields Part)>? current = null;
        List<(ObservationRowV1 Row, CaptureId Capture, PartFields Part)>? own = null;
        foreach ((ObservationRowV1 row, CaptureId capture) in members
            .OrderBy(member => member.Row.NativeTicks)
            .ThenBy(member => fields[(member.Row.RawStreamId, member.Row.RawSourceEpoch, member.Row.RawRecordOrdinal)].Sequence ?? long.MaxValue)
            .ThenBy(member => member.Row.RawRecordOrdinal))
        {
            var memberKey = (row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal);
            PartFields part = fields[memberKey];
            if (current is not { Count: > 0 } || ((part.Flags ?? 0) & 1) != 0 || ((current[^1].Part.Flags ?? 0) & 2) != 0
                || (part.Sequence ?? long.MaxValue) <= (current[^1].Part.Sequence ?? long.MinValue))
            {
                current = [];
                uses.Add(current);
            }

            current.Add((row, capture, part));
            if (memberKey == key) own = current;
        }

        return (own ?? [], uses.Count);
    }

    /// <summary>Why the buffers are not the whole part, in words; null when they are.</summary>
    private static string? Problem(ContentPartBuffer[] buffers)
    {
        var problems = new List<string>(3);
        if (buffers.Length == 0 || (buffers[0].Flags & 1) == 0 || buffers[0].Sequence != 0)
        {
            problems.Add("its first buffer was not recorded - the capture began after it, or lost it");
        }

        if (buffers.Length == 0 || (buffers[^1].Flags & 2) == 0)
        {
            problems.Add("its last buffer was not recorded - the capture stopped before it, or lost it");
        }

        long[] missing = [.. Enumerable.Range(0, Math.Max(0, buffers.Length - 1))
            .Where(index => buffers[index + 1].Sequence - buffers[index].Sequence > 1)
            .SelectMany(index => LongRange(buffers[index].Sequence + 1, buffers[index + 1].Sequence))];
        if (missing.Length > 0)
        {
            problems.Add(string.Create(CultureInfo.CurrentCulture,
                $"{(missing.Length == 1 ? "buffer" : "buffers")} {string.Join(", ", missing.Take(8))}{(missing.Length > 8 ? " and more" : string.Empty)} {(missing.Length == 1 ? "was" : "were")} not recorded"));
        }

        ContentPartBuffer[] notWhole = [.. buffers.Where(buffer => buffer.Entry?.Fragment.Disposition != ContentDispositionV1.Whole)];
        if (notWhole.Length > 0)
        {
            problems.Add(string.Create(CultureInfo.CurrentCulture,
                $"{(notWhole.Length == 1 ? "buffer" : "buffers")} {string.Join(", ", notWhole.Take(8).Select(buffer => buffer.Sequence))} {(notWhole.Length == 1 ? "was" : "were")} not kept whole"));
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);

        static IEnumerable<long> LongRange(long from, long to)
        {
            for (long value = from; value < to; value++) yield return value;
        }
    }

    /// <summary>The part an HTTP capture event carries, by WinINet's event ids (ADR-037).</summary>
    private static string PartName(ushort eventId) => eventId switch
    {
        2001 => "request head",
        2002 => "request body",
        2003 => "response head",
        2004 => "response body",
        _ => "message part",
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Every record's exchange, place and ends, read from the generation's source fields by column.</summary>
    private static Dictionary<(uint, uint, ulong), PartFields> ReadPartFields(
        SessionStore store, SessionManifestV1 manifest, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<(uint, uint, ulong), PartFields>();
        foreach (string name in SessionSegments.FieldNames(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store, manifest, name);
            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            bool opened = false;
            SegmentColumnSlice streams = default, epochs = default, ordinals = default, values = default;
            for (int index = 0; index < segment.RowCount; index++)
            {
                var field = (SourceField)(ushort)codes.UnsignedAt(index)!.Value;
                if (field is not (SourceField.HttpExchangeId or SourceField.ContentBufferSequence or SourceField.ContentBufferFlags))
                {
                    continue;
                }

                if (!opened)
                {
                    streams = segment.Slice(SegmentColumnId.RawStreamId);
                    epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
                    ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
                    values = segment.Slice(SegmentColumnId.FieldValue);
                    opened = true;
                }

                var key = ((uint)streams.UnsignedAt(index)!.Value, (uint)epochs.UnsignedAt(index)!.Value, ordinals.UnsignedAt(index)!.Value);
                long? value = values.SignedAt(index);
                PartFields known = fields.GetValueOrDefault(key);
                fields[key] = field switch
                {
                    SourceField.HttpExchangeId => known with { Exchange = value },
                    SourceField.ContentBufferSequence => known with { Sequence = value },
                    _ => known with { Flags = value },
                };
            }
        }

        return fields;
    }

    private readonly record struct PartFields(long? Exchange, long? Sequence, long? Flags);
}
