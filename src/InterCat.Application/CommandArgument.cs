namespace InterCat.Application;

/// <summary>
/// A value in an icat command the window or icat cites for a person to type, put so a command line takes it whole.
/// </summary>
public static class CommandArgument
{
    /// <summary>
    /// The value as one argument, in cmd and PowerShell alike: as it is where it holds only letters, digits and characters
    /// neither shell reads - svchost.exe, C:\Apps\updater.exe - else in double quotes, as a program two folders tell apart is
    /// named: "git.exe (cmd)". No program's name or executable's path holds a double quote.
    /// </summary>
    public static string Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || @"._-\/:+".Contains(character))
            ? value
            : $"\"{value}\"";
    }

    /// <summary>An enumeration's value as a person types it: DirectOnly as direct-only, BytesSent as bytes-sent.</summary>
    public static string Typed(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
    }
}
