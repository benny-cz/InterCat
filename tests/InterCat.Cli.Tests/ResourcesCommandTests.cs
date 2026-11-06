using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat resources (ADR-097): a session's named synchronization objects and sections by their handles, each from its first
/// record to its last, by the object directory its name lies in, with whose it was and which of its ends the capture saw.
/// </summary>
public sealed class ResourcesCommandTests
{
    [Fact(DisplayName = "R3: icat resources lists each resource by the namespace its name lies in, says whose it was and which of its ends the capture saw, and keeps an unnamed one unnamed")]
    public async Task ResourcesAreListedByNamespace()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = NamedObjects();
        Publish(session.Store, rows, fields: fields);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, _) = await Run("resources", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        string text = output.ReplaceLineEndings("\n");
        Assert.Contains("\nRESOURCES\n", text, StringComparison.Ordinal);
        Assert.Matches(@"\n  Resources +4 synchronization objects and 1 section, 4 named\n", text);

        // The namespaces, the busiest first, to browse by.
        Assert.Matches(@"\n\s*\\Sessions\\1\\BaseNamedObjects\s+3\s*\n", text);
        Assert.Matches(@"\n\s*\\BaseNamedObjects\\Global\s+1\s*\n", text);

        // Each resource by its number, type, namespace and leaf, whose it was, and which of its ends were seen.
        Assert.Matches(@"Synchronization object 1\s+Event\s+\\Sessions\\1\\BaseNamedObjects\s+Ready\s+PID 100, PID 200\s+\S+ s\s+\S+ s, closed\s+4", text);
        Assert.Matches(@"Synchronization object 2\s+Mutant\s+\\BaseNamedObjects\\Global\s+Lock\s+PID 300\s+\S+ s, made before\s+\S+ s, closed", text);
        Assert.Matches(@"Synchronization object 3\s+Event\s+\\Sessions\\1\\BaseNamedObjects\s+Held\s+PID 300\s+\S+ s\s+\S+ s, held at the stop", text);
        Assert.Matches(@"Synchronization object 4\s+Event\s+-\s+unnamed\s+PID 100 · 1 record bound to none\s+\S+ s\s+\S+ s, its close unseen", text);
        Assert.Matches(@"Section 9\s+Section\s+\\Sessions\\1\\BaseNamedObjects\s+SharedMap\s+PID 100", text);
        Assert.Contains("Its records are handles, not traffic", text, StringComparison.Ordinal);

        // --namespace keeps those named in a directory or beneath it, its case as the object manager ignores it; and
        // --mechanism one mechanism's.
        (code, output, _) = await Run("resources", session.Path, "--namespace", @"\sessions\1\BASENAMEDOBJECTS\");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Equal(["Held", "Ready", "SharedMap"], Leaves(output));
        (code, output, _) = await Run("resources", session.Path, "--namespace", @"\BaseNamedObjects");
        Assert.Equal(["Lock"], Leaves(output));
        (code, output, _) = await Run("resources", session.Path, "--mechanism", "section");
        Assert.Equal(["SharedMap"], Leaves(output));

        // The document keeps each name as recorded, an unnamed one with no name, and each holder's handles.
        (code, output, _) = await Run("resources", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal(("resources-v1", 5, 1), (json.RootElement.GetProperty("contract").GetString(),
            json.RootElement.GetProperty("resources").GetInt32(), json.RootElement.GetProperty("unnamed").GetInt32()));
        JsonElement[] listed = [.. json.RootElement.GetProperty("listed").EnumerateArray()];
        JsonElement cut = listed.Single(item => item.GetProperty("number").GetInt64() == 4);
        Assert.Equal((0, JsonValueKind.Null, false, false), (cut.GetProperty("names").GetArrayLength(),
            cut.GetProperty("namespace").ValueKind, cut.GetProperty("lastHandleClosed").GetBoolean(), cut.GetProperty("heldAtStop").GetBoolean()));
        Assert.Equal(1, cut.GetProperty("unbound").GetProperty("BeforeFirstEvidence").GetInt32());
        JsonElement ready = listed.Single(item => item.GetProperty("number").GetInt64() == 1);
        Assert.Equal([(100, 1, 0, 1), (200, 0, 1, 1)], ready.GetProperty("holders").EnumerateArray()
            .Select(holder => (holder.GetProperty("processId").GetInt32(), holder.GetProperty("handlesMade").GetInt32(),
                holder.GetProperty("handlesDuplicated").GetInt32(), holder.GetProperty("handlesClosed").GetInt32()))
            .OrderBy(holder => holder.Item1));

        // A mechanism it does not list is refused, by name.
        (code, _, string said) = await Run("resources", session.Path, "--mechanism", "pipe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--mechanism expects synchronization or section; 'pipe' is not one.", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: icat resources says a session whose records number no resource lists none, which is not none on the machine")]
    public async Task NoResourceIsNotNoneOnTheMachine()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 100, 1)]);
        session.Store.ReleaseSegmentReaders();
        (InterCatExitCode code, string output, _) = await Run("resources", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("so none listed is not none on the machine", output, StringComparison.Ordinal);
    }

    // The leaves of the resources a listing's table names, in their order of name.
    private static string[] Leaves(string output) =>
        [.. Regex.Matches(output, @"(?m)^\s*(?:Synchronization object|Section) \d+\s+\S+\s+\S+\s+(\S+)")
            .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
