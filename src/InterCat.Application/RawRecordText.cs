using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// A retained journal envelope as a person reads it, the same in InterCat's window and in icat raw (R5, R18): its raw
/// identity, its provider named where the catalog names it, its codes in words rather than an enumeration's names, and
/// what its body kept, counted in the singular where it is one.
/// </summary>
public static class RawRecordText
{
    /// <summary>The most bytes a revealed preview shows, as both readers cap it.</summary>
    public const int PreviewBytes = 256;

    /// <summary>Said where the body kept no byte: the body's facts above give the recorded reason.</summary>
    public const string NoBody = "No body bytes were retained; the disposition above gives the recorded reason.";

    /// <summary>Said where a preview stopped short of the bytes the body kept.</summary>
    public const string PreviewStopped = "Preview stopped at 256 bytes; more retained body bytes exist.";

    /// <summary>What a redacted package's record is, in place of an original record's disclosure.</summary>
    public const string SyntheticDisclosure = "This package holds no original record. This entry carries the row's "
        + "pseudonymous descriptor, header and reading, and never a body or extended data.";

    /// <summary>
    /// Why a redacted package's row has no entry: every row of a package has one, checked as the package was saved, so the
    /// package changed since - never that an original record was released.
    /// </summary>
    public const string SyntheticMissing = "The package's journal holds no entry for this row, though every row of a "
        + "redacted package has one: the package is not as it was saved.";

    /// <summary>
    /// A record's facts, label and value, in the order both readers list them: its raw identity, and, where its journal
    /// holds it, its provider and header, clock, processor, schema, admission policy and body; what it holds of its message,
    /// which the normalized row says with or without the journal; and its extended items.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Facts(SessionRawRecordDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        RawRecordId raw = detail.ObservationId.RawRecordId;
        CultureInfo culture = CultureInfo.CurrentCulture;
        var facts = new List<(string Label, string Value)>
        {
            ("Raw record", string.Create(culture,
                $"capture {raw.CaptureId} · stream {raw.StreamId} · source epoch {raw.SourceEpoch} · ordinal {raw.RecordOrdinal}")),
        };
        if (detail.Header is not { } header || detail.BufferContext is not { } buffer)
        {
            if (detail.Content is { } held) facts.Add(("Content", held.Describe()));
            return facts;
        }

        facts.AddRange(
        [
            // A public provider is named beside its identity; one the catalog does not name is its identity alone.
            ("Provider", (KnownProviders.NameOf(header.ProviderId) is { } name
                    ? $"{name} ({header.ProviderId:N})"
                    : header.ProviderId.ToString("N"))
                + string.Create(culture,
                    $" · event {header.EventId} · version {header.Version} · opcode {header.Opcode} · task {header.Task}")),
            ("Header PID/TID", string.Create(culture,
                $"{header.ProcessId}/{header.ThreadId} · activity {header.ActivityId} · related {header.RelatedActivityId}")),
            ("Clock", string.Create(culture,
                $"{detail.ClockId} · encoding {Encoding(detail.TimestampEncoding)} · native reading {detail.NativeTicks}")),
            ("Processor", string.Create(culture,
                $"{buffer.ProcessorNumber} · logger {buffer.LoggerId} · pointer size {Bytes(detail.PointerSize)}")),
            ("Schema reference", (detail.SchemaReference?.ToString(culture) ?? "none")
                + " · fingerprint " + (detail.SchemaFingerprint ?? "unavailable")),
            ("Admission policy reference", string.Create(culture, $"{detail.AdmissionPolicyReference} · {detail.AdmissionPolicyId}")),
            ("Body", $"{Words(detail.BodyClassification)}, {Words(detail.BodyDisposition)} · original "
                + $"{Bytes(detail.OriginalBodyLength)} · retained {Bytes(detail.RetainedBodyLength)}"),
        ]);

        // The body is the event's own fields; whether the message itself was kept is a separate statement (§3.7).
        if (detail.Content is { } content)
        {
            facts.Add(("Content", content.Describe()));
        }

        facts.Add(("Extended items", string.Create(culture,
            $"retained {detail.ExtendedItems.Count:N0} · omitted {detail.OmittedExtendedItemCount ?? 0:N0}")));
        return facts;
    }

    /// <summary>One extended item: "item type 3, flags 0: original 12 bytes, retained 1 byte".</summary>
    public static string ExtendedItem(RawExtendedItemSummary item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return string.Create(CultureInfo.CurrentCulture, $"item type {item.Type}, flags {item.Flags}: original ")
            + Bytes(item.OriginalLength) + ", retained " + Bytes(item.RetainedLength);
    }

    /// <summary>A revealed preview's rows of inert hexadecimal, sixteen bytes each, each led by its offset.</summary>
    public static IEnumerable<string> PreviewRows(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            yield return $"{offset:X4}: {Convert.ToHexString(bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset)))}";
        }
    }

    /// <summary>A code's name in lower-case words: "ApprovedMetadata" reads "approved metadata"; none recorded says so.</summary>
    public static string Words<T>(T? value)
        where T : struct, Enum
    {
        if (value is not { } known) return "not recorded";
        string name = known.ToString();
        var words = new StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index])) words.Append(' ');
            words.Append(char.ToLowerInvariant(name[index]));
        }

        return words.ToString();
    }

    /// <summary>How a record's native reading is encoded, in words.</summary>
    public static string Encoding(TimestampEncoding? encoding) => encoding switch
    {
        TimestampEncoding.Qpc => "QPC (performance counter)",
        TimestampEncoding.FileTimeUtc => "UTC file time",
        TimestampEncoding.EtwSystemTimeConverted => "ETW system time, converted",
        TimestampEncoding.CpuCycleCounter => "CPU cycle counter",
        _ => "unknown",
    };

    /// <summary>A length the envelope records, "1 byte" or "64 bytes"; one it does not record says so.</summary>
    private static string Bytes(long? length) => length is { } known ? CountText.Of(known, "byte") : "not recorded";
}
