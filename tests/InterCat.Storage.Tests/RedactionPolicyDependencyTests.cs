using System.Security.Cryptography;
using System.Text;
using InterCat.Storage;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// A redacted package's policy is its provenance (`contracts/redacted-session-v1.md`): it reads back exactly, retention
/// cannot release it, and a replacement generation carries it, so a package never comes to read as an original capture.
/// </summary>
public sealed class RedactionPolicyDependencyTests
{
    [Fact(DisplayName = "I22: a redaction policy survives reopen, cannot be released, and is carried by a replacement")]
    public void PolicyIsProvenance()
    {
        string root = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.Redaction", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] policyBytes = Encoding.UTF8.GetBytes("{\"contract\":\"test-policy\"}");
            (_, CommittedBoundary boundary) = PublishWith(root, [policyBytes]);
            string name = DerivedGenerationBuilder.RedactionPolicyFileName(1);

            SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(root));
            Assert.Equal(policyBytes, SessionSegments.RedactionPolicy(reopened.Root, reopened.Current!));
            ArgumentException refusal = Assert.Throws<ArgumentException>(() =>
                reopened.ReleaseDependencies([name], "try to release provenance", DateTimeOffset.UtcNow));
            Assert.Contains("provenance", refusal.Message, StringComparison.Ordinal);

            _ = reopened.CommitReplacingDerived([], 1, boundary, DateTimeOffset.UtcNow);
            SessionStore replaced = SessionStore.OpenExisting(LocalOwnedDirectory.Open(root));
            Assert.Equal(2, replaced.Current!.Generation);
            Assert.Contains(replaced.Current.Dependencies, dependency => dependency.Name == name
                && dependency.Kind == StoreDependencyKind.RedactionPolicy);
            Assert.Equal(policyBytes, SessionSegments.RedactionPolicy(replaced.Root, replaced.Current));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "I22: an ordinary session names no policy, and a generation naming two is refused")]
    public void OneOrNone()
    {
        string root = Path.Combine(Path.GetTempPath(), "InterCat.Storage.Tests.Redaction", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (SessionStore none, _) = PublishWith(root, []);
            Assert.Null(SessionSegments.RedactionPolicy(none.Root, none.Current!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        try
        {
            (SessionStore two, _) = PublishWith(root, [[1], [2]]);
            Assert.Throws<InvalidDataException>(() => SessionSegments.RedactionPolicy(two.Root, two.Current!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One generation holding a journal, a plan and the given policies, named as a package names them.</summary>
    private static (SessionStore Store, CommittedBoundary Boundary) PublishWith(string root, IReadOnlyList<byte[]> policies)
    {
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(root), Guid.NewGuid(), "redaction-test");
        var staged = new List<StoreStagingFile>();
        try
        {
            byte[] journalBytes = Encoding.UTF8.GetBytes("journal");
            const string journalName = "journal-0000000001.icatj";
            staged.Add(Stage(store, journalName, StoreDependencyKind.Journal, journalBytes));
            staged.Add(Stage(store, "normalizer-plan-0000000001.json", StoreDependencyKind.DerivationPlan,
                Encoding.UTF8.GetBytes("retained plan")));
            for (int index = 0; index < policies.Count; index++)
            {
                staged.Add(Stage(store, index == 0
                    ? DerivedGenerationBuilder.RedactionPolicyFileName(1)
                    : $"redaction-policy-0000000001-{index}.json", StoreDependencyKind.RedactionPolicy, policies[index]));
            }

            var boundary = new CommittedBoundary(journalName, journalBytes.Length, 1,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(journalBytes)));
            _ = store.Commit(staged, boundary, DateTimeOffset.UtcNow);
            return (store, boundary);
        }
        finally
        {
            foreach (StoreStagingFile file in staged) file.Dispose();
        }
    }

    private static StoreStagingFile Stage(SessionStore store, string name, StoreDependencyKind kind, byte[] content)
    {
        StoreStagingFile file = store.Stage(name, kind);
        file.Content.Write(content);
        _ = file.Complete();
        return file;
    }
}
