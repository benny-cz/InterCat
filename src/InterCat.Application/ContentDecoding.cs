using System.Diagnostics;
using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A field a decoder read from a message: its name, its value in words, and the bytes of the message it came from.</summary>
public sealed record DecodedField(string Name, string Value, ContentRange Bytes);

/// <summary>
/// What a decoder made of one record's kept content (§11.2, content-v1 §4): the decoder that read it, at its version; the
/// record whose fragment it read and the kept bytes it read; every field it decoded, with the bytes each came from; and
/// what it did not decode, and why. It is derived from the fragment and never stands in for it: the fragment's bytes stay
/// the evidence, and a decoding is made again whenever it is asked for, never kept.
/// </summary>
public sealed record DecodedContent
{
    /// <summary>The decoder's stable identity, which names its format: "intercat-content-fixture".</summary>
    public required string DecoderId { get; init; }

    /// <summary>The decoder as a person reads its name: "InterCat's content fixture decoder".</summary>
    public required string Decoder { get; init; }

    /// <summary>The decoder's version: the same bytes decode to the same fields at one version, and may not at another.</summary>
    public required int DecoderVersion { get; init; }

    /// <summary>The record whose fragment was read.</summary>
    public required RawRecordId Record { get; init; }

    /// <summary>The kept bytes the decoder read, by their offsets in the message; null when it read none.</summary>
    public required ContentRange? Read { get; init; }

    /// <summary>The fields decoded, in the order of their bytes.</summary>
    public required IReadOnlyList<DecodedField> Fields { get; init; }

    /// <summary>What of the message was not decoded, and why, as a sentence; null when every kept byte was.</summary>
    public string? NotDecoded { get; init; }

    /// <summary>Which decoder read it, at which version, as a heading says it.</summary>
    public string Heading => string.Create(CultureInfo.InvariantCulture, $"Decoded by {Decoder}, version {DecoderVersion}");
}

/// <summary>
/// The decoders a person may ask to read kept content with (§11.2): InterCat's own content fixture's alone, the small
/// synthetic decoder that proves the model - a decoding linked to the fragment it read and to its decoder's version -
/// before any real protocol's decoder exists. A decoding is asked for, never made on its own; it reads only bytes kept with
/// consent to inspect them, at most what a view shows at once, within a time budget; and it renders, runs and fetches
/// nothing it reads, so its fields are inert text.
/// </summary>
public static class ContentDecoders
{
    /// <summary>The most bytes a decoding reads: what a view shows at once.</summary>
    public const int MaximumBytes = ContentBytesView.MaximumShownBytes;

    /// <summary>How long a decoding may take before it stops and says so.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>The name of the decoder that reads <paramref name="row"/>'s content, or null when none does.</summary>
    public static string? For(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return FixtureMessages.Reads(row) ? FixtureMessages.Name : null;
    }

    /// <summary>
    /// Decodes <paramref name="detail"/>'s kept content with the decoder that reads its record; null when none does. Its
    /// bytes must have been read, and kept with consent to inspect them: content kept without that consent is never
    /// decoded, and says so.
    /// </summary>
    public static DecodedContent? Decode(SessionContentDetail detail, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (!detail.Available || detail.Entry is not { } entry || !FixtureMessages.Reads(detail.Observation))
        {
            return null;
        }

        RawRecordId record = entry.Fragment.RecordIn(entry.Header.CaptureId);
        if (ContentBytesView.Kept(entry.Fragment) is not { } kept)
        {
            return FixtureMessages.Nothing(record, entry.Fragment.Disposition == ContentDispositionV1.OmittedBySessionLimit
                ? "No byte of the message was kept, so there is nothing to decode."
                : "The message holds no bytes, so there is nothing to decode.");
        }

        if (!entry.Inspectable)
        {
            return FixtureMessages.Nothing(record,
                "The capture kept these bytes without consent to inspect them, so they are never decoded.");
        }

        if (detail.Bytes is not { } bytes)
        {
            throw new InvalidOperationException("A decoding reads the kept bytes, which were not read.");
        }

        return FixtureMessages.Decode(record, kept, bytes, entry.Fragment, budget ?? Budget);
    }

