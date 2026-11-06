using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat resources (ADR-097): a session's named synchronization objects, sections by their handles and by their views, and
/// AF_UNIX sockets, each from its first record to its last, by the object directory its name lies in, with whose it was and
/// which of its ends the capture saw.
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
        Assert.Matches(@"\n  Resources +4 synchronization objects, 1 section; 4 named\n", text);
        Assert.Matches(@"\n  Traffic +5 of mechanisms recording none\n", text);

        // The namespaces, the busiest first, to browse by.
        Assert.Matches(@"\n\s*\\Sessions\\1\\BaseNamedObjects\s+3\s*\n", text);
        Assert.Matches(@"\n\s*\\BaseNamedObjects\\Global\s+1\s*\n", text);

        // Each resource by its number, type, namespace and leaf, whose it was, and which of its ends were seen.
        Assert.Matches(@"Synchronization object 1\s+Event\s+\\Sessions\\1\\BaseNamedObjects\s+Ready\s+PID 100, PID 200\s+\S+ s\s+\S+ s, closed\s+none recorded\s+4", text);
        Assert.Matches(@"Synchronization object 2\s+Mutant\s+\\BaseNamedObjects\\Global\s+Lock\s+PID 300\s+\S+ s, made before\s+\S+ s, closed", text);
        Assert.Matches(@"Synchronization object 3\s+Event\s+\\Sessions\\1\\BaseNamedObjects\s+Held\s+PID 300\s+\S+ s\s+\S+ s, held at the stop", text);
        Assert.Matches(@"Synchronization object 4\s+Event\s+-\s+unnamed\s+PID 100 · 1 record bound to none\s+\S+ s\s+\S+ s, its close unseen", text);
        Assert.Matches(@"Section 9\s+Section\s+\\Sessions\\1\\BaseNamedObjects\s+SharedMap\s+PID 100", text);
        Assert.Contains("A synchronization object's and a section's records are handles, not traffic", text, StringComparison.Ordinal);

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
        Assert.Contains("--mechanism expects synchronization, section or socket; 'pipe' is not one.", said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: icat resources says the traffic each AF_UNIX socket carried, lists the idle ones apart with --idle, and says a synchronization object's traffic is none recorded")]
    public async Task IdleSocketsAreListedApart()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(session.Store, rows, fields: fields);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, _) = await Run("resources", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        string text = output.ReplaceLineEndings("\n");
        Assert.Matches(@"\n  Resources +1 synchronization object, 4 Unix sockets; 1 named\n", text);
        Assert.Matches(@"\n  Traffic +3 carried traffic, 1 idle, 1 of a mechanism recording none\n", text);
        Assert.Matches(@"Unix socket 5\s+-\s+-\s+unnamed\s+server\.exe · PID 100\s+\S+ s\s+\S+ s, closed\s+idle\s+3", text);
        Assert.Matches(@"Unix socket 6\s+-\s+-\s+unnamed\s+server\.exe · PID 100\s+.+\s+96 B sent, 120 B received\s+5", text);
        Assert.Matches(@"Unix socket 7\s+.+\s+120 B sent, 0 B received · 1 transfer unmeasured\s+5", text);
        Assert.Matches(@"Unix socket 8\s+-\s+-\s+unnamed\s+client\.exe · PID 200\s+\S+ s, made before\s+\S+ s, its close unseen\s+64 B sent, 0 B received\s+1", text);
        Assert.Matches(@"Synchronization object 1\s+.+\s+none recorded\s+2", text);

        // --idle keeps the resources that could carry traffic and carried none: the bound socket alone.
        (code, output, _) = await Run("resources", session.Path, "--idle");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Equal(["Unix socket 5"], Regex.Matches(output, @"(?m)^\s*((?:Unix socket|Section|Synchronization object) \d+)")
            .Select(match => match.Groups[1].Value));
        (code, output, _) = await Run("resources", session.Path, "--mechanism", "socket", "--json");
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement unmeasured = json.RootElement.GetProperty("listed").EnumerateArray().Single(item => item.GetProperty("number").GetInt64() == 7)
            .GetProperty("traffic");
        Assert.Equal((1L, 1L, 120L, 0L, 1L, false), (unmeasured.GetProperty("sends").GetInt64(), unmeasured.GetProperty("receives").GetInt64(),
            unmeasured.GetProperty("bytesSent").GetInt64(), unmeasured.GetProperty("bytesReceived").GetInt64(),
            unmeasured.GetProperty("unmeasuredTransfers").GetInt64(), unmeasured.GetProperty("idle").GetBoolean()));
    }

    [Fact(DisplayName = "R3: icat resources lists a section by its views apart from its handles, with its views and their largest extent, and an image's sections only when --images asks")]
    public async Task MappedSectionsAreListedByTheirViews()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        Publish(session.Store, rows, fields: fields);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, _) = await Run("resources", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        string text = output.ReplaceLineEndings("\n");
        Assert.Matches(@"\n  Resources +1 section, 3 mapped sections; 1 named\n", text);
        Assert.Matches(@"\n  Images +1 image section not listed: a module's views, not memory a process shared; --images lists them\n", text);
        Assert.Matches(@"Section 4\s+not named\s+\\BaseNamedObjects\s+Shared4\s+writer\.exe · PID 100", text);
        Assert.Matches(@"Mapped section 4\s+-\s+-\s+unnamed · 2 views up to 64 KiB\s+writer\.exe · PID 100, reader\.exe · PID 200\s+\S+ s\s+\S+ s, unmapped\s+none recorded\s+4", text);
        Assert.Matches(@"Mapped section 6\s+-\s+-\s+unnamed · 1 view up to 8 KiB\s+reader\.exe · PID 200\s+\S+ s, made before\s+\S+ s, unmapped", text);
        Assert.Matches(@"Mapped section 7\s+-\s+-\s+unnamed · 1 view up to 16 KiB\s+reader\.exe · PID 200\s+\S+ s, made before\s+\S+ s, its unmapping unseen", text);
        Assert.DoesNotContain("Image section 5", text, StringComparison.Ordinal);
        Assert.Contains("never joined to the number its handles carry", text, StringComparison.Ordinal);

        // No view names its section, so a namespace holds no image's section, and none is said to be left out of it.
        (code, output, _) = await Run("resources", session.Path, "--namespace", @"\BaseNamedObjects");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"Section 4\s+not named\s+\\BaseNamedObjects\s+Shared4", output);
        Assert.DoesNotMatch(@"\n  Images ", output.ReplaceLineEndings("\n"));

        // --images lists the image's section too, held at the stop.
        (code, output, _) = await Run("resources", session.Path, "--images");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"Image section 5\s+-\s+-\s+unnamed · 1 view up to 4 KiB\s+writer\.exe · PID 100\s+\S+ s\s+\S+ s, held at the stop", output);

        // The document says which instances are by their views and which an image's, with each holder's views.
        (code, output, _) = await Run("resources", session.Path, "--json");
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("imagesNotListed").GetInt32());
        JsonElement mapped = json.RootElement.GetProperty("listed").EnumerateArray()
            .Single(item => item.GetProperty("byViews").GetBoolean() && item.GetProperty("number").GetInt64() == 6);
        Assert.Equal((1, 8192L, false), (mapped.GetProperty("views").GetInt32(), mapped.GetProperty("largestView").GetInt64(),
            mapped.GetProperty("image").GetBoolean()));
        Assert.Equal((0, 1, 1), mapped.GetProperty("holders").EnumerateArray().Select(holder => (holder.GetProperty("viewsMapped").GetInt32(),
            holder.GetProperty("viewsUnmapped").GetInt32(), holder.GetProperty("viewsListed").GetInt32())).Single());
    }

    [Fact(DisplayName = "R21: icat resources says a session whose only resources are an image's sections lists them only with --images, never that its records number none")]
    public async Task ImageSectionsAloneAreNotNone()
    {
        // Section 5's records alone: an image's view, held at the stop.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = MappedSections();
        HashSet<ulong> image = [.. fields.Where(field => field is { Field: SourceField.SectionNumber, Value: 5 }).Select(field => field.RawRecordOrdinal)];
        using var session = new TemporarySession();
        Publish(session.Store, [.. rows.Where(row => row.Mechanism != Mechanism.SharedSection || image.Contains(row.RawRecordOrdinal))],
            fields: [.. fields.Where(field => image.Contains(field.RawRecordOrdinal))]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, _) = await Run("resources", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("Every resource of this session is an image's section, a module's views rather than memory a process "
            + "shared, and is listed only with --images: 1 image section.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("none listed is not none on the machine", output, StringComparison.Ordinal);

        (code, output, _) = await Run("resources", session.Path, "--images");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"Image section 5\s+-\s+-\s+unnamed · 1 view up to 4 KiB", output);
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
