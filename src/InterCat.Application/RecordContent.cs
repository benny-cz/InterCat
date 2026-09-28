using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// What a record holds of the message it describes - the bytes a process sent or received, a call's arguments - and,
/// when nothing, why, and which source could hold them (§3.7, §11). No source InterCat admits records content: each
/// carries metadata about a transfer, a call or a message, and the statement says which, rather than leaving a person to
/// wonder whether the bytes were dropped.
/// </summary>
/// <param name="Recorded">Whether the session holds the record's content; false for every source admitted today.</param>
/// <param name="Reason">Why it holds none, as a sentence: what the record's source carries instead.</param>
/// <param name="Elsewhere">Which source could hold it, as a sentence, when one is known; null when none is.</param>
public sealed record RecordContent(bool Recorded, string Reason, string? Elsewhere)
{
    /// <summary>The statement as the inspector and the command line give it: "None. {reason} {elsewhere}".</summary>
    public string Describe() => (Recorded ? "Recorded. " : "None. ") + Reason + (Elsewhere is null ? string.Empty : " " + Elsewhere);

    /// <summary>
    /// The content of <paramref name="row"/>'s record: none, with the reason its source gives. A redacted package's record
    /// is synthetic (<paramref name="synthetic"/>) and keeps nothing of any original.
    /// </summary>
    public static RecordContent Of(ObservationRowV1 row, bool synthetic = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (synthetic)
        {
            return new(false, "A redacted package keeps no body or content of any record; its records are synthetic.", null);
        }

        return row.Mechanism switch
        {
            Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle =>
                new(false, "A lifecycle record describes a process or thread, not a message.", null),
            Mechanism.Tcp or Mechanism.Udp => new(false,
                "The kernel's network events carry endpoints and a transfer's size, never the bytes sent.",
                "A packet capture could hold them; encrypted traffic stays encrypted there."),
            Mechanism.Rpc => new(false,
                "RPC's events carry a call's interface, procedure and status, never its arguments.",
                "No driverless source is known to carry them; instrumenting the process could."),
            Mechanism.Alpc => new(false, "ALPC's kernel events carry a message id, never the message.", null),
            _ => new(false, "This record's source carries metadata only.", null),
        };
    }
}
