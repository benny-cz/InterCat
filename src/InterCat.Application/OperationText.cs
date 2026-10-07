using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Plain-language text for a derived operation, shared by the viewer and the command line so both state a call the
/// same way (`contracts/operations-v1.md`).
/// </summary>
public static class OperationText
{
    /// <summary>
    /// A duration in the unit that keeps it readable: nanoseconds below a microsecond, then microseconds, milliseconds
    /// and seconds, with one decimal below 100 of the unit: "4.8 µs", "26.1 µs", "457 µs", "1.2 ms". A value that would
    /// round up to 1,000 of a unit is written in the next one.
    /// </summary>
    public static string Duration(long nanoseconds, IFormatProvider? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);
        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        if (nanoseconds < 1_000)
        {
            return string.Create(provider, $"{nanoseconds:N0} ns");
        }

        (decimal value, string unit) = nanoseconds switch
        {
            < 999_500 => (nanoseconds / 1_000m, "µs"),
            < 999_500_000 => (nanoseconds / 1_000_000m, "ms"),
            _ => (nanoseconds / 1_000_000_000m, "s"),
        };
        return string.Create(provider, $"{value.ToString(value < 99.95m ? "N1" : "N0", provider)} {unit}");
    }

    /// <summary>
    /// A bound written as <see cref="Duration"/> writes a duration, but rounded up at the precision written, so it never
    /// reads smaller than it is: 500.05 µs is "501 µs", where a duration would read "500 µs".
    /// </summary>
    public static string DurationAtLeast(double nanoseconds, IFormatProvider? culture = null)
    {
        if (!double.IsFinite(nanoseconds) || nanoseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nanoseconds), nanoseconds, "A bound is a finite, non-negative duration.");
        }

        long whole = checked((long)Math.Ceiling(nanoseconds));
        long step = whole switch
        {
            < 1_000 => 1,
            < 99_950 => 100,
            < 999_500 => 1_000,
            < 99_950_000 => 100_000,
            < 999_500_000 => 1_000_000,
            < 99_950_000_000 => 100_000_000,
            _ => 1_000_000_000,
        };
        return Duration(checked((whole + step - 1) / step * step), culture);
    }

    /// <summary>What a call's records establish, in words.</summary>
    public static string State(RpcCallState state) => state switch
    {
        RpcCallState.Completed => "completed",
        RpcCallState.OpenAtCaptureEnd => "open at capture end",
        RpcCallState.StartNotObserved => "start not observed",
        RpcCallState.NoActivityId => "no activity id",
        RpcCallState.Ambiguous => "ambiguous: its activity id was reused before its stop",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No call is in this state."),
    };

    /// <summary>
    /// An operation's state (<c>EN-OperationState</c>) in the words a call's state reads (R5): "started", "completed",
    /// "failed", "start not observed", "open at capture end", censored rather than failed, "ambiguous", and "evicted
    /// unresolved" where the bounded pending state gave it up before it paired. A state this version does not know is
    /// named by its number, never guessed.
    /// </summary>
    public static string State(OperationState state) => state switch
    {
        OperationState.Started => "started",
        OperationState.Completed => "completed",
        OperationState.ExplicitlyFailed => "failed",
        OperationState.OrphanCompletion => "start not observed",
        OperationState.OpenAtBoundary => "open at capture end",
        OperationState.Ambiguous => "ambiguous",
        OperationState.EvictedUnresolved => "evicted unresolved",
        _ => string.Create(CultureInfo.InvariantCulture, $"state {(int)state}"),
    };

    /// <summary>What a client call's other end is, in words (`contracts/operations-v1.md` §5c).</summary>
    public static string PeerState(RpcPeerState state) => state switch
    {
        RpcPeerState.Served => "served",
        RpcPeerState.NoAlpcEvidence => "the capture collected no ALPC",
        RpcPeerState.NotCompleted => "not completed, so no window to follow",
        RpcPeerState.NoSend => "no ALPC send on its thread during the call",
        RpcPeerState.SeveralSends => "several ALPC sends during the call",
        RpcPeerState.NoMessageId => "its ALPC send carried no message id",
        RpcPeerState.NoReceive => "no other process received its message",
        RpcPeerState.SeveralReceives => "several other processes received its message",
        RpcPeerState.NoServerCall => "the receiving thread began no server call within 5 ms",
        RpcPeerState.CannotCheck => "an interface or procedure is missing, so the link cannot be checked",
        RpcPeerState.Conflicting => "the server call names another interface or procedure",
        RpcPeerState.ServerCallShared => "another client call reached the same server call",
        RpcPeerState.NotReached => "no linked client call reached it",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No other end is in this state."),
    };
}
