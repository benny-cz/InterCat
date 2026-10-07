using System.Globalization;
using InterCat.Application;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// Where the window keeps the demo it opens (§14 M5): in a folder of InterCat's own beside the user's sessions, never among
/// them, so the saved sessions list holds only what a person saved. A folder that holds the demo is opened as it is; one
/// that holds anything else is left as it is, and the next free folder takes a new demo.
/// </summary>
internal static class DemoPlace
{
    /// <summary>How many folders are tried before the window says why it cannot make the demo.</summary>
    private const int MaximumFolders = 100;

    /// <summary>The folder the window keeps demos in: InterCat's own, beside its sessions.</summary>
    public static string Root()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(userData)
            ? throw new InvalidOperationException("The system did not provide a local user-data directory to keep the demo in.")
            : Path.Combine(userData, "InterCat", "Demo");
    }

    /// <summary>
    /// The demo investigation to open under <paramref name="root"/>: the first of its folders that holds one, or one made in
    /// the first that is free.
    /// </summary>
    public static string Ensure(string root, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        for (int index = 1; index <= MaximumFolders; index++)
        {
            string folder = Path.Combine(root, index == 1 ? "demo" : string.Create(CultureInfo.InvariantCulture, $"demo-{index}"));
            string workspace = Path.Combine(folder, DemoInvestigation.WorkspaceFileName);
            if (File.Exists(workspace))
            {
                return workspace;
            }

            if (!File.Exists(folder) && (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                return DemoInvestigation.Create(folder, cancellationToken).WorkspacePath;
            }
        }

        throw new IOException($"Every demo folder in {root} holds something else; remove one to make the demo again.");
    }
}
