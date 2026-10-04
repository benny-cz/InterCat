using System.Text.RegularExpressions;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.4: a read the view starts in the background is answered on a later turn of the thread that asked, never inside the
/// step that asked for it. A read that had finished by the time it was awaited continued inline, so a ranking's bytes
/// were applied within the setter that asked for them, and the "updating" state it had just published was skipped; tests
/// of that state failed whenever the read beat the step, eight runs in eight under load.
/// </summary>
public sealed partial class ReadAnswerTests
{
    [Fact(DisplayName = "§6.4: a read that has already finished is answered on a later turn of the asking thread, never inside the step that asked")]
    public void AFinishedReadIsAnsweredLater() => SingleThreadedContext.Run(async () =>
    {
        int asking = Environment.CurrentManagedThreadId;
        var steps = new List<string>();
        async Task Ask()
        {
            steps.Add("asked");
            int answer = await Task.FromResult(42).AnsweredLater();
            steps.Add(Environment.CurrentManagedThreadId == asking ? $"answered {answer} on the asking thread" : "answered elsewhere");
        }

        Task asked = Ask();
        steps.Add("the step ended");
        await asked;
        Assert.Equal(["asked", "the step ended", "answered 42 on the asking thread"], steps);
    });

    [Fact(DisplayName = "§6.4: every read the workspace awaits from its evidence source is answered on a later turn")]
    public void EveryEvidenceReadIsAnsweredLater()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "src", "InterCat.Desktop");
        var reads = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "WorkspaceViewModel*.cs"))
        {
            string source = File.ReadAllText(path);
            foreach (Match awaited in AwaitPattern().Matches(source))
            {
                string target = awaited.Groups["target"].Value;
                if (!IsEvidenceRead(target))
                {
                    continue;
                }

                // What follows the awaited expression: past its call's arguments, if it is a call.
                int end = awaited.Index + awaited.Length;
                if (end < source.Length && source[end] == '(')
                {
                    end = PastArguments(source, end);
                }

                string read = $"{Path.GetFileName(path)}: await {target}";
                reads.Add(read);
                Assert.True(
                    target.EndsWith(".AnsweredLater", StringComparison.Ordinal)
                        || source[end..].TrimStart().StartsWith(".AnsweredLater()", StringComparison.Ordinal),
                    $"{read} is answered inside the step that asked for it.");
            }
        }

        // The scan finds what it is for: every evidence read the view model awaits today, and none fewer.
        Assert.True(reads.Count >= 18, $"Only {reads.Count} evidence reads were found: {string.Join("; ", reads)}");
    }

    /// <summary>A read from the session's evidence source, or the task of one held in a local.</summary>
    private static bool IsEvidenceRead(string target) =>
        target.StartsWith("source.", StringComparison.Ordinal)
        || target.StartsWith("evidenceSource!.", StringComparison.Ordinal)
        || target is "read" or "measures"
        || target.StartsWith("read.", StringComparison.Ordinal)
        || target.StartsWith("measures.", StringComparison.Ordinal);

    private static int PastArguments(string source, int open)
    {
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return index + 1;
            }
        }

        throw new InvalidOperationException("An awaited call's arguments never close.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    [GeneratedRegex(@"\bawait\s+(?<target>[A-Za-z_][\w!]*(?:\.[A-Za-z_]\w*)*)")]
    private static partial Regex AwaitPattern();
}
