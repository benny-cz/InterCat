using System.Diagnostics;
using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

public static partial class ContentDecoders
{
    /// <summary>
    /// HTTP/1.1 message heads as WinINet's capture keeps them, each one part (ADR-037; FX-HTTP-001 to FX-HTTP-003 found
    /// every kept part equal to the wire): a request head's request line - its method, target and version - or a response
    /// head's status line - its version, status code and reason - then each field line's name and value, to the empty line
    /// that ends the head (RFC 9112 §2 to §5). A line HTTP/1.1 does not allow where it stands stops the decoding at its
    /// start, saying which line and why, and nothing from it on is decoded, so a head is never decoded as more than its
    /// bytes show (M8). A line ends in CRLF, or in LF alone, as RFC 9112 §2.2 lets a recipient read it. Values are inert
    /// text: each byte outside visible ASCII and the space is written as \xHH.
    /// </summary>
    private static class HttpHeads
    {
        public const string Id = "http1-head";
        public const string Name = "InterCat's HTTP/1.1 head decoder";
        public const int Version = 1;

        /// <summary>WinINet's capture events of a request head and of a response head (content-v1 §5.1).</summary>
        public const ushort RequestHead = 2001, ResponseHead = 2003;

        public static bool Reads(ObservationRowV1 row) =>
            row.Mechanism == Mechanism.Http && row.EventId is RequestHead or ResponseHead;

        /// <summary>A decoding of <paramref name="part"/> that read no byte, and why.</summary>
        public static DecodedContent Nothing(RawRecordId record, string part, string why) => new()
        {
            DecoderId = Id,
            Decoder = Name,
            DecoderVersion = Version,
            Record = record,
            Part = part,
            Read = null,
            Fields = [],
            NotDecoded = why,
        };

        public static DecodedContent Decode(RawRecordId record, string part, ReadOnlyMemory<byte> all, bool request, TimeSpan budget)
        {
            // At most what a view shows at once is read, from the head's first byte, so an offset in the bytes read is the
            // same offset in the part.
            int length = (int)Math.Min(all.Length, MaximumBytes);
            ReadOnlySpan<byte> bytes = all.Span[..length];
            CultureInfo culture = CultureInfo.CurrentCulture;
            Stopwatch elapsed = Stopwatch.StartNew();
            var fields = new List<DecodedField>();
            string? stopped = null;
            int at = 0;
            int number = 0;
            while (stopped is null)
            {
                if (elapsed.Elapsed >= budget)
                {
                    stopped = string.Create(culture, $"The decoder stopped at its time budget, at byte {at:N0}; the rest is not decoded.");
                    break;
                }

                int lineFeed = bytes[at..].IndexOf((byte)'\n');
                if (lineFeed < 0)
                {
                    stopped = Unended(at, length, all.Length, number, culture);
                    break;
                }

                number++;
                int end = lineFeed > 0 && bytes[at + lineFeed - 1] == '\r' ? at + lineFeed - 1 : at + lineFeed;
                ReadOnlySpan<byte> line = bytes[at..end];
                if (line.IndexOf((byte)'\r') >= 0)
                {
                    stopped = string.Create(culture, $"Line {number:N0}, from byte {at:N0}, holds a carriage return that ends no line, ")
                        + (number == 1 ? "so nothing is decoded." : "so the head is not decoded from it on.");
                    break;
                }

                if (number == 1)
                {
                    stopped = request ? RequestLine(line, at, fields) : StatusLine(line, at, fields);
                }
                else if (line.Length == 0)
                {
                    // The empty line ends the head; a head part holds nothing after it.
                    int after = all.Length - (at + lineFeed + 1);
                    return Decoded(record, part, length, fields, after > 0
                        ? CountText.Of(after, "byte") + " " + CountText.Agree(after, "follows", "follow")
                            + " the empty line that ends the head; a head holds none, so " + CountText.Agree(after, "it is", "they are")
                            + " not decoded."
                        : null);
                }
                else
                {
                    stopped = FieldLine(line, at, number, fields, culture);
                }

                at += lineFeed + 1;
            }

            return Decoded(record, part, length, fields, stopped);
        }

        private static DecodedContent Decoded(RawRecordId record, string part, int length, List<DecodedField> fields, string? stopped) => new()
        {
            DecoderId = Id,
            Decoder = Name,
            DecoderVersion = Version,
            Record = record,
            Part = part,
            Read = new ContentRange(0, length - 1),
            Fields = fields,
            NotDecoded = stopped,
        };

        /// <summary>
        /// Why the head ends before the empty line that ends a head: the bytes read stop at what a view shows at once; the
        /// head holds no line end at all; it ends inside a line, which is then not decoded; or it ends after a whole line,
        /// and may have held more fields.
        /// </summary>
        private static string Unended(int at, int length, long all, int number, CultureInfo culture) =>
            length < all
                ? string.Create(culture, $"The decoder read the first {length:N0} bytes, as many as a view shows at once, and the head does not end in them; the rest is not decoded.")
                : number == 0
                    ? "The head ends before its first line does, so nothing is decoded."
                    : at < length
                        ? string.Create(culture, $"The head ends inside line {number + 1:N0}, from byte {at:N0}, before the empty line that ends a head, so that line is not decoded.")
                        : "The head ends without the empty line that ends a head, so it may have held more fields than those decoded.";

