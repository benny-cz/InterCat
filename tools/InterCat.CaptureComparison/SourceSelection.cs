using InterCat.Capture.Windows;

namespace InterCat.CaptureComparison;

/// <summary>
/// The sources a run captures, by the short names <c>--sources</c> takes. The default is the two sources the Explore
/// profile enables, which every earlier result measured; a source the profile leaves off, such as RPC, is measured by
/// naming it, which is what its impact evidence must exist before the profile may turn it on.
/// </summary>
internal static class SourceSelection
{
    public static IReadOnlyList<string> Default { get; } =
    [
        WindowsSourceCatalog.KernelProcessSourceId,
        WindowsSourceCatalog.KernelNetworkSourceId,
    ];

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["process"] = WindowsSourceCatalog.KernelProcessSourceId,
        ["network"] = WindowsSourceCatalog.KernelNetworkSourceId,
        ["rpc"] = WindowsSourceCatalog.RpcSourceId,
        ["file"] = WindowsSourceCatalog.KernelFileSourceId,
    };

    /// <summary>The names <c>--sources</c> accepts, in the order they are listed.</summary>
    public static IReadOnlyList<string> Accepted { get; } = ["process", "network", "rpc", "file"];

    /// <summary>The source ids a comma-separated list names, in its order; null when a name is unknown or repeated.</summary>
    public static IReadOnlyList<string>? Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] names = value.Split(',', StringSplitOptions.TrimEntries);
        var ids = new List<string>(names.Length);
        foreach (string name in names)
        {
            if (!Names.TryGetValue(name, out string? id) || ids.Contains(id, StringComparer.Ordinal))
            {
                return null;
            }

            ids.Add(id);
        }

        return ids.Count == 0 ? null : ids;
    }
}