    /// <summary>
    /// InterCat's content fixture's text messages (ADR-036, FX-CONTENT-001): "InterCat content fixture message 7 on
    /// conversation 2. " and then its filler sentence, repeated for as long as the message is. Its byte messages carry no
    /// fields, and are said to.
    /// </summary>
    private static class FixtureMessages
    {
        public const string Id = "intercat-content-fixture";
        public const string Name = "InterCat's content fixture decoder";
        public const int Version = 1;
        private const string Lead = "InterCat content fixture message ";
        private const string Middle = " on conversation ";
        private const string End = ". ";
        private const string Filler = "The quick brown fox jumps over the lazy dog. ";

        /// <summary>The most digits a header's number has: any more, and it is not the fixture's.</summary>
        private const int MaximumDigits = 18;

        /// <summary>The fixture's provider, whose two events carry a message its raiser sent or received.</summary>
        private static readonly Guid Provider =
            System.Diagnostics.Tracing.EventSource.GetGuid(typeof(ContentFixtureEventSource));

        public static bool Reads(ObservationRowV1 row) => row.ProviderId == Provider
            && row.EventId is ContentFixtureEventSource.MessageSentId or ContentFixtureEventSource.MessageReceivedId;

        /// <summary>A decoding that read no byte, and why.</summary>
        public static DecodedContent Nothing(RawRecordId record, string why) => new()
        {
            DecoderId = Id,
            Decoder = Name,
            DecoderVersion = Version,
            Record = record,
            Read = null,
            Fields = [],
            NotDecoded = why,
        };

        public static DecodedContent Decode(
            RawRecordId record, ContentRange kept, ReadOnlyMemory<byte> all, ContentFragmentV1 fragment, TimeSpan budget)
        {
            // A fixture message's header begins at its first byte, so bytes kept from later in it have none to read.
            if (kept.First > 0)
            {
                return Nothing(record, string.Create(CultureInfo.CurrentCulture,
                    $"The capture kept the message from byte {kept.First:N0}, not from its first, where a fixture message's ")
                    + "header begins, so nothing is decoded.");
            }

            // At most what a view shows at once is read, from the message's first byte, so an offset in the bytes read is
            // the same offset in the message; the rest of a longer message is said to be left undecoded.
            ContentRange read = ContentBytesView.Shown(kept);
            ReadOnlySpan<byte> bytes = ContentBytesView.Slice(all, kept, read).Span;
            Stopwatch elapsed = Stopwatch.StartNew();
            var fields = new List<DecodedField>(3);
            int at = 0;

            // The header, part by part: a number is decoded only once a byte past its digits shows it whole.
            Mismatch? header = Literal(bytes, ref at, Lead)
                ?? Number(bytes, ref at, "message", fields)
                ?? Literal(bytes, ref at, Middle)
                ?? Number(bytes, ref at, "conversation", fields)
                ?? Literal(bytes, ref at, End);
            string? stopped = header is { } mismatch ? Header(mismatch, fragment) : null;
            if (stopped is null && at < bytes.Length)
            {
                // The filler, checked byte by byte against the fixture's sentence, within the time budget.
                int differs = -1;
                for (int index = at; index < bytes.Length && differs < 0; index++)
                {
                    if (bytes[index] != Filler[(index - at) % Filler.Length])
                    {
                        differs = index;
                    }
                    else if ((index & 0xFFF) == 0 && elapsed.Elapsed > budget)
                    {
                        stopped = string.Create(CultureInfo.CurrentCulture,
                            $"The decoder stopped at its time budget, at byte {index:N0}; the rest is not decoded.");
                        break;
                    }
                }

                if (stopped is null)
                {
                    var filler = new ContentRange(at, read.Last);
                    fields.Add(new("filler", differs < 0
                        ? $"the fixture's filler sentence, repeated ({CountText.Of(filler.Length, "byte")})"
                        : string.Create(CultureInfo.CurrentCulture,
                            $"{CountText.Of(filler.Length, "byte")}, which leave the fixture's filler sentence at byte {differs:N0}"),
                        filler));
                }
            }

            return new()
            {
                DecoderId = Id,
                Decoder = Name,
                DecoderVersion = Version,
                Record = record,
                Read = read,
                Fields = fields,
                NotDecoded = stopped ?? Rest(fragment, kept, read),
            };
        }

