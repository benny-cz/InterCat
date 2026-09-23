using System.Globalization;

namespace InterCat.CaptureBroker;

/// <summary>
/// The broker's launch contract. The launching client names its own user SID and logon session (so the pipe admits
/// exactly that token after an elevation that may use another account) and the pipe instance it will connect to.
/// None of these is trusted as authentication: every connection is still checked against the impersonated token.
/// </summary>
public sealed record BrokerLaunchOptions(
    BrokerOwnerIdentity Owner,
    Guid ServerInstanceId,
    TimeSpan IdleExitAfter)
{
    public const string ServeCommand = "serve";

    public static readonly TimeSpan MinimumIdleExit = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumIdleExit = TimeSpan.FromHours(24);

    public static string Usage =>
        $"""
        Usage: InterCat.CaptureBroker {ServeCommand} --owner-sid <SID> --owner-logon-session <LUID>
                 --instance <GUID> [--idle-exit-seconds <10-86400>]

          --owner-sid            The capturing user's SID; only this user may connect.
          --owner-logon-session  That user's logon-session LUID (decimal or 0x hex) as the client's own token reports it.
          --instance             Pipe instance GUID; the broker listens on \\.\pipe\InterCat.Broker.v1.<GUID:N>.
          --idle-exit-seconds    Exit after this long with no client and no active capture (default {BrokerHostSettings.DefaultIdleExit.TotalSeconds:0}).
        """;

    /// <summary>Parses the launch arguments. Returns null with an error for anything but a complete serve request.</summary>
    public static BrokerLaunchOptions? Parse(IReadOnlyList<string> args, out BrokerLaunchParseError? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        if (args.Count == 0 || !string.Equals(args[0], ServeCommand, StringComparison.Ordinal))
        {
            error = new(BrokerLaunchParseErrorKind.NotServe, "The broker only runs as 'serve'.");
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Count; index++)
        {
            string name = args[index];
            if (name is not ("--owner-sid" or "--owner-logon-session" or "--instance" or "--idle-exit-seconds"))
            {
                error = new(BrokerLaunchParseErrorKind.Invalid, $"Unknown argument '{name}'.");
                return null;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = new(BrokerLaunchParseErrorKind.Invalid, $"'{name}' needs a value.");
                return null;
            }

            if (!values.TryAdd(name, args[++index]))
            {
                error = new(BrokerLaunchParseErrorKind.Invalid, $"'{name}' was given twice.");
                return null;
            }
        }

        if (!values.TryGetValue("--owner-sid", out string? sid)
            || !values.TryGetValue("--owner-logon-session", out string? session)
            || !values.TryGetValue("--instance", out string? instance))
        {
            error = new(
                BrokerLaunchParseErrorKind.Invalid,
                "--owner-sid, --owner-logon-session and --instance are all required.");
            return null;
        }

        if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || sid.Length > 184 || sid.Any(char.IsControl))
        {
            error = new(BrokerLaunchParseErrorKind.Invalid, "--owner-sid is not a SID.");
            return null;
        }

        if (!TryParseLuid(session, out ulong logonSession) || logonSession == 0)
        {
            error = new(BrokerLaunchParseErrorKind.Invalid, "--owner-logon-session is not a non-zero LUID.");
            return null;
        }

        if (!Guid.TryParse(instance, out Guid instanceId) || instanceId == Guid.Empty)
        {
            error = new(BrokerLaunchParseErrorKind.Invalid, "--instance is not a non-empty GUID.");
            return null;
        }

        TimeSpan idle = BrokerHostSettings.DefaultIdleExit;
        if (values.TryGetValue("--idle-exit-seconds", out string? idleText))
        {
            if (!int.TryParse(idleText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                || TimeSpan.FromSeconds(seconds) < MinimumIdleExit
                || TimeSpan.FromSeconds(seconds) > MaximumIdleExit)
            {
                error = new(BrokerLaunchParseErrorKind.Invalid, "--idle-exit-seconds must be 10 to 86400.");
                return null;
            }

            idle = TimeSpan.FromSeconds(seconds);
        }

        return new(new(sid.ToUpperInvariant(), logonSession), instanceId, idle);
    }

    private static bool TryParseLuid(string text, out ulong value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

public enum BrokerLaunchParseErrorKind
{
    /// <summary>No serve command: the historic fail-closed launch.</summary>
    NotServe = 1,

    /// <summary>A serve command that is malformed.</summary>
    Invalid = 2,
}

public sealed record BrokerLaunchParseError(BrokerLaunchParseErrorKind Kind, string Message);
