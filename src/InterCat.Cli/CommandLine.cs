using System.Buffers;
using System.Globalization;
using InterCat.Application;

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

    /// <summary>Every option the command read, so one given again is named as given twice rather than as unknown.</summary>
    private readonly HashSet<string> taken = new(StringComparer.Ordinal);

    /// <summary>
    /// Every option the command looked for, given or not, in the order it looked: what one it does not know was meant as,
    /// when it is a letter or two from one of them.
    /// </summary>
    private readonly List<string> asked = [];

    /// <summary>The options the command looked for that take a value, so one written with it after '=' is said how to give it.</summary>
    private readonly HashSet<string> valued = new(StringComparer.Ordinal);

    /// <summary>The options the command looked for that end the line, so each is said to be given without its value.</summary>
    private readonly HashSet<string> unvalued = new(StringComparer.Ordinal);

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

        Ask(name);
        valued.Add(name);
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                unvalued.Add(name);
                return null;
            }

            string value = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            taken.Add(name);
            return value;
        }

        return null;
    }

    /// <summary>
    /// The whole number after <paramref name="name"/>; null when the option is absent. A value that is not a whole number is
    /// never read as absent, which would run with the default in its place: it is kept, as its refusal, in
    /// <see cref="Malformed"/>.
    /// </summary>
    public int? TakeIntegerOption(string name)
    {
        string? raw = TakeOption(name);
        if (raw is null)
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        Malformed ??= $"{name} expects a whole number; '{raw}' is not one.";
        return null;
    }

    /// <summary>The refusal of the first value <see cref="TakeIntegerOption"/> could not read; null when it read every one.</summary>
    public string? Malformed { get; private set; }

    /// <summary>
    /// The refusal of an option's value, in the one form the command line says one in (R18): what <paramref name="option"/>
    /// expects and the value given that is not one, or, when none was given, that it is required and what it takes.
    /// </summary>
    public static string Expects(string option, string? given, string expects) =>
        given is null ? $"{option} is required: {expects}." : $"{option} expects {expects}; '{given}' is not one.";

    /// <summary>
    /// The refusal of a value that must be one of <paramref name="names"/>, in the form <see cref="Expects(string, string?,
    /// string)"/> says one in, its names as a person types them; one a letter or two from a name is answered with it,
    /// whatever its case or dashes, as `BytsSent` is with bytes-sent.
    /// </summary>
    public static string Expects(string option, string? given, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] meant = given is null ? []
            : [.. NameText.Nearest(Compact(given), names.Select(Compact)).Select(near => names.First(name => Compact(name) == near))];
        return meant.Length == 0
            ? Expects(option, given, NameText.Either(names))
            : $"{option} expects {NameText.Either(names)}; '{given}' is not one; did you mean {NameText.Either(meant)}?";
    }

    /// <summary>A name as a person types it: an enumeration's "NothingBefore" as "nothing-before".</summary>
    public static string Kebab(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
    }

    /// <summary>Every name <typeparamref name="T"/> declares, as a person types it, in the order of their values.</summary>
    public static string[] Kebabs<T>()
        where T : struct, Enum => [.. Enum.GetNames<T>().Select(Kebab)];

    public bool TryTakeFlag(string name)
    {
        Ask(name);
        int index = arguments.IndexOf(name);
        if (index < 0)
        {
            return false;
        }

        arguments.RemoveAt(index);
        taken.Add(name);
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
    /// How a refusal names an argument no part of the command took, quoted as given so the sentence's full stop is never
    /// read as part of it: an option it does not know, an operand that starts with a dash and so was read as one, which
    /// follows <c>--</c> instead, or an operand past the last the command takes.
    /// </summary>
    public static string Unknown(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return !argument.StartsWith('-') ? $"Unexpected argument '{argument}': the command takes no more."
            : NamesAnOption(argument) ? $"Unknown option '{argument}'."
            : $"Unknown option '{argument}': an operand that starts with a dash follows --, as in -- \"{argument}\".";
    }

    /// <summary>
    /// How a refusal names what <see cref="TryReportUnknown"/> found in this command line, as the command read it. An
    /// option it took once and that is given again is named as given twice - `--generation 1 --generation 7` - and one it
    /// looked for that ends the line as given without its value, since neither is unknown; one it reads written with its
    /// value after '=', as other tools take it, is said how to give it; an argument with a dash it does not know, a letter
    /// or two from options it reads, is answered with them; anything else is named as <see cref="Unknown"/> names it.
    /// </summary>
    public string Refusal(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (taken.Contains(argument))
        {
            return $"{argument} is given more than once; give it once.";
        }

        if (unvalued.Contains(argument))
        {
            return $"{argument} is given without its value; give one after it.";
        }

        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0 && asked.Contains(argument[..equals]))
        {
            string name = argument[..equals];
            string value = argument[(equals + 1)..];
            string example = value.Length == 0 ? string.Empty
                : $", as in {name} {(value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value)}";
            return valued.Contains(name)
                ? $"{name} takes its value as the argument after it{example}, not after '='."
                : $"{name} takes no value; give it alone.";
        }

        string[] meant = argument.StartsWith('-') ? NameText.Nearest(argument, asked) : [];
        return meant.Length > 0 ? $"Unknown option '{argument}'; did you mean {NameText.Either(meant)}?" : Unknown(argument);
    }

    public bool TryReportUnknown(out string? unknown)
    {
        unknown = arguments.Count > 0 ? arguments[0] : operands.TryPeek(out string? operand) ? operand : null;
        return unknown is not null;
    }

    /// <summary>A name as two spellings of it compare: its letters without dashes.</summary>
    private static string Compact(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>Notes that the command looked for <paramref name="name"/>, once, in the order it looked.</summary>
    private void Ask(string name)
    {
        if (!asked.Contains(name))
        {
            asked.Add(name);
        }
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
