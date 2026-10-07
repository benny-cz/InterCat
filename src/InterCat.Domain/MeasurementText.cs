using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// What a measurement counts, in one set of words for each dimension (R5): its basis (§5.3's `EN-Basis`), which the
/// window's ranking selector and <c>icat metric</c> name, and its byte domain, which a record's size and <c>icat metric</c>
/// say. A value this version does not know is named by its number, never guessed.
/// </summary>
public static class MeasurementText
{
    /// <summary>"source observations", "logical operations" or "resource topology".</summary>
    public static string Basis(AnalysisBasis basis) => basis switch
    {
        AnalysisBasis.SourceObservations => "source observations",
        AnalysisBasis.LogicalOperations => "logical operations",
        AnalysisBasis.ResourceTopology => "resource topology",
        _ => string.Create(CultureInfo.InvariantCulture, $"basis {(int)basis}"),
    };

    /// <summary>
    /// Which bytes a size counts, as it follows the size: "carried by the transport", "of the application's own message",
    /// or "a capacity rather than a transfer". One domain's bytes are never another's (P3).
    /// </summary>
    public static string Domain(ByteDomain domain) => domain switch
    {
        ByteDomain.TransportObserved => "carried by the transport",
        ByteDomain.RequestedIo => "requested by an I/O",
        ByteDomain.CompletedIo => "completed by an I/O",
        ByteDomain.ApplicationPayload => "of the application's own message",
        ByteDomain.CapturedContent => "of captured content",
        ByteDomain.Capacity => "a capacity rather than a transfer",
        _ => string.Create(CultureInfo.InvariantCulture, $"byte domain {(int)domain}"),
    };
}
