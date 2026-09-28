using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A run of a message's bytes by their offsets in it, from the first to the last, both included.</summary>
public readonly record struct ContentRange(long First, long Last)
{
    /// <summary>How many bytes the range spans.</summary>
    public long Length => Last - First + 1;

    /// <summary>The range in words: "bytes 0 to 4,095 (4,096 bytes)".</summary>
    public string Describe(IFormatProvider? culture = null) => string.Create(
        culture ?? CultureInfo.CurrentCulture, $"bytes {First:N0} to {Last:N0} ({Length:N0} {(Length == 1 ? "byte" : "bytes")})");
}

/// <summary>One line of a hex view: at most sixteen bytes, the offset of the first in its message, and each one's ASCII.</summary>
/// <param name="Offset">The first byte's offset in its message.</param>
/// <param name="Hex">The bytes in hexadecimal, two groups of eight, padded so a short last line keeps the text column.</param>
/// <param name="Text">Each byte as printable ASCII, or '.' for any other value.</param>
public sealed record ContentHexRow(long Offset, string Hex, string Text)
{
    /// <summary>The line as a hex dump prints it, which is what a copy and a screen reader take.</summary>
    public string Line => string.Create(CultureInfo.InvariantCulture, $"{Offset:X8}  {Hex}  {Text}");
}

/// <summary>A fragment's text as its source declares it, made safe to show.</summary>
/// <param name="Text">The text, with every control, format and private character shown as a visible mark.</param>
/// <param name="Cut">Whether the text stopped at the view's character bound before the bytes did.</param>
/// <param name="Escaped">How many characters are shown as a visible mark instead of themselves.</param>
/// <param name="Replaced">How many characters are U+FFFD: bytes that are not the declared encoding there, or that character.</param>
public sealed record ContentText(string Text, bool Cut, int Escaped, int Replaced);

/// <summary>One fact about kept content, labelled as the viewer and the command line state it.</summary>
public sealed record ContentFact(string Label, string Value);

/// <summary>
/// How kept content is shown to a person (§3.7, §11.2): its facts first; then, only when asked, inert hexadecimal with
/// each byte's printable ASCII beside it, and, only for a fragment its source declares text, that text with every control
/// and invisible character shown as a visible mark. Nothing is decoded beyond what the source declared, nothing in it
/// runs or renders as markup, and at most a bounded window of it is shown at once.
/// </summary>
public static class ContentBytesView
{
    /// <summary>The bytes one line of the hex view shows.</summary>
    public const int BytesPerRow = 16;

    /// <summary>The most bytes a view shows at once; a longer range is shown from its first byte.</summary>
    public const int MaximumShownBytes = 64 * 1024;

    /// <summary>The most characters the text view shows at once.</summary>
    public const int MaximumTextCharacters = 16 * 1024;

