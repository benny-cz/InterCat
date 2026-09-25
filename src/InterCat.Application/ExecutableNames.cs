namespace InterCat.Application;

/// <summary>
/// Short, unambiguous names for executable groups. A witnessed image path such as
/// <c>\Device\HarddiskVolume8\Windows\System32\svchost.exe</c> identifies a group but does not read as a label: a ranked
/// row or a graph node shows the file name, extended by just enough parent folders to tell apart executables that share
/// one, such as <c>git.exe (cmd)</c> and <c>git.exe (bin)</c>. The path itself stays the group's detail.
/// </summary>
internal static class ExecutableNames
{
    public const string Unwitnessed = "Executable not witnessed";

    /// <summary>A label for every key. Paths that differ only in letter case name one executable and one group.</summary>
    public static Dictionary<string, string> Label(IReadOnlyCollection<(string Key, string? Path)> executables)
    {
        ArgumentNullException.ThrowIfNull(executables);
        var labels = new Dictionary<string, string>(executables.Count, StringComparer.Ordinal);
        var parts = new Dictionary<string, string[]>(executables.Count, StringComparer.Ordinal);
        foreach ((string key, string? path) in executables)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            string[] components = string.IsNullOrWhiteSpace(path)
                ? []
                : path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!parts.TryAdd(key, components))
            {
                throw new ArgumentException($"Executable group {key} occurs twice.", nameof(executables));
            }

            if (components.Length == 0)
            {
                labels[key] = Unwitnessed;
            }
        }

        foreach (IGrouping<string, string> sameName in parts
            .Where(entry => entry.Value.Length > 0)
            .GroupBy(entry => entry.Value[^1], entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            string[] keys = [.. sameName.Order(StringComparer.Ordinal)];
            if (keys.Length == 1)
            {
                labels[keys[0]] = parts[keys[0]][^1];
                continue;
            }

            // The fewest folders, nearest the file first, that make every label in this set different. Distinct paths
            // always differ once the whole folder is shown; identical ones fall back to the path so none is merged.
            int deepest = keys.Max(key => parts[key].Length - 1);
            int depth = 1;
            while (depth < deepest
                && keys.Select(key => Folders(parts[key], depth)).Distinct(StringComparer.OrdinalIgnoreCase).Count() < keys.Length)
            {
                depth++;
            }

            bool distinct = keys.Select(key => Folders(parts[key], depth))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys.Length;
            foreach (string key in keys)
            {
                string folders = Folders(parts[key], depth);
                labels[key] = !distinct
                    ? string.Join('\\', parts[key])
                    : folders.Length == 0 ? parts[key][^1] : $"{parts[key][^1]} ({folders})";
            }
        }

        return labels;
    }

    /// <summary>The <paramref name="depth"/> folders nearest the file, outermost first; empty for a bare file name.</summary>
    private static string Folders(string[] components, int depth) =>
        string.Join('\\', components[Math.Max(0, components.Length - 1 - depth)..^1]);
}
