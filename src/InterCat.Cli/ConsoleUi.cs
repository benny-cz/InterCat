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

    public static void Heading(string text)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine(Paint(text.ToUpperInvariant(), "1;36"));
        Console.Out.WriteLine(new string('-', Math.Min(text.Length, 78)));
    }

    public static void Line(string text = "") => Console.Out.WriteLine(text);

    // A label as long as the column still keeps two spaces before its value, rather than running into it.
    public static void Field(string label, string value, int width = 22) =>
        Console.Out.WriteLine($"  {label.PadRight(Math.Max(width, label.Length + 2))}{value}");

    public static void Bullet(string text) => Console.Out.WriteLine($"  - {text}");

    public static void Note(string text) => Console.Out.WriteLine(Paint($"  {text}", "2"));

    public static void Progress(string text) => Console.Error.WriteLine(Paint($"… {text}", "2"));

    public static void Warn(string text) => Console.Error.WriteLine(Paint($"! {text}", "33"));

    public static void Failure(string text) => Console.Error.WriteLine(Paint($"x {text}", "31"));

    public static void Success(string text) => Console.Error.WriteLine(Paint($"+ {text}", "32"));

    /// <summary>Renders a fixed-width table. Columns are padded from the widest cell, never truncated.</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

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

    public static string Ratio(decimal? value) =>
        value is null ? "not measured" : value.Value.ToString("P1", CultureInfo.CurrentCulture);

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
