using InterCat.Desktop;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The capture card names a file of the session from the session's folder down (§6.1), since its path line names the
/// folder whole: a reason naming one reads in the detail's few lines. Only a path within the folder is shortened.
/// </summary>
public sealed class SessionFolderTextTests
{
    private const string Folder = @"C:\Users\marketing-analyst\AppData\Local\InterCat\Sessions\explore-2026-10-22T09-14-03Z";

    [Fact(DisplayName = "§6.1: a file of the session is named from the session's folder down, each time it is named")]
    public void AFileOfTheSessionIsNamedFromItsFolderDown()
    {
        Assert.Equal(
            @"The process cannot access the file '…\journal\chunk-000017.icj' because it is being used by another process.",
            SessionFolderText.Shortened(
                $@"The process cannot access the file '{Folder}\journal\chunk-000017.icj' because it is being used by another process.",
                Folder));

        // At the text's start, twice, with the folder given with its separator, in another case, or with the other separator.
        Assert.Equal(@"…\a.ics and …\b.ics", SessionFolderText.Shortened($@"{Folder}\a.ics and {Folder}\b.ics", Folder + @"\"));
        Assert.Equal(@"'…\a.ics'", SessionFolderText.Shortened($@"'{Folder.ToUpperInvariant()}\a.ics'", Folder));
        Assert.Equal("Could not find '…/segments/a.ics'.",
            SessionFolderText.Shortened("Could not find '/home/me/sessions/explore/segments/a.ics'.", "/home/me/sessions/explore/"));
    }

    [Fact(DisplayName = "§6.1: the folder alone, another folder whose name begins with its, and a path ending in its are named as they are")]
    public void OnlyAPathWithinTheFolderIsShortened()
    {
        string[] kept =
        [
            $"The session at '{Folder}' could not be opened.",
            $"Its folder is {Folder}",
            $@"Could not find '{Folder}-2\journal\chunk-000001.icj'.",
            $@"Could not find '\\?\{Folder}\a.ics'.",
        ];
        foreach (string text in kept)
        {
            Assert.Equal(text, SessionFolderText.Shortened(text, Folder));
        }

        string[] elsewhere = ["Could not find '/mnt/home/me/sessions/explore/a.ics'.", "'/disk2/home/me/sessions/explore/a.ics'"];
        foreach (string text in elsewhere)
        {
            Assert.Equal(text, SessionFolderText.Shortened(text, "/home/me/sessions/explore"));
        }

        // A card that names no session's folder, or the root's, shortens nothing.
        Assert.Equal($@"'{Folder}\a.ics'", SessionFolderText.Shortened($@"'{Folder}\a.ics'", null));
        Assert.Equal($@"'{Folder}\a.ics'", SessionFolderText.Shortened($@"'{Folder}\a.ics'", string.Empty));
        Assert.Equal("'/a.ics'", SessionFolderText.Shortened("'/a.ics'", "/"));
    }
}
