using System.Globalization;
using InterCat.Analysis;

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
}
