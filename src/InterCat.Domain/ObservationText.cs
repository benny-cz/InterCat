using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// A record's kind and layer, and why one of its fields holds no value, as InterCat names them to a person, one mapping
/// for every layer (R5): the window's rows and inspector and the command line's tables say "request start" where the
/// enumeration says <c>RequestStart</c>. A value this version does not know is named by its number, never guessed.
/// </summary>
public static class ObservationText
{
    /// <summary>A record's kind as a word: "send", "request start", "unknown kind".</summary>
    public static string Kind(ObservationKind kind) => kind switch
    {
        ObservationKind.Send => "send",
        ObservationKind.Receive => "receive",
        ObservationKind.RequestStart => "request start",
        ObservationKind.RequestEnd => "request end",
        ObservationKind.Open => "open",
        ObservationKind.Close => "close",
        ObservationKind.Bind => "bind",
        ObservationKind.Connect => "connect",
        ObservationKind.Accept => "accept",
        ObservationKind.Disconnect => "disconnect",
        ObservationKind.Map => "map",
        ObservationKind.Unmap => "unmap",
        ObservationKind.Wait => "wait",
        ObservationKind.Signal => "signal",
        ObservationKind.Create => "create",
        ObservationKind.Exit => "exit",
        ObservationKind.Inventory => "inventory",
        ObservationKind.Error => "error",
        ObservationKind.Discovery => "discovery",
        ObservationKind.UnknownKind => "unknown kind",
        _ => string.Create(CultureInfo.InvariantCulture, $"kind {(int)kind}"),
    };

    /// <summary>A record's layer as a word: "transport", "application", "lifecycle".</summary>
    public static string Layer(ObservationLayer layer) => layer switch
    {
        ObservationLayer.Transport => "transport",
        ObservationLayer.Application => "application",
        ObservationLayer.Resource => "resource",
        ObservationLayer.Lifecycle => "lifecycle",
        ObservationLayer.Collector => "collector",
        _ => string.Create(CultureInfo.InvariantCulture, $"layer {(int)layer}"),
    };

    /// <summary>
    /// Why a field holds no value, as it follows the field's name - "size not exposed", "size redacted" - or stands alone
    /// in a column: what the source withheld is not what the profile left out, what was denied, or what a lost event took
    /// with it (R21).
    /// </summary>
    public static string Absence(FieldAvailability availability) => availability switch
    {
        FieldAvailability.Present => "present",
        FieldAvailability.NotExposed => "not exposed",
        FieldAvailability.ProfileDisabled => "not collected by the profile",
        FieldAvailability.Denied => "denied",
        FieldAvailability.EventLost => "lost with its event",
        FieldAvailability.SchemaUnknown => "not decoded: schema unknown",
        FieldAvailability.Redacted => "redacted",
        FieldAvailability.NotApplicable => "not applicable",
        _ => string.Create(CultureInfo.InvariantCulture, $"availability {(int)availability}"),
    };
}
