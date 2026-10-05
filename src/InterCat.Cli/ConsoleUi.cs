using System.Globalization;
using System.Text;

namespace InterCat.Cli;

/// <summary>
/// Terminal presentation. Machine-readable data goes to stdout; progress and status go to stderr
/// (section 20.4). Colour is an additional channel only: every state is also carried by its text (R14).
/// </summary>
internal static class ConsoleUi
{
    private static readonly bool UseColor = DetermineColorSupport();

    // Fields written one after another are one group, written out when anything else is written or the command ends: a
    // group's values start in one column, two spaces past its longest label, so a long label never pushes its own value
    // out of line with the rest. A capture's progress can be written from another thread, hence the lock.
    private static readonly Lock FieldGate = new();
    private static readonly List<(string Label, string Value, int Width)> PendingFields = [];
    private static TextWriter? pendingWriter;

    public static void Heading(string text)
    {
        Flush();
        Console.Out.WriteLine();
        Console.Out.WriteLine(Paint(text.ToUpperInvariant(), "1;36"));
        Console.Out.WriteLine(new string('-', Math.Min(text.Length, 78)));
    }

    public static void Line(string text = "")
    {
        Flush();
        Console.Out.WriteLine(text);
    }

    /// <summary>
    /// A labelled value. It is written with the fields written next to it, once something else is written or the command
    /// ends (<see cref="Flush"/>): every value of the group starts in one column, at least <paramref name="width"/> past
    /// the indent and two spaces past the group's longest label.
    /// </summary>
    public static void Field(string label, string value, int width = 22)
    {
        lock (FieldGate)
        {
            // A field written while help is explained on stderr belongs to that writer's group, not stdout's.
            if (pendingWriter is not null && !ReferenceEquals(pendingWriter, Console.Out))
            {
                FlushFields();
            }

            pendingWriter = Console.Out;
            PendingFields.Add((label, value, width));
        }
    }

    /// <summary>Writes the fields not yet written, as one group: every other write, and the end of a command, calls it.</summary>
    public static void Flush()
    {
        lock (FieldGate)
        {
            FlushFields();
        }
    }

    public static void Bullet(string text)
    {
        Flush();
        Console.Out.WriteLine($"  - {text}");
    }

    public static void Note(string text)
    {
        Flush();
        Console.Out.WriteLine(Paint($"  {text}", "2"));
    }

    public static void Progress(string text)
    {
        Flush();
        Console.Error.WriteLine(Paint($"… {text}", "2"));
    }

    public static void Warn(string text)
    {
        Flush();
        Console.Error.WriteLine(Paint($"! {text}", "33"));
    }

    public static void Failure(string text)
    {
        Flush();
        Console.Error.WriteLine(Paint($"x {text}", "31"));
    }

    /// <summary>A reader's refusal of a value, in its own words (<see cref="Reason"/>).</summary>
    public static void Failure(ArgumentException exception) => Failure(Reason(exception));

    /// <summary>
    /// What an exception says, as a person reads it: .NET names an argument's parameter after its words,
    /// " (Parameter 'cursor')", which is the code's name for the value and not one a person typed.
    /// </summary>
    public static string Reason(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string suffix = exception is ArgumentException { ParamName: { Length: > 0 } name } ? $" (Parameter '{name}')" : string.Empty;
        return suffix.Length > 0 && exception.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? exception.Message[..^suffix.Length]
            : exception.Message;
    }

    /// <summary>
    /// Writes what <paramref name="write"/> prints to stderr. What explains a refusal or a failure - the help an invocation
    /// was refused against, what would complete it, the sources that kept a capture from starting - goes where the failure
    /// does, so stdout carries only what a command answers (§20.4) and a reader of `--json` never parses prose.
    /// </summary>
    public static void Explain(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        TextWriter output = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            write();
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    public static void Success(string text)
    {
        Flush();
        Console.Error.WriteLine(Paint($"+ {text}", "32"));
    }

    /// <summary>Renders a fixed-width table. Columns are padded from the widest cell, never truncated.</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        Flush();

        var widths = new int[headers.Count];
        for (int column = 0; column < headers.Count; column++)
        {
            widths[column] = headers[column].Length;
        }

        foreach (IReadOnlyList<string> row in rows)
        {
            for (int column = 0; column < headers.Count && column < row.Count; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        var builder = new StringBuilder();
        builder.Append("  ");
        for (int column = 0; column < headers.Count; column++)
        {
            builder.Append(headers[column].PadRight(widths[column] + 2));
        }

        Console.Out.WriteLine(Paint(builder.ToString().TrimEnd(), "1"));

        foreach (IReadOnlyList<string> row in rows)
        {
            builder.Clear();
            builder.Append("  ");
            for (int column = 0; column < headers.Count; column++)
            {
                string cell = column < row.Count ? row[column] : string.Empty;
                builder.Append(cell.PadRight(widths[column] + 2));
            }

            Console.Out.WriteLine(builder.ToString().TrimEnd());
        }
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Bytes(long? value) =>
        value is null ? "unknown" : $"{value.Value.ToString("N0", CultureInfo.CurrentCulture)} B";

    /// <summary>A size for a person to read (1 GiB, 512 MiB), where the exact byte count adds nothing.</summary>
    public static string Size(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double scaled = value;
        int unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} B"
            : $"{scaled.ToString(scaled % 1 == 0 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    public static string Ratio(decimal? value) =>
        value is null ? "not measured" : value.Value.ToString("P1", CultureInfo.CurrentCulture);

    private static void FlushFields()
    {
        if (PendingFields.Count == 0)
        {
            return;
        }

        int column = PendingFields.Max(field => Math.Max(field.Width, field.Label.Length + 2));
        foreach ((string label, string value, _) in PendingFields)
        {
            pendingWriter!.WriteLine($"  {label.PadRight(column)}{value}");
        }

        PendingFields.Clear();
        pendingWriter = null;
    }

    private static string Paint(string text, string code) => UseColor ? $"\u001b[{code}m{text}\u001b[0m" : text;

    private static bool DetermineColorSupport()
    {
        if (System.Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        return !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    }
}
