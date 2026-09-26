using System.Globalization;
using InterCat.Capture.Journal;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The sessions the user saved, listed while none is open (§3.1): newest first, described from their manifests and
/// segment headers alone, and nothing that is not a readable session.
/// </summary>
public sealed class RecentSessionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§3.1: the saved sessions are listed newest first from their manifests, and nothing else is")]
    public void TheSavedSessionsAreListedNewestFirst()
    {
        using var root = new TemporaryDirectory();
        string older = Session(root.Path, "explore-20260925T090000Z-a", records: 2, Now.AddDays(-1));
        string newest = Session(root.Path, "explore-20260926T170000Z-b", records: 3, Now.AddMinutes(-20));
        string imported = Session(root.Path, "import-of-a-trace", records: 1, Now.AddHours(-2));

        // A directory that is not a session, one whose pointer is torn, and an empty one are left out, not guessed at.
        _ = Directory.CreateDirectory(Path.Combine(root.Path, "notes"));
        string torn = Directory.CreateDirectory(Path.Combine(root.Path, "explore-torn")).FullName;
        File.WriteAllText(Path.Combine(torn, SessionPointerV1.FileName), "{");
        File.WriteAllText(Path.Combine(root.Path, "notes", "readme.txt"), "not a session");

        // The newest one's follow ended early and has not been finished yet.
        LiveFollowTicket.For(Guid.NewGuid(), Path.Combine(root.Path, "evidence"), newest, Now, Now.AddSeconds(30)).Hold().Dispose();

        IReadOnlyList<RecentSessionRow> rows = RecentSessions.Find(root.Path, Now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.Equal([newest, imported, older], rows.Select(row => row.Path));
        Assert.Equal(["Explore capture", "import-of-a-trace", "Explore capture"], rows.Select(row => row.Title));
        Assert.StartsWith("saved today at 17:40 · 3 records · ", rows[0].Detail, StringComparison.Ordinal);
        Assert.EndsWith(" · not finished saving", rows[0].Detail, StringComparison.Ordinal);
        Assert.StartsWith("saved today at 16:00 · 1 record · ", rows[1].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("not finished", rows[1].Detail, StringComparison.Ordinal);
        Assert.StartsWith("saved yesterday at 18:00 · 2 records · ", rows[2].Detail, StringComparison.Ordinal);
        Assert.Matches(@" · \d+ KB$", rows[2].Detail);
        Assert.Equal($"Explore capture, {rows[0].Detail}. Press Enter to open it.", rows[0].AccessibleName);

        Assert.Equal([newest], RecentSessions.Find(root.Path, Now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, maximum: 1)
            .Select(row => row.Path));
        Assert.Empty(RecentSessions.Find(Path.Combine(root.Path, "missing"), Now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
    }

    private static string Session(string root, string name, int records, DateTimeOffset savedUtc)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "recent-tests");
        _ = Publish(
            store,
            [.. Enumerable.Range(0, records).Select(index =>
                Lifecycle(100 + index, ObservationKind.Create, 400 + index, (ulong)(index + 1)) with { SessionRelativeTicks = 100 + index })],
            committedUtc: savedUtc);
        return directory;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InterCat.Desktop.Tests.Recent", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