    private const string HexDigits = "0123456789ABCDEF";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);

    /// <summary>The bytes a fragment keeps, by their offsets in its message; null when it keeps none.</summary>
    public static ContentRange? Kept(ContentFragmentV1 fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        long first = fragment.Offset ?? 0;
        return fragment.Kept == 0 ? null : new(first, first + fragment.Kept - 1);
    }

    /// <summary>What of <paramref name="range"/> a view shows at once: all of it, or its first <see cref="MaximumShownBytes"/>.</summary>
    public static ContentRange Shown(ContentRange range) =>
        range.Length <= MaximumShownBytes ? range : new(range.First, range.First + MaximumShownBytes - 1);

    /// <summary>The bytes of <paramref name="range"/>, out of every kept byte of the fragment that keeps <paramref name="kept"/>.</summary>
    public static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> bytes, ContentRange kept, ContentRange range)
    {
        if (range.First < kept.First || range.Last > kept.Last || range.First > range.Last || bytes.Length != kept.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "A range outside the kept bytes has no bytes to show.");
        }

        return bytes.Slice((int)(range.First - kept.First), (int)range.Length);
    }

    /// <summary>The hex view's lines of <paramref name="bytes"/>, the first of which is at <paramref name="firstOffset"/>.</summary>
    public static IReadOnlyList<ContentHexRow> Rows(ReadOnlySpan<byte> bytes, long firstOffset)
    {
        if (bytes.Length > MaximumShownBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "A view shows a bounded window of bytes at once.");
        }

        var rows = new List<ContentHexRow>((bytes.Length + BytesPerRow - 1) / BytesPerRow);
        var hex = new StringBuilder(BytesPerRow * 3 + 1);
        var text = new StringBuilder(BytesPerRow);
        for (int start = 0; start < bytes.Length; start += BytesPerRow)
        {
            ReadOnlySpan<byte> line = bytes.Slice(start, Math.Min(BytesPerRow, bytes.Length - start));
            hex.Clear();
            text.Clear();
            for (int index = 0; index < BytesPerRow; index++)
            {
                if (index > 0)
                {
                    hex.Append(index == BytesPerRow / 2 ? "  " : " ");
                }

                if (index < line.Length)
                {
                    hex.Append(HexDigits[line[index] >> 4]).Append(HexDigits[line[index] & 0xF]);
                    text.Append(line[index] is >= 0x20 and <= 0x7E ? (char)line[index] : '.');
                }
                else
                {
                    hex.Append("  ");
                }
            }

            rows.Add(new(firstOffset + start, hex.ToString(), text.ToString()));
        }

        return rows;
    }

    /// <summary>The bytes as the hex view shows them, one line a row: what "copy as hex" places on the clipboard.</summary>
    public static string Dump(ReadOnlySpan<byte> bytes, long firstOffset)
    {
        IReadOnlyList<ContentHexRow> rows = Rows(bytes, firstOffset);
        var dump = new StringBuilder(rows.Count * 80);
        foreach (ContentHexRow row in rows)
        {
            dump.Append(row.Line).Append(Environment.NewLine);
        }

        return dump.ToString();
    }

    /// <summary>
    /// <paramref name="bytes"/> read as the text <paramref name="encoding"/> declares, which a caller asks only of a
    /// fragment whose source declares text. A line break stays one; a tab stays one; every other control character, every
    /// invisible or direction-changing format character, every separator that would break a line unseen, and every
    /// private or unassigned code point is shown as a visible mark, so what is shown is what the bytes hold.
    /// </summary>
    public static ContentText Text(ReadOnlySpan<byte> bytes, ContentEncodingV1 encoding)
    {
        string decoded = encoding switch
        {
            ContentEncodingV1.Utf8 => Utf8.GetString(bytes),
            ContentEncodingV1.Utf16LittleEndian => Utf16.GetString(bytes),
            _ => throw new ArgumentException("Only content its source declares text is read as text.", nameof(encoding)),
        };

        var text = new StringBuilder(Math.Min(decoded.Length, MaximumTextCharacters) + 16);
        Span<char> units = stackalloc char[2];
        int escaped = 0, replaced = 0;
        bool cut = false;
        for (int index = 0; index < decoded.Length;)
        {
            if (text.Length >= MaximumTextCharacters)
            {
                cut = true;
                break;
            }

            _ = Rune.DecodeFromUtf16(decoded.AsSpan(index), out Rune rune, out int consumed);
            index += consumed;
            if (rune.Value is '\n' or '\t')
            {
                text.Append((char)rune.Value);
            }
            else if (rune.Value == '\r')
            {
                // A Windows line break is one line break; a carriage return alone is shown, since it overwrites a line.
                if (index < decoded.Length && decoded[index] == '\n')
                {
                    text.Append('\n');
                    index++;
                }
                else
                {
                    text.Append('␍');
                    escaped++;
                }
            }
            else if (rune.Value < 0x20)
            {
                // The control pictures block names each C0 control: U+2400 is NUL's.
                text.Append((char)(0x2400 + rune.Value));
                escaped++;
            }
            else if (rune.Value == 0x7F)
            {
                text.Append('␡');
                escaped++;
            }
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
            {
                text.Append(CultureInfo.InvariantCulture, $"⟨U+{rune.Value:X4}⟩");
                escaped++;
            }
            else
            {
                replaced += rune.Value == 0xFFFD ? 1 : 0;
                text.Append(units[..rune.EncodeToUtf16(units)]);
            }
        }

        return new(text.ToString(), cut, escaped, replaced);
    }

    /// <summary>Whether a fragment's source declares its bytes text, so a text view of them is not a guess.</summary>
    public static bool DeclaresText(ContentEncodingV1 encoding) =>
        encoding is ContentEncodingV1.Utf8 or ContentEncodingV1.Utf16LittleEndian;

    /// <summary>
    /// Reads a range a person typed - its first and last byte, each a decimal offset, with any digit grouping, or a
    /// hexadecimal one after "0x" - within <paramref name="kept"/>. False, with the problem in words, when it is not one.
    /// </summary>
    public static bool TryParseRange(string? first, string? last, ContentRange kept, out ContentRange range, out string? problem)
    {
        range = default;
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (!TryParseOffset(first, out long from))
        {
            problem = "Enter the first byte as an offset, like 16 or 0x10.";
            return false;
        }

        if (!TryParseOffset(last, out long to))
        {
            problem = "Enter the last byte as an offset, like 4095 or 0xFFF.";
            return false;
        }

        if (from > to)
        {
            problem = "The first byte comes after the last.";
            return false;
        }

        if (from < kept.First || to > kept.Last)
        {
            problem = string.Create(culture, $"The kept bytes are {kept.First:N0} to {kept.Last:N0}; choose a range within them.");
            return false;
        }

        range = new(from, to);
        problem = null;
        return true;
    }

    /// <summary>
    /// What a person reads before any byte (§3.7): what the bytes are and where they came from, how many of the message
    /// were kept and which are missing, whether it is part of a reassembled whole, and what it was kept under.
    /// </summary>
    public static IReadOnlyList<ContentFact> Facts(SessionContentEntry entry, ObservationRowV1 row, long generation,
        IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(row);
        culture ??= CultureInfo.CurrentCulture;
        ContentFragmentV1 fragment = entry.Fragment;
        string classification = RecordContent.Classification(fragment.Classification);
        string what = char.ToUpperInvariant(classification[0]) + classification[1..] + fragment.Direction switch
        {
            Direction.Outbound => ", which the record's owner sent",
            Direction.Inbound => ", which the record's owner received",
            _ => "; its source does not say which way it went",
        };
        string encoding = fragment.Encoding switch
        {
            ContentEncodingV1.Utf8 => "UTF-8 text, as its source declares",
            ContentEncodingV1.Utf16LittleEndian => "UTF-16 text (little-endian), as its source declares",
            _ => "Binary, as its source declares; it is never read as text",
        };
        string message = fragment.OriginalLength is { } original
            ? string.Create(culture, $"{original:N0} {(original == 1 ? "byte" : "bytes")}")
            : "Its length is not stated by its source";
        ContentRange? kept = Kept(fragment);
        string keptText = fragment.Disposition switch
        {
            ContentDispositionV1.OmittedBySessionLimit => "None: the capture had reached its content limit when this record arrived",
            ContentDispositionV1.TruncatedByRecordLimit => string.Create(culture,
                $"{Capital(kept!.Value.Describe(culture))}, cut by the {entry.Header.RecordLimit:N0}-byte record limit"),
            _ when kept is null => "All of it: the message held no bytes",
            _ => "All of it: " + kept.Value.Describe(culture),
        };
        long start = fragment.Offset ?? 0;
        var missing = new List<string>(2);
        if (start > 0)
        {
            missing.Add(new ContentRange(0, start - 1).Describe(culture) + ", before this fragment");
        }

        if (fragment.OriginalLength is { } length && start + fragment.Kept < length)
        {
            missing.Add(new ContentRange(start + fragment.Kept, length - 1).Describe(culture));
        }

        string missingText = missing.Count > 0
            ? Capital(string.Join("; ", missing)) + ". They were never recorded, and nothing stands in for them"
            : fragment.OriginalLength is null && fragment.Disposition != ContentDispositionV1.Whole
                ? "Unknown: its source does not state the message's length"
                : "None";
        string reassembly = start > 0
            ? string.Create(culture, $"One fragment, from byte {start:N0} of its message; it is not joined with any other record's content")
            : "One fragment, from its message's first byte; it is not joined with any other record's content";
        string policy = string.Create(culture, $"{entry.Header.PolicyId}, at most {entry.Header.RecordLimit:N0} bytes a record; ")
            + (entry.Inspectable
                ? "its bytes are shown only when you ask"
                : "its bytes are never shown: the capture kept them without consent to inspect them");
        return
        [
            new("What it is", what),
            new("Source", string.Create(culture,
                $"{EvidenceRowText.ProviderName(row.ProviderId)} · event {row.EventId} version {row.DescriptorVersion}")),
            new("Encoding", encoding),
            new("Message", message),
            new("Kept", keptText),
            new("Missing", missingText),
            new("Reassembly", reassembly),
            new("Kept under", policy),
            new("Stored in", string.Create(culture, $"{entry.ChunkName}, beside the journal, read in generation {generation:N0}")),
        ];
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool TryParseOffset(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                && value >= 0;
        }

        // Digit grouping as any culture writes it - "4,095", "4 095", "4'095" - so an offset copied from a size reads back.
        var digits = new StringBuilder(trimmed.Length);
        foreach (char character in trimmed)
        {
            if (character is not (',' or '_' or '\'' or ' ' or ' ' or ' '))
            {
                digits.Append(character);
            }
        }

        return long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
