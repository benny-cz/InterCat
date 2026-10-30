namespace InterCat.Application;

/// <summary>
/// The names a person most likely meant by one that names nothing (R18): those a letter or two from it. The command line
/// answers a mistyped command, option, value or program with them, and the window a search that finds nothing.
/// </summary>
public static class NameText
{
    /// <summary>
    /// The names among <paramref name="names"/> nearest <paramref name="given"/> that are near enough to be what it was
    /// meant as: a letter or two left out, added, changed or swapped with the next, whatever their case, and no more than
    /// one for every three letters given, so a short name is answered only with one a letter from it. None when no name
    /// is that near.
    /// </summary>
    public static string[] Nearest(string given, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(names);
        int most = Math.Min(2, given.Length / 3);

        // A name longer or shorter than what was given by more than that is farther from it, so it is never measured: a
        // long argument costs no more than a short one.
        (string Name, int Edits)[] near =
        [
            .. names.Distinct(StringComparer.Ordinal)
                .Where(name => Math.Abs(name.Length - given.Length) <= most)
                .Select(name => (name, Edits(given, name)))
                .Where(name => name.Item2 <= most),
        ];
        return near.Length == 0 ? [] : [.. near.Where(name => name.Edits == near.Min(other => other.Edits)).Select(name => name.Name)];
    }

    /// <summary>
    /// The programs among <paramref name="programs"/> - each named as InterCat lists it, by its executable's file name, with
    /// its folders after it in parentheses where two share one - that <paramref name="given"/> is a letter or two from, as
    /// <see cref="Nearest"/> measures it, by the file name with or without its extension, as a search or --program names a
    /// program: "chrme" is a letter from chrome.exe. Each once, in the order given.
    /// </summary>
    public static string[] NearestPrograms(string given, IEnumerable<string> programs)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(programs);
        (string Program, string[] Spellings)[] spelled =
            [.. programs.Distinct(StringComparer.Ordinal).Select(program => (program, Spellings(program)))];
        HashSet<string> near = [.. Nearest(given.Trim(), spelled.SelectMany(program => program.Spellings))];
        return [.. spelled.Where(program => program.Spellings.Any(near.Contains)).Select(program => program.Program)];
    }

    /// <summary>Choices as a sentence names them: "A", "A or B", "A, B or C".</summary>
    public static string Either(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return choices.Count == 1 ? choices[0] : $"{string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}";
    }

    /// <summary>A program's name as it is compared: whole, its file name alone, and that but for its extension.</summary>
    private static string[] Spellings(string program)
    {
        int folders = program.IndexOf(" (", StringComparison.Ordinal);
        string file = folders < 0 ? program : program[..folders];
        return [.. new[] { program, file, file.LastIndexOf('.') is > 0 and int dot ? file[..dot] : file }.Distinct(StringComparer.Ordinal)];
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
                int changed = Same(from[i - 1], to[j - 1]) ? 0 : 1;
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + changed);
                if (i > 1 && j > 1 && Same(from[i - 1], to[j - 2]) && Same(from[i - 2], to[j - 1]))
                {
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
                }
            }
        }

        return distance[from.Length, to.Length];
    }

    /// <summary>Whether two letters are one, whatever their case: a capital is no letter of its own.</summary>
    private static bool Same(char one, char other) => char.ToLowerInvariant(one) == char.ToLowerInvariant(other);
}
