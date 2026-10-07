using System.Globalization;
using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>
/// Why a record binds to no process instance, in one set of words for every layer that says it (R5): the window's evidence
/// rows, and <c>icat processes</c>, <c>metric</c>, <c>operations</c> and <c>exchanges</c>. Each reason reads as a phrase
/// about the record, after "owner unresolved:" or alone in a column. A reason this version does not know is named by its
/// number, never guessed.
/// </summary>
public static class BindingText
{
    public static string Reason(ProcessBindingReason reason) => reason switch
    {
        ProcessBindingReason.Bound => "bound",
        ProcessBindingReason.NoOwner => "the record names no owner",
        ProcessBindingReason.BeforeFirstEvidence => "before its PID's first lifecycle record",
        ProcessBindingReason.BetweenInstances => "between one instance of its PID exiting and the next being created",
        ProcessBindingReason.AfterExit => "after its PID's last instance exited",

        // Any binding weaker than the policy admits: a reused PID's candidate under the default policy, and also a PID's
        // first instance's correlated binding under direct evidence only.
        ProcessBindingReason.NotAdmittedByPolicy => "a binding the evidence policy does not admit",
        ProcessBindingReason.ExecutableUnknown => "the process is known, but its full image path was not witnessed",
        ProcessBindingReason.PeerEndpointIncomplete => "the record carries no complete endpoint pair to find its other end",
        ProcessBindingReason.PeerNotObserved => "no record in this capture holds the other end (a remote or unobserved peer)",
        ProcessBindingReason.PeerAmbiguous => "more than one process holds the other end",
        ProcessBindingReason.PeerUnbound => "the other end's records bind to no process instance",
        ProcessBindingReason.NoRelationRule => "no relation rule covers this mechanism yet",
        ProcessBindingReason.CallNotLinked => "an RPC call whose other end was not linked through ALPC",
        _ => string.Create(CultureInfo.InvariantCulture, $"reason {(int)reason}"),
    };
}
