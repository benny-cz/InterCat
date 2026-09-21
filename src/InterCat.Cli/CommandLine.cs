using System.Globalization;

namespace InterCat.Cli;

/// <summary>
/// A small argument reader. An unrecognised option is refused with the accepted form rather than ignored,
/// because a silently dropped flag changes what a recorded run means (section 20.4, section 20.6 UserInput).
/// </summary>
internal sealed class CommandLine
{
    private readonly List<string> arguments;

    public CommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        this.arguments = [.. arguments];
    }

    public string? TakeOption(string name)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                return null;
            }

            string value = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            return value;
        }

        return null;
    }

    public int? TakeIntegerOption(string name)
    {
        string? raw = TakeOption(name);
        if (raw is null)
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    public bool TryTakeFlag(string name)
    {
        int index = arguments.IndexOf(name);
        if (index < 0)
        {
            return false;
        }

        arguments.RemoveAt(index);
        return true;
    }

    public string? TakePositional()
    {
        foreach (string argument in arguments)
        {
            if (!argument.StartsWith('-'))
            {
                arguments.Remove(argument);
                return argument;
            }
        }

        return null;
    }

    public bool TryReportUnknown(out string? unknown)
    {
        unknown = arguments.Count == 0 ? null : arguments[0];
        return unknown is not null;
    }
}
