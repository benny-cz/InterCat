using System.Globalization;

namespace InterCat.Cli;

/// <summary>
/// A small argument reader. An unrecognised option is refused with the accepted form rather than ignored,
/// because a silently dropped flag changes what a recorded run means (section 20.4, section 20.6 UserInput).
/// Options may come before, between or after the positional arguments, so every option that takes a value is
/// read first: what is left that names no option is positional.
/// </summary>
internal sealed class CommandLine
{
    private readonly List<string> arguments;

    /// <summary>What follows a bare <c>--</c>: operands only, never options, so a note or a name may start with a dash.</summary>
    private readonly Queue<string> operands;

    private bool positionalTaken;

    public CommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        List<string> all = [.. arguments];
        int separator = all.IndexOf("--");
        this.arguments = separator < 0 ? all : all[..separator];
        operands = new(separator < 0 ? [] : all[(separator + 1)..]);
    }

    /// <summary>
    /// The value after <paramref name="name"/>, removing both; null when the option is absent or ends the line. A value
    /// is read before any positional argument, which could otherwise have been this value: once misread, `icat metric
    /// --metric observations &lt;session&gt;` looked for a session named "observations".
    /// </summary>
    public string? TakeOption(string name)
    {
        if (positionalTaken)
        {
            throw new InvalidOperationException(
                $"{name} is read after a positional argument, which may have been its value: read every option first.");
        }

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

    /// <summary>
    /// The first argument when it names no option: a verb, such as <c>tcp</c> in <c>icat measure tcp</c>, that decides
    /// which options follow and so is read before them. No option's value can come first.
    /// </summary>
    public string? TakeVerb()
    {
        if (arguments.Count == 0 || IsOption(arguments[0]))
        {
            return null;
        }

        string verb = arguments[0];
        arguments.RemoveAt(0);
        return verb;
    }

    /// <summary>The next argument that names no option, then the next after a bare <c>--</c>; null when none is left.</summary>
    public string? TakePositional()
    {
        positionalTaken = true;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!IsOption(arguments[index]))
            {
                string argument = arguments[index];
                arguments.RemoveAt(index);
                return argument;
            }
        }

        return operands.TryDequeue(out string? operand) ? operand : null;
    }

    public bool TryReportUnknown(out string? unknown)
    {
        unknown = arguments.Count > 0 ? arguments[0] : operands.TryPeek(out string? operand) ? operand : null;
        return unknown is not null;
    }

    /// <summary>
    /// Whether an argument names an option: it starts with a dash and is not a negative number, such as a view that starts
    /// before the investigation's epoch.
    /// </summary>
    private static bool IsOption(string argument) =>
        argument.StartsWith('-')
        && !(argument.Length > 1
            && (char.IsAsciiDigit(argument[1]) || (argument[1] == '.' && argument.Length > 2 && char.IsAsciiDigit(argument[2]))));
}
