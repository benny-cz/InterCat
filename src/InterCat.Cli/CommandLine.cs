using System.Buffers;
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
    /// <summary>What an option's name is written in, after its dashes.</summary>
    private static readonly SearchValues<char> OptionNameCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-");

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

    /// <summary>
    /// How a refusal names an argument no part of the command took: an option it does not know or that is missing its value,
    /// an operand that starts with a dash and so was read as one, which follows <c>--</c> instead, or an operand past the
    /// last the command takes.
    /// </summary>
    public static string Unknown(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return !argument.StartsWith('-') ? $"Unexpected argument: {argument}"
            : NamesAnOption(argument) ? $"Unknown or incomplete option: {argument}"
            : $"Unknown or incomplete option: {argument} - an operand that starts with a dash follows --, as in -- \"{argument}\"";
    }

    public bool TryReportUnknown(out string? unknown)
    {
        unknown = arguments.Count > 0 ? arguments[0] : operands.TryPeek(out string? operand) ? operand : null;
        return unknown is not null;
    }

    /// <summary>Whether an argument is written as an option is: a dash or two, then a letter, then letters, digits and dashes.</summary>
    private static bool NamesAnOption(string argument)
    {
        int name = argument.StartsWith("--", StringComparison.Ordinal) ? 2 : 1;
        return argument.Length > name && char.IsAsciiLetter(argument[name])
            && argument.AsSpan(name).IndexOfAnyExcept(OptionNameCharacters) < 0;
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
