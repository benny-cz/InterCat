namespace InterCat.Ui.Tests;

/// <summary>
/// A test's folder of sessions, removed once the windows that read them have let them go: a window closed with a reading
/// under way still holds that session's evidence lease until the reading ends, and the lease's lock file refuses the
/// folder's removal meanwhile - which clean-worktree runs met as tests ended (revisions 888 and 890).
/// </summary>
internal static class HeldFolders
{
    /// <summary>
    /// Removes <paramref name="path"/> and all it holds, if it is there, waiting up to five seconds for the windows'
    /// readings to let it go, and running the window's dispatcher between tries so a reading that ends on it can; a folder
    /// still held then is left to the temporary folder, as a test session's is.
    /// </summary>
    public static void Remove(string path)
    {
        for (int attempt = 0; Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (attempt < 50)
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