        /// <summary>
        /// Why the header the bytes began did not decode: they end inside it - the message itself, or the capture's kept
        /// prefix of it - or they leave it, which no fixture text message does.
        /// </summary>
        private static string Header(Mismatch mismatch, ContentFragmentV1 fragment)
        {
            int at = mismatch.At;
            return !mismatch.Ended
                ? string.Create(CultureInfo.CurrentCulture, $"At byte {at:N0} it leaves the header a fixture text message ")
                    + "begins with, so it carries no fields the decoder reads; the fixture's byte messages are bytes alone."
                : fragment.Disposition == ContentDispositionV1.TruncatedByRecordLimit
                    ? string.Create(CultureInfo.CurrentCulture, $"The kept bytes end inside the fixture's header, at byte {at:N0}, ")
                        + "so the rest of it, and what it names, is not decoded."
                    : string.Create(CultureInfo.CurrentCulture, $"The message ends inside the fixture's header, at byte {at:N0}, ")
                        + "so it names no more than the fields decoded.";
        }

        /// <summary>What of the message lies past the bytes read, and why it is not decoded; null when nothing does.</summary>
        private static string? Rest(ContentFragmentV1 fragment, ContentRange kept, ContentRange read) =>
            read.Last < kept.Last
                ? string.Create(CultureInfo.CurrentCulture,
                    $"The decoder read the first {read.Length:N0} kept bytes, as many as a view shows at once; the rest is not decoded.")
                : fragment.Disposition == ContentDispositionV1.TruncatedByRecordLimit && fragment.OriginalLength is { } original
                    ? string.Create(CultureInfo.CurrentCulture,
                        $"The capture kept the first {kept.Length:N0} of the message's {original:N0} bytes; the rest was never kept, so it is not decoded.")
                    : null;

        /// <summary>
        /// Matches <paramref name="literal"/> at <paramref name="at"/>, moving past it; null when it matched, else where it
        /// stopped: at the bytes' end, or at a byte that differs.
        /// </summary>
        private static Mismatch? Literal(ReadOnlySpan<byte> bytes, ref int at, string literal)
        {
            foreach (char expected in literal)
            {
                if (at >= bytes.Length)
                {
                    return new(at, Ended: true);
                }

                if (bytes[at] != expected)
                {
                    return new(at, Ended: false);
                }

                at++;
            }

            return null;
        }

        /// <summary>
        /// Reads a number's ASCII digits at <paramref name="at"/> into a field, moving past them; null when it did, else
        /// where it stopped: where a digit should begin and none does, at a digit past the most a fixture's number has, or
        /// at the bytes' end before a byte past the digits shows the number whole - a number cut there could have been
        /// longer, so it is not decoded.
        /// </summary>
        private static Mismatch? Number(ReadOnlySpan<byte> bytes, ref int at, string name, List<DecodedField> fields)
        {
            int start = at;
            while (at < bytes.Length && at - start < MaximumDigits && IsDigit(bytes[at]))
            {
                at++;
            }

            if (at >= bytes.Length)
            {
                return new(start, Ended: true);
            }

            if (at == start || IsDigit(bytes[at]))
            {
                return new(at, Ended: false);
            }

            fields.Add(new(name, Encoding.ASCII.GetString(bytes[start..at]), new(start, at - 1)));
            return null;
        }

        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

        /// <summary>Where a literal stopped matching: at a byte that differs, or where the bytes ended.</summary>
        private readonly record struct Mismatch(int At, bool Ended);
    }
}
