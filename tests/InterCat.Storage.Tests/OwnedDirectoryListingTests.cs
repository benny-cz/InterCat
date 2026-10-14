using System.Diagnostics;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// A reader lists a session's files to confirm what it leases (store-v1 §3) while its writer stages and publishes others.
/// A file the writer removes between the listing and the facts read of it is not a dependency of anything a lease can
/// hold, so the listing leaves it out rather than failing - which ended a live view's follow of its capture.
/// </summary>
public sealed class OwnedDirectoryListingTests
{
    [Fact(DisplayName = "I15: a listing of a session's files never fails on a file its writer removes while it lists")]
    public async Task AListingLeavesOutAFileRemovedWhileItLists()
    {
        string root = Path.Combine(Path.GetTempPath(), "intercat-listing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LocalOwnedDirectory directory = LocalOwnedDirectory.Open(root);
            File.WriteAllText(Path.Combine(root, "manifest-0000000001.json"), "{}");

            // A writer staging files and removing them as fast as it can, as a live capture's does between a reader's
            // listings.
            using var stop = new CancellationTokenSource();
            Task churn = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string staged = Path.Combine(root, SessionStore.StagingPrefix + Guid.NewGuid().ToString("N") + ".lease");
                    File.WriteAllText(staged, "lease");
                    File.Delete(staged);
                }
            });
            try
            {
                var listing = Stopwatch.StartNew();
                while (listing.Elapsed < TimeSpan.FromSeconds(2))
                {
                    IReadOnlyDictionary<string, OwnedFileFacts> listed = directory.DescribeOwnedFiles();
                    Assert.Equal(2, listed["manifest-0000000001.json"].LengthBytes);
                }
            }
            finally
            {
                await stop.CancelAsync();
                await churn;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
