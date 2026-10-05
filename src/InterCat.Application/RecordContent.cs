using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What a record holds of the message it describes (§3.7, §11).</summary>
public enum RecordContentState
{
    /// <summary>Its source carries no content: the reason says what it carries instead.</summary>
    None = 1,

    /// <summary>The capture kept some or all of it (`contracts/content-v1.md`).</summary>
    Kept = 2,

    /// <summary>The capture would have kept it, but its content limit had been reached.</summary>
    Omitted = 3,

    /// <summary>Kept content could not all be read, so whether this record's is among it is unknown (R21).</summary>
    Unknown = 4,
}

/// <summary>
/// What a record holds of the message it describes - the bytes a process sent or received, a call's arguments - and,
/// when nothing, why, and which source could hold them (§3.7, §11). Most sources carry metadata about a transfer, a call
/// or a message, and the statement says which, rather than leaving a person to wonder whether the bytes were dropped. A
/// record whose content a capture kept says how much of it, of what, and whether a person may see it (ADR-036).
/// </summary>
/// <param name="Reason">What the record holds of its message, as a sentence.</param>
/// <param name="Elsewhere">Which source could hold it, as a sentence, when its source holds none and one is known.</param>
public sealed record RecordContent(RecordContentState State, string Reason, string? Elsewhere)
{
    /// <summary>Whether the session holds any of the record's bytes.</summary>
    public bool Recorded => State == RecordContentState.Kept;

    /// <summary>The statement as the inspector and the command line give it.</summary>
    public string Describe() => State switch
    {
        RecordContentState.None => "None. " + Reason + (Elsewhere is null ? string.Empty : " " + Elsewhere),
        RecordContentState.Unknown => "Unknown. " + Reason,
        _ => Reason,
    };

    /// <summary>
    /// The content of <paramref name="row"/>'s record: what the capture kept of it (<paramref name="kept"/>), or why it
    /// holds none. A redacted package's record is synthetic (<paramref name="synthetic"/>) and keeps nothing of any original.
    /// <paramref name="problem"/> says why kept content could not all be read.
    /// </summary>
    public static RecordContent Of(
        ObservationRowV1 row,
        bool synthetic = false,
        SessionContentEntry? kept = null,
        string? problem = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (synthetic)
        {
            return new(RecordContentState.None,
                "A redacted package keeps no body or content of any record; its records are synthetic.", null);
        }

        if (kept is not null)
        {
            return Kept(kept);
        }

        if (problem is not null)
        {
            return new(RecordContentState.Unknown,
                $"This session's kept content could not all be read ({problem}), so this record's may be in what could not.",
                null);
        }

        return row.Mechanism switch
        {
            Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle =>
                new(RecordContentState.None, "A lifecycle record describes a process or thread, not a message.", null),
            Mechanism.Tcp or Mechanism.Udp => new(RecordContentState.None,
                "The kernel's network events carry endpoints and a transfer's size, never the bytes sent.",
                "A packet capture could hold them, and encrypted traffic stays encrypted there. A Content capture of a WinINet "
                + "client's process keeps its HTTP messages above the encryption."),
            Mechanism.Rpc => new(RecordContentState.None,
                "RPC's events carry a call's interface, procedure and status, never its arguments.",
                "No driverless source is known to carry them; instrumenting the process could."),
            Mechanism.Alpc => new(RecordContentState.None, "ALPC's kernel events carry a message id, never the message.", null),

            // WinINet's capture carries the message itself, and a capture keeps a record for each of its buffers: one with
            // none was released since, on its own or with its journal chunk (content-v1 §2).
            Mechanism.Http => new(RecordContentState.None,
                "Its source carries the message itself, and the session keeps none of it now: a retention released it, on "
                + "its own or with its journal chunk.", null),
            _ => new(RecordContentState.None, "This record's source carries metadata only.", null),
        };
    }

    /// <summary>What a capture kept of a record, in words: how many bytes of what, and whether a person may see them.</summary>
    private static RecordContent Kept(SessionContentEntry kept)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ContentFragmentV1 fragment = kept.Fragment;
        string what = Classification(fragment.Classification) + fragment.Direction switch
        {
            Direction.Outbound => " it sent",
            Direction.Inbound => " it received",
            _ => string.Empty,
        };
        string encoding = fragment.Encoding switch
        {
            ContentEncodingV1.Utf8 => "UTF-8 text",
            ContentEncodingV1.Utf16LittleEndian => "UTF-16 text",
            _ => "binary",
        };
        string from = fragment.Offset is > 0 and { } offset
            ? string.Create(culture, $" from byte {offset:N0} of its message")
            : string.Empty;
        string hidden = kept.Inspectable
            ? string.Empty
            : " Its bytes are not shown: the capture kept them without consent to inspect them.";
        return fragment.Disposition switch
        {
            ContentDispositionV1.TruncatedByRecordLimit => new(RecordContentState.Kept,
                string.Create(culture,
                    $"Kept in part: the first {fragment.Kept:N0} of {fragment.OriginalLength:N0} bytes of {what}{from}, {encoding}; ")
                + string.Create(culture,
                    $"the other {fragment.Missing:N0} were cut by the {kept.Header.RecordLimit:N0}-byte record limit.")
                + hidden,
                null),
            ContentDispositionV1.OmittedBySessionLimit => new(RecordContentState.Omitted,
                "Not kept: the capture had reached its content limit when this record arrived; "
                + (fragment.OriginalLength is { } original
                    ? string.Create(culture, $"{what} was {original:N0} bytes.")
                    : $"the length of {what} was not stated."),
                null),

            // An empty message is kept whole too, and "0 bytes of" it would read as though its bytes were lost.
            _ when fragment.Kept == 0 && fragment.Offset is null or 0 => new(RecordContentState.Kept,
                $"Kept whole: {what}, which held no bytes.",
                null),
            _ => new(RecordContentState.Kept,
                string.Create(culture, $"Kept whole: {fragment.Kept:N0} bytes of {what}{from}, {encoding}.") + hidden,
                null),
        };
    }

    /// <summary>A fragment's classification as the noun phrase a statement uses (`EN-ContentClassification`).</summary>
    internal static string Classification(ContentClassificationV1 classification) => classification switch
    {
        ContentClassificationV1.OpaqueProviderData => "opaque provider data",
        ContentClassificationV1.TransportFragment => "a transport fragment",
        ContentClassificationV1.ApplicationPayload => "an application payload",
        ContentClassificationV1.DecodedFields => "decoded fields",
        ContentClassificationV1.EncryptedContent => "encrypted content",
        _ => "content",
    };
}
