using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Analysis;

/// <summary>
/// A coverage ledger's facts in words, one mapping for each of its dimensions (R5): how an epoch's evidence was acquired,
/// what each loss layer reported, and why a delivered record was not admitted. A coverage reason says a loss in these
/// words, and <c>icat session</c> lists the ledger in them. A value this version does not know is named by its number.
/// </summary>
public static class CoverageLedgerText
{
    /// <summary>How an epoch's evidence was acquired: "ETL import" or "live capture".</summary>
    public static string Acquisition(CoverageAcquisition acquisition) => acquisition switch
    {
        CoverageAcquisition.EtlImport => "ETL import",
        CoverageAcquisition.LiveCapture => "live capture",
        _ => string.Create(CultureInfo.InvariantCulture, $"acquisition {(int)acquisition}"),
    };

    /// <summary>
    /// What one loss layer reported, as a coverage reason says it: "the session reported 3 lost events, which may be any
    /// mechanism's", or of an imported file, "the file reported …". A reported zero is said, never left out: an absent
    /// counter is not a reported zero (R21).
    /// </summary>
    public static string Loss(CoverageLossV1 loss, CoverageAcquisition acquisition)
    {
        ArgumentNullException.ThrowIfNull(loss);
        return loss.Layer switch
        {
            LossLayer.SourceSession => acquisition == CoverageAcquisition.EtlImport
                ? $"the file reported {Count(loss.Lost, "lost event")}, which may be any mechanism's"
                : $"the session reported {Count(loss.Lost, "lost event")}, which may be any mechanism's",
            LossLayer.ConsumerBuffers => $"the consumer lost {Count(loss.Lost, "buffer")} of unknown size",
            LossLayer.CallbackQueue => $"InterCat's full queue dropped {Count(loss.Lost, "record")}",
            LossLayer.Storage => $"{Count(loss.Lost, "admitted record")} could not be stored",
            _ => string.Create(CultureInfo.InvariantCulture, $"{Count(loss.Lost, "record")} lost at layer {(int)loss.Layer}"),
        };
    }

    /// <summary>Why a delivered record was not admitted: not lost, and a wider policy could admit it.</summary>
    public static string Omission(OmissionReason reason) => reason switch
    {
        OmissionReason.UnrequestedProvider => "a provider the capture did not request",
        OmissionReason.DescriptorNotAdmitted => "not in the profile's allowlist",
        OmissionReason.DescriptorDenied => "denied by the profile",
        _ => string.Create(CultureInfo.InvariantCulture, $"omission {(int)reason}"),
    };

    private static string Count(long count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");
}
