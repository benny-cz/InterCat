using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// Plain-language text for one admitted evidence row, shared by the viewer and the command line so both describe a
/// row the same way. It restates what the row records and nothing more: a missing size stays "not exposed", an
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
        if (family != 4) return "IPv" + family.ToString(CultureInfo.InvariantCulture) + " endpoints (address not retained)";
        string? source = Endpoint(row.SourceEndpointAddress, row.SourceEndpointPort);
        string? destination = Endpoint(row.DestinationEndpointAddress, row.DestinationEndpointPort);
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

    /// <summary>The measured size with its unit, "size not exposed" when the source withholds it, or null when no size applies.</summary>
    public static string? Size(ObservationRowV1 row, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ByteValue is { } bytes)
            return string.Create(culture ?? CultureInfo.CurrentCulture, $"{bytes:N0} B");
        return row.ByteAvailability == FieldAvailability.NotApplicable ? null : "size not exposed";
    }

    /// <summary>
    /// Who canonically owns the row: the resolved instance's executable and PID, a candidate named as one, or the
    /// PID with the reason no instance holds it. Without a resolution only the PID the record names is stated.
    /// </summary>
    public static string Owner(SessionEvidenceRecord record, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        string pid = record.Observation.OwnerProcessId is { } id
            ? string.Create(format, $"PID {id}")
            : "no owner process named";
        if (record.Owner is not { } owner) return pid;
        if (owner.Instance is null)
            return record.Observation.OwnerProcessId is null ? pid : pid + " · owner unresolved: " + Reason(owner.Reason);
        string named = string.IsNullOrWhiteSpace(owner.ImageName)
            ? string.Create(format, $"PID {owner.ProcessId} · executable not witnessed")
            : string.Create(format, $"{owner.ImageName} · PID {owner.ProcessId}");
        string qualifier = owner.Strength == RelationStrength.Candidate ? " · candidate"
            : owner.Strength == RelationStrength.Conflicting ? " · conflicting" : string.Empty;
        string admitted = owner.AdmittedUnderPolicy ? string.Empty : " · not admitted by the evidence policy";
        return named + qualifier + admitted;
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
    public static string ProviderName(Guid provider) => provider.ToString("D") switch
    {
        "7dd42a49-5329-4832-8dfd-43d979153a88" => "Microsoft-Windows-Kernel-Network",
        "22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716" => "Microsoft-Windows-Kernel-Process",
        string other => "provider " + other,
    };

    public static string MechanismName(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle => "Process",
        Mechanism.ThreadLifecycle => "Thread",
        Mechanism.Tcp => "TCP",
        Mechanism.Udp => "UDP",
        Mechanism.UnixDomainSocket => "Unix socket",
        Mechanism.NamedPipe => "Named pipe",
        Mechanism.AnonymousPipe => "Anonymous pipe",
        Mechanism.Rpc => "RPC",
        Mechanism.Alpc => "ALPC",
        Mechanism.SharedSection => "Shared section",
        Mechanism.ComActivation => "COM activation",
        Mechanism.WindowMessage => "Window message",
        Mechanism.RemoteFileOrSmb => "Remote file",
        Mechanism.Quic => "QUIC",
        Mechanism.Dde => "DDE",
        Mechanism.ApplicationSdk => "Application SDK",
        Mechanism.UnknownMechanism => "Unknown mechanism",
        _ => mechanism.ToString(),
    };

    private static string Verb(ObservationKind kind) => kind switch
    {
        ObservationKind.RequestStart => "request start",
        ObservationKind.RequestEnd => "request end",
        ObservationKind.UnknownKind => "record",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Reason(ProcessBindingReason reason) => reason switch
    {
        ProcessBindingReason.NoOwner => "the record names no owner",
        ProcessBindingReason.BeforeFirstEvidence => "earlier than this PID's first lifecycle record",
        ProcessBindingReason.BetweenInstances => "between two instances of this PID",
        ProcessBindingReason.AfterExit => "after this PID's last instance exited",
        ProcessBindingReason.NotAdmittedByPolicy => "a reused PID's candidate the evidence policy does not admit",
        _ => reason.ToString(),
    };

    private static string? Endpoint(uint? address, ushort? port) => address is { } value && port is { } number
        ? string.Create(CultureInfo.InvariantCulture,
            $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}:{number}")
        : null;
}