        /// <summary>A request line - method, target and version, one space apart - into its three fields; else why it is not one.</summary>
        private static string? RequestLine(ReadOnlySpan<byte> line, int start, List<DecodedField> fields)
        {
            int space = line.IndexOf((byte)' ');
            int second = space > 0 ? line[(space + 1)..].IndexOf((byte)' ') : -1;
            if (space <= 0 || second <= 0 || !IsToken(line[..space]) || !IsTarget(line.Slice(space + 1, second))
                || !IsVersion(line[(space + second + 2)..]))
            {
                return "Its first line is not a request line - a method, a target and an HTTP version, one space apart - so "
                    + "nothing is decoded.";
            }

            fields.Add(new("method", Encoding.ASCII.GetString(line[..space]), new(start, start + space - 1)));
            fields.Add(new("target", Encoding.ASCII.GetString(line.Slice(space + 1, second)), new(start + space + 1, start + space + second)));
            fields.Add(new("version", Encoding.ASCII.GetString(line[(space + second + 2)..]), new(start + space + second + 2, start + line.Length - 1)));
            return null;
        }

        /// <summary>
        /// A status line - an HTTP version, a three-digit status code and a reason, one space apart, the reason empty or
        /// absent - into its fields; else why it is not one.
        /// </summary>
        private static string? StatusLine(ReadOnlySpan<byte> line, int start, List<DecodedField> fields)
        {
            if (line.Length < 12 || !IsVersion(line[..8]) || line[8] != ' ' || !IsDigit(line[9]) || !IsDigit(line[10])
                || !IsDigit(line[11]) || (line.Length > 12 && (line[12] != ' ' || !IsValue(line[13..]))))
            {
                return "Its first line is not a status line - an HTTP version, a three-digit status code and a reason, one "
                    + "space apart - so nothing is decoded.";
            }

            fields.Add(new("version", Encoding.ASCII.GetString(line[..8]), new(start, start + 7)));
            fields.Add(new("status", Encoding.ASCII.GetString(line[9..12]), new(start + 9, start + 11)));
            if (line.Length > 13)
            {
                fields.Add(new("reason", Inert(line[13..]), new(start + 13, start + line.Length - 1)));
            }

            return null;
        }

        /// <summary>
        /// A field line - a name, a colon, then its value between optional whitespace - into a field named as the line names
        /// it, its bytes the whole line; else why it is not one, which stops the decoding there.
        /// </summary>
        private static string? FieldLine(ReadOnlySpan<byte> line, int start, int number, List<DecodedField> fields, CultureInfo culture)
        {
            string where = string.Create(culture, $"Line {number:N0}, from byte {start:N0},");
            if (line[0] is (byte)' ' or (byte)'\t')
            {
                return where + " begins with whitespace, continuing the line before it as HTTP/1.1 no longer allows, so the head "
                    + "is not decoded from it on.";
            }

            int colon = line.IndexOf((byte)':');
            if (colon < 0)
            {
                return where + " holds no colon, so it is not a field line, and the head is not decoded from it on.";
            }

            if (!IsToken(line[..colon]))
            {
                return where + " names its field with nothing, or with a character a field name cannot hold - whitespace before "
                    + "its colon is one - so the head is not decoded from it on.";
            }

            ReadOnlySpan<byte> value = line[(colon + 1)..].Trim(" \t"u8);
            if (!IsValue(value))
            {
                return where + " holds a control character in its value, so the head is not decoded from it on.";
            }

            fields.Add(new(Encoding.ASCII.GetString(line[..colon]), Inert(value), new(start, start + line.Length - 1)));
            return null;
        }

        /// <summary>Whether <paramref name="bytes"/> are a token (RFC 9110 §5.6.2): one or more of its characters.</summary>
        private static bool IsToken(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return false;
            }

            foreach (byte value in bytes)
            {
                if (!(IsDigit(value) || value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                    || "!#$%&'*+-.^_`|~"u8.Contains(value)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether <paramref name="bytes"/> are a request target: one or more visible ASCII characters.</summary>
        private static bool IsTarget(ReadOnlySpan<byte> bytes) =>
            !bytes.IsEmpty && bytes.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7E) < 0;

        /// <summary>Whether <paramref name="bytes"/> are an HTTP version: "HTTP/", a digit, a dot and a digit (RFC 9112 §2.3).</summary>
        private static bool IsVersion(ReadOnlySpan<byte> bytes) =>
            bytes.Length == 8 && bytes.StartsWith("HTTP/"u8) && IsDigit(bytes[5]) && bytes[6] == '.' && IsDigit(bytes[7]);

        /// <summary>Whether <paramref name="bytes"/> hold no control character but the tab, and no DEL (RFC 9110 §5.5).</summary>
        private static bool IsValue(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                if (value is < 0x20 and not (byte)'\t' or 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The bytes as inert text: visible ASCII and the space as they are, every other byte as \xHH.</summary>
        private static string Inert(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length);
            foreach (byte value in bytes)
            {
                if (value is >= 0x20 and <= 0x7E)
                {
                    text.Append((char)value);
                }
                else
                {
                    text.Append(CultureInfo.InvariantCulture, $"\\x{value:X2}");
                }
            }

            return text.ToString();
        }

        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    }
}
