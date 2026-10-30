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

        string[] meant = argument.StartsWith('-') ? Nearest(argument, asked) : [];
        return meant.Length > 0 ? $"Unknown option '{argument}'; did you mean {Either(meant)}?" : Unknown(argument);
    }

    /// <summary>
    /// The names among <paramref name="names"/> nearest <paramref name="given"/> that are near enough to be what it was
    /// meant as: a letter or two left out, added, changed or swapped with the next, and no more than one for every three
    /// letters given, so a short name is answered only with one a letter from it. None when no name is that near.
    /// </summary>
    public static string[] Nearest(string given, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(names);
        int most = Math.Min(2, given.Length / 3);
        (string Name, int Edits)[] near =
            [.. names.Distinct(StringComparer.Ordinal).Select(name => (name, Edits(given, name))).Where(name => name.Item2 <= most)];
        return near.Length == 0 ? [] : [.. near.Where(name => name.Edits == near.Min(other => other.Edits)).Select(name => name.Name)];
    }

    /// <summary>Choices as a sentence names them: "A", "A or B", "A, B or C".</summary>
    public static string Either(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return choices.Count == 1 ? choices[0] : $"{string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}";
    }

    public bool TryReportUnknown(out string? unknown)
    {
        unknown = arguments.Count > 0 ? arguments[0] : operands.TryPeek(out string? operand) ? operand : null;
        return unknown is not null;
    }

    /// <summary>
    /// How many letters must be left out, added, changed, or swapped with the next to make <paramref name="from"/> into
    /// <paramref name="to"/>: the optimal string alignment distance.
    /// </summary>
    private static int Edits(string from, string to)
    {
        int[,] distance = new int[from.Length + 1, to.Length + 1];
        for (int i = 0; i <= from.Length; i++)
        {
            distance[i, 0] = i;
        }

        for (int j = 0; j <= to.Length; j++)
        {
            distance[0, j] = j;
        }

        for (int i = 1; i <= from.Length; i++)
        {
            for (int j = 1; j <= to.Length; j++)
            {
                int changed = from[i - 1] == to[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + changed);
                if (i > 1 && j > 1 && from[i - 1] == to[j - 2] && from[i - 2] == to[j - 1])
                {
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
                }
            }
        }

        return distance[from.Length, to.Length];
    }

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
