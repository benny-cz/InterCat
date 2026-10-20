using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// Lets a Windows path in wrapped text break where a reader parts it, after a backslash, and never after its drive's colon
/// (R15). Unicode's line breaking finds no break in "C:\Windows\System32\svchost.exe" but the one after "C:", so a path too
/// long for its line left "C:" on a line of its own in the inspector at the smallest window. Each backslash a name follows
/// is followed by a zero-width space, and a drive's colon is joined to its backslash; neither is seen, nor said.
/// </summary>
public sealed class PathBreaks : IValueConverter
{
    /// <summary>The converter a binding names.</summary>
    public static PathBreaks Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? Breakable(text) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary><paramref name="text"/> with a break after each backslash a name follows, and none after a drive's colon.</summary>
    internal static string Breakable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var built = new StringBuilder(text.Length + 8);
        for (int index = 0; index < text.Length; index++)
        {
            char letter = text[index];
            if (letter == '\\' && index > 0 && text[index - 1] == ':')
            {
                built.Append('\u2060');
            }

            built.Append(letter);
            if (letter == '\\' && index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]) && text[index + 1] != '\\')
            {
                built.Append('\u200B');
            }
        }

        return built.ToString();
    }
}
