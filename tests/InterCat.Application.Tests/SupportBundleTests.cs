using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §20.6's support bundle: what it says of a session is chosen from an allowlist, a session it cannot read is said to be,
/// by its folder, and its listing says what it holds before it is written (P16).
/// </summary>
public sealed class SupportBundleTests
{
    [Fact(DisplayName = "P16: a support bundle says a session it cannot read by its folder, in its reader's words, and a ledger it lacks as unknown")]
    public void ASupportBundleSaysWhatItCannotRead()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10) with { SessionRelativeTicks = 1_000 },
        ]);
        session.Store.ReleaseSegmentReaders();
        string folder = Path.GetFileName(session.Path);

        // Read whole: every mechanism with its rows, and a ledger the generation does not publish leaves each one's coverage
        // unknown, in the reader's words, rather than a mechanism with no rows reading as quiet.
        SupportSession whole = SupportBundle.DescribeSession(session.Path);
        Assert.Null(whole.Problem);
        Assert.Equal((folder, 2L, 1L), (whole.Folder, whole.Rows, whole.Generation));
        Assert.Null(whole.Ledger);
        Assert.Equal(SessionCoverage.ByMechanism(null).Select(entry => (MechanismText.Name(entry.Mechanism), entry.Reason)),
            whole.Mechanisms.Select(mechanism => (mechanism.Mechanism, mechanism.Reason)));
        Assert.All(whole.Mechanisms, mechanism =>
            Assert.Equal(CoverageStateText.Value(CoverageState.UnknownCoverage), mechanism.Coverage));
        Assert.Equal(2L, whole.Mechanisms.Sum(mechanism => mechanism.Rows));
        Assert.Equal(1L, whole.Mechanisms.Single(mechanism => mechanism.Mechanism == MechanismText.Name(Mechanism.Tcp)).Rows);

        // A segment changed after it was published: the session is said not to verify, by its folder, never its path.
        string segment = Directory.GetFiles(session.Path, "seg-*").First();
        byte[] bytes = File.ReadAllBytes(segment);
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(segment, bytes);
        SupportSession damaged = SupportBundle.DescribeSession(session.Path);
        Assert.Equal(folder, damaged.Folder);
        Assert.Contains("no generation of it verifies", damaged.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetDirectoryName(session.Path)!, damaged.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, damaged.Rows);

        // A folder that is gone is said to be, by its name, where its reader named its whole path.
        string gone = Path.Combine(Path.GetDirectoryName(session.Path)!, "gone-" + Guid.NewGuid().ToString("N"));
        SupportSession missing = SupportBundle.DescribeSession(gone);
        Assert.Equal($"The session directory '{Path.GetFileName(gone)}' does not exist.", missing.Problem);
    }

    [Fact(DisplayName = "§20.6: a support bundle's listing says what it holds for none, one or several sessions, and what every bundle leaves out")]
    public void ASupportBundlesListingSaysWhatItHolds()
    {
        Assert.Equal(
            ["InterCat's version, and this machine's operating system, architecture, runtime and processor count"],
            SupportBundle.Holds(0, capabilities: false));
        IReadOnlyList<string> one = SupportBundle.Holds(1, capabilities: true);
        Assert.Equal(3, one.Count);
        Assert.Equal("this machine's capability report: which sources it could capture, and why not", one[1]);
        Assert.StartsWith("1 session: its folder's name, generation and files", one[2], StringComparison.Ordinal);
        Assert.StartsWith("2 sessions: each one's folder name, generation and files", SupportBundle.Holds(2, capabilities: true)[2],
            StringComparison.Ordinal);
        Assert.Contains("endpoint addresses and ports", SupportBundle.LeftOut);
        Assert.Contains("command lines", SupportBundle.LeftOut);
        Assert.False(string.IsNullOrWhiteSpace(SupportBundle.ProductVersion));
    }
}
