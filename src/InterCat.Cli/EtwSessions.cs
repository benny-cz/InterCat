namespace InterCat.Cli;

/// <summary>
/// Whether this process may start an ETW session of its own, as icat record and icat measure do: ETW is Windows', and
/// starting a session of it needs an elevated shell. A refusal says which of the two stands in the way, and what works
/// without it.
/// </summary>
internal static class EtwSessions
{
    /// <summary>
    /// Why <c>icat <paramref name="command"/></c> may not start its session here, or null when it may: away from Windows,
    /// with <paramref name="elsewhere"/>; on Windows without elevation, with <paramref name="unelevated"/>.
    /// </summary>
    public static string? Refusal(string command, bool elevated, string unelevated, string elsewhere) =>
        !OperatingSystem.IsWindows()
            ? $"icat {command} needs Windows: it starts an ETW session, which Windows alone has. {elsewhere}"
            : !elevated
                ? $"Starting an ETW session needs an elevated shell: run icat {command} from one. {unelevated}"
                : null;

    /// <summary>What a measurement's refusal names that works without its session.</summary>
    public const string MeasureUnelevated = "icat capabilities says what this machine can record without one.";

    /// <inheritdoc cref="MeasureUnelevated"/>
    public const string MeasureElsewhere = "Here, icat verify re-evaluates a TCP or UDP run measured on Windows.";
}
