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
    public static string Ensure(string root, CancellationToken cancellationToken = default) => Ensure(
        root, "demo", DemoInvestigation.WorkspaceFileName,
        folder => DemoInvestigation.Create(folder, cancellationToken).WorkspacePath,
        "demo");

    /// <summary>
    /// The discovery demo's investigation to open under <paramref name="root"/> (ADR-101): the first of its own folders -
    /// `discovery`, then `discovery-2` and on - that holds one, or one made in the first that is free.
    /// </summary>
    public static string EnsureDiscovery(string root, CancellationToken cancellationToken = default) => Ensure(
        root, "discovery", DiscoveryDemo.WorkspaceFileName,
        folder => DiscoveryDemo.Create(folder, cancellationToken).WorkspacePath,
        "discovery demo");

    // The investigation a demo keeps in the first of its folders under root that holds one, or makes in the first that holds
    // nothing; a folder holding anything else is left as it is.
    private static string Ensure(string root, string name, string workspaceFile, Func<string, string> create, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        for (int index = 1; index <= MaximumFolders; index++)
        {
            string folder = Path.Combine(root, index == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}-{index}"));
            string workspace = Path.Combine(folder, workspaceFile);
            if (File.Exists(workspace))
            {
                return workspace;
            }

            if (!File.Exists(folder) && (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                return create(folder);
            }
        }

        throw new IOException($"Every {what} folder in {root} holds something else; remove one to make the {what} again.");
    }
}
