using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Plain-language text for one admitted evidence row, shared by the viewer and the command line so both describe a
/// row the same way. It restates what the row records and nothing more: a missing size says why it is missing, an
/// unresolved owner says why, and a direction is the one the record's own kind states.
/// </summary>
public static class EvidenceRowText
{
    /// <summary>What happened, in words: "TCP send", "Process start", "Process running at capture start".</summary>
    public static string Title(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Mechanism switch
        {
            Mechanism.ProcessLifecycle => row.Kind switch
            {
                ObservationKind.Create => "Process start",
                ObservationKind.Exit => "Process exit",
                ObservationKind.Inventory => "Process running at capture start",
                _ => "Process " + Verb(row.Kind),
            },
            _ => MechanismName(row.Mechanism) + " " + Verb(row.Kind),
        };
    }

    /// <summary>The row's session-relative time in seconds, or why it has none.</summary>
    public static string When(ObservationRowV1 row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.SessionRelativeTicks is { } nanoseconds
            ? string.Create(culture ?? CultureInfo.CurrentCulture,
                $"{(nanoseconds < 0 ? "−" : "+")}{Math.Abs((decimal)nanoseconds) / 1_000_000_000m:0.000000} s")
            : "time unavailable";
    }

    /// <summary>
    /// The record's own endpoint and the remote one, with the direction its kind states: an arrow for data sent or
    /// received, a double arrow for a connection event. Null when the record carries no endpoint pair.
    /// </summary>
    public static string? Endpoints(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.EndpointAddressFamily is not { } family) return null;
        (string? source, string? destination) = family == 6
            ? (Endpoint(row.SourceEndpointAddressV6, row.SourceEndpointPort),
                Endpoint(row.DestinationEndpointAddressV6, row.DestinationEndpointPort))
            : (Endpoint(row.SourceEndpointAddress, row.SourceEndpointPort),
                Endpoint(row.DestinationEndpointAddress, row.DestinationEndpointPort));
        if (source is null || destination is null) return source ?? destination;
        (string own, string remote) = TransportEndpoints.OrientationOf(row.Mechanism, row.Kind) == EndpointOrientation.OwnerFirst
            ? (source, destination)
            : (destination, source);
        return row.Kind switch
        {
            ObservationKind.Send => $"{own} → {remote}",
            ObservationKind.Receive => $"{own} ← {remote}",
            _ => $"{own} ↔ {remote}",
        };
    }

    /// <summary>
    /// The measured size with its unit, or why the record holds none - "size not exposed" when the source withholds it,
    /// "size redacted", "size lost with its event" - or null when no size applies.
    /// </summary>
    public static string? Size(ObservationRowV1 row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ByteValue is { } bytes)
            return string.Create(culture ?? CultureInfo.CurrentCulture, $"{bytes:N0} B");
        return Absent(row.ByteAvailability) is { } absent ? "size " + absent : null;
    }

    /// <summary>
    /// Why a record holds no size, in <see cref="ObservationText.Absence"/>'s words, or null when no size applies. A size
    /// said to be present that the record does not hold is not recorded, never read as zero.
    /// </summary>
    private static string? Absent(FieldAvailability availability) => availability switch
    {
        FieldAvailability.NotApplicable => null,
        FieldAvailability.Present => "not recorded",
        _ => ObservationText.Absence(availability),
    };

    /// <summary>
    /// The size with what it measures, in words (R5): "1,460 B carried by the transport", "604 B of the application's own
    /// message", or why there is none, "not exposed", where a label already says it is the size. A size's domain says
    /// which bytes were counted, and one domain's bytes are never another's (P3).
    /// </summary>
    public static string? SizeWithDomain(ObservationRowV1 row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ByteValue is null) return Absent(row.ByteAvailability);
        if (Size(row, culture) is not { } size) return null;
        if (row.ByteDomain is not { } domain) return size;
        return size + (domain == ByteDomain.Capacity ? ", " : " ") + MeasurementText.Domain(domain);
    }

    /// <summary>
    /// The PID the row belongs to under the binding rule: the owner its payload names, or the process that raised it
    /// when its mechanism's records are raised in the process they describe (<see cref="RecordAttribution"/>). Null
    /// when neither applies.
    /// </summary>
    public static int? OwnerProcessId(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.HeaderProcessId);
    }

    /// <summary>
    /// Who canonically owns the row: the resolved instance's executable and PID, a candidate named as one, or the
    /// PID with the reason no instance holds it. Without a resolution only the PID the record belongs to is stated.
    /// </summary>
    public static string Owner(SessionEvidenceRecord record, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        int? processId = OwnerProcessId(record.Observation);

        // An owner the payload does not name is the process that raised the record (ADR-030), and is said to be: the
        // record's attribution quality describes its payload, which names none.
        string raisedBy = processId is not null && record.Observation.OwnerProcessId is null ? "raised by " : string.Empty;
        string pid = processId is { } id
            ? string.Create(format, $"{raisedBy}PID {id}")
            : "no owner process named";
        if (record.Owner is not { } owner) return pid;
        if (owner.Instance is null)
            return processId is null ? pid : pid + " · owner unresolved: " + BindingText.Reason(owner.Reason);
        string named = string.IsNullOrWhiteSpace(owner.ImageName)
            ? string.Create(format, $"{raisedBy}PID {owner.ProcessId} · executable not witnessed")
            : string.Create(format, $"{raisedBy}{owner.ImageName} · PID {owner.ProcessId}");
        string qualifier = owner.Strength == RelationStrength.Candidate ? " · candidate"
            : owner.Strength == RelationStrength.Conflicting ? " · conflicting" : string.Empty;
        string admitted = owner.AdmittedUnderPolicy ? string.Empty : " · not admitted by the evidence policy";
        return named + qualifier + admitted;
    }

    /// <summary>
    /// The owner as a clause of a sentence about the record: "owned by client.exe · PID 100", or, where the payload names
    /// no owner, "raised by …" or "with no owner process named", which "owned by" would misstate.
    /// </summary>
    public static string Ownership(SessionEvidenceRecord record, IFormatProvider? culture = null)
    {
        string owner = Owner(record, culture);
        return owner.StartsWith("raised by ", StringComparison.Ordinal) ? owner
            : owner.StartsWith("no owner", StringComparison.Ordinal) ? "with " + owner
            : "owned by " + owner;
    }

    /// <summary>One line for a list: time, what happened, size and endpoints where the record has them.</summary>
    public static string Summary(SessionEvidenceRecord record, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObservationRowV1 row = record.Observation;
        var parts = new List<string> { When(row, culture), Title(row) };
        if (Size(row, culture) is { } size) parts.Add(size);
        if (Endpoints(row) is { } endpoints) parts.Add(endpoints);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The provider's registered name for the two providers InterCat's validated sources use, whose identifiers are
    /// fixed by Windows; any other provider is named by its identifier rather than by a guess.
    /// </summary>
    /// <summary>
    /// A provider's public name, when it is one of the providers InterCat's catalog admits. Any other provider is named by
    /// its identifier - which in a redacted package is a pseudonym, and is said to be one.
    /// </summary>
    public static string ProviderName(Guid provider, bool pseudonymous = false) =>
        RedactedSessionPseudonyms.PublicProviders.TryGetValue(provider, out string? name)
            ? name
            : pseudonymous
                ? RedactedSessionPseudonyms.PseudonymousProviderName(provider) + " (pseudonym)"
                : "provider " + provider.ToString("D");

    /// <summary>
    /// A record's four quality dimensions in words: "attribution proven, correlation unknown, measurement unknown, timing
    /// proven". Exports keep the enumeration names, which a program reads; a person reads these.
    /// </summary>
    public static string Quality(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"attribution {QualityName(row.AttributionQuality)}, correlation {QualityName(row.CorrelationQuality)}, "
            + $"measurement {QualityName(row.MeasurementQuality)}, timing {QualityName(row.TimingQuality)}";
    }

    /// <summary>One quality level as a word; a level this version does not know is named by its number, never guessed.</summary>
    public static string QualityName(QualityLevel level) => level switch
    {
        QualityLevel.Proven => "proven",
        QualityLevel.Qualified => "qualified",
        QualityLevel.Weak => "weak",
        QualityLevel.UnknownQuality => "unknown",
        _ => string.Create(CultureInfo.InvariantCulture, $"level {(int)level}"),
    };

    /// <summary>A mechanism as a lane or a row names it (<see cref="MechanismText.Name"/>).</summary>
    public static string MechanismName(Mechanism mechanism) => MechanismText.Name(mechanism);

    /// <summary>A kind as a title's verb (<see cref="ObservationText.Kind"/>); a record of unknown kind is a record.</summary>
    private static string Verb(ObservationKind kind) => kind == ObservationKind.UnknownKind ? "record" : ObservationText.Kind(kind);

    private static string? Endpoint(uint? address, ushort? port) => address is { } value && port is { } number
        ? EndpointText.Endpoint(value, number)
        : null;

    private static string? Endpoint(UInt128? address, ushort? port) => address is { } value && port is { } number
        ? EndpointText.Endpoint(value, number)
        : null;
}
