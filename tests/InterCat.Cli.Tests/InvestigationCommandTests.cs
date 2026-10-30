using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// An investigation as a person makes it on the command line (§8, M4): two hosts' sessions added, aligned by their wall
/// clocks, correlated, decided, compared and packaged, each step saying what it did and refusing what it cannot do, with
/// the exit code a script reads. These are the steps M4's two-host exit check runs on separately captured traces.
/// </summary>
public sealed class InvestigationCommandTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Investigation", Guid.NewGuid().ToString("N"));

    public InvestigationCommandTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "§8.2: two hosts' sessions are added, aligned by their wall clocks, correlated, decided and compared on the command line")]
    public async Task TwoHostsAreAlignedCorrelatedAndCompared()
    {
        (string workspace, string client, string server) = await TwoHosts();

        // Two boots' counters are not one clock, so they are not aligned as one, and the refusal says what to do instead.
        (InterCatExitCode refused, string unwritten, string why) = await Run(
            "workspace", "align", workspace, server, client, "--same-boot");
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (refused, unwritten));
        Assert.Contains("ran in different boots", why, StringComparison.Ordinal);
        Assert.EndsWith("Align them by their wall clocks." + Environment.NewLine, why, StringComparison.Ordinal);
        Assert.Empty(InvestigationWorkspace.Read(workspace).Alignments);

        // Through the wall clocks each capture read, within what the person says they agreed to, and the drift between.
        (InterCatExitCode aligned, string shown, string said) = await Run(
            "workspace", "align", workspace, server, client, "--wall-clock", "--sync", "5ms", "--drift-ppm", "50");
        Assert.Equal(InterCatExitCode.Success, aligned);
        Assert.Contains($"Session {server} at 0.000 s is session {client} at 0.750 s, within ±5.1 ms: the wall clocks' stated "
            + "agreement ±5.0 ms, the samples' acquisition ±1.0 µs, and the drift over the 750 ms between them (alignment revision 1).",
            said, StringComparison.Ordinal);
        Assert.Contains($"session {client}'s clock; 1 of 1 other session aligned to it", shown, StringComparison.Ordinal);
        WorkspaceAlignment alignment = Assert.Single(InvestigationWorkspace.Read(workspace).Alignments);
        Assert.Equal(1, alignment.Revision);

        // An order is stated only beyond the pair's combined uncertainty; within it, it is said to be ambiguous.
        (InterCatExitCode compared, string order, _) = await Run("workspace", "compare", workspace, $"{server}@1.0", $"{client}@1.5");
        Assert.Equal(InterCatExitCode.Success, compared);
        Assert.Contains($"session {server} at 1.000 s, which is 1.750 s ±5.1 ms of the investigation's time", order, StringComparison.Ordinal);
        Assert.Contains($"session {client} at 1.500 s, the investigation's time reference", order, StringComparison.Ordinal);

        // Shown, the investigation is headed as one, and says what its time is.
        string described = (await Run("workspace", "show", workspace)).Output;
        Assert.StartsWith("INVESTIGATION\n", described.TrimStart().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Showing an investigation opens each session to learn what it is and writes to none.", described,
            StringComparison.Ordinal);
        Assert.Contains("The investigation's time is its reference session's clock.", described, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace's time", described, StringComparison.Ordinal);

        // Its sessions are sessions, as the window lists them, in its fields, its hosts' table and what it says of them.
        Assert.Matches(@"Sessions\s+2 present", described);
        Assert.Matches(@"Host\s+Name\s+Sessions\s+One host with, by a person", described);
        Assert.Contains("Each session is named by its identity and the capture its journal records", described, StringComparison.Ordinal);
        Assert.DoesNotContain("member", described, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The first is after the second by 250 ms, more than their combined uncertainty of ±5.1 ms.", order,
            StringComparison.Ordinal);
        using (JsonDocument close = JsonDocument.Parse((await Run(
            "workspace", "compare", workspace, $"{server}@1.0", $"{client}@1.752", "--json")).Output))
        {
            JsonElement answer = close.RootElement;
            Assert.Equal(("workspace-comparison-v1", "Ambiguous", 2_000_000L, 5_088_500L), (answer.GetProperty("contract").GetString(),
                answer.GetProperty("order").GetString(), answer.GetProperty("differenceNanoseconds").GetInt64(),
                answer.GetProperty("uncertaintyNanoseconds").GetInt64()));
            Assert.Equal("Their order is ambiguous: they are 2.0 ms apart, within their combined uncertainty of ±5.1 ms.",
                answer.GetProperty("statement").GetString());
        }

        // The known exchange is a candidate, its bytes the same on both sides; deciding it is a person's, never evidence.
        (InterCatExitCode correlated, string candidates, _) = await Run("workspace", "correlate", workspace);
        Assert.Equal(InterCatExitCode.Success, correlated);
        Assert.Contains("1. TCP 10.0.0.10:50100 ⇄ 10.0.0.20:443 · lifetimes overlap", candidates, StringComparison.Ordinal);
        Assert.Contains("The first sent 52,095 B in 30 measured transfers, and the second received 52,095 B in 30: the same bytes.",
            candidates, StringComparison.Ordinal);
        (InterCatExitCode accepted, _, string decided) = await Run(
            "workspace", "join", workspace, "1", "--accept", "--note", "the known exchange");
        Assert.Equal(InterCatExitCode.Success, accepted);
        Assert.Contains("Candidate 1 (10.0.0.10:50100 ⇄ 10.0.0.20:443) is accepted as one connection, by you, never as evidence "
            + "(join revision 1).", decided, StringComparison.Ordinal);
        Assert.Equal(("the known exchange", WorkspaceJoinDecision.Accepted),
            (Assert.Single(InvestigationWorkspace.Read(workspace).Joins).Note, InvestigationWorkspace.Read(workspace).Joins[0].Decision));
        (InterCatExitCode none, _, string missing) = await Run("workspace", "join", workspace, "7", "--accept");
        Assert.Equal(InterCatExitCode.InvalidInvocation, none);
        Assert.Contains("There are 2 candidates now, so there is no candidate 7; icat workspace correlate lists them.", missing,
            StringComparison.Ordinal);
        (InterCatExitCode undecided, _, string again) = await Run("workspace", "join", workspace, "1", "--withdraw");
        Assert.Equal(InterCatExitCode.Success, undecided);
        Assert.Contains("is undecided again; its earlier decisions are kept (join revision 2).", again, StringComparison.Ordinal);

        // Withdrawn, the alignment leaves the investigation no time across hosts, and no order is stated.
        (InterCatExitCode withdrawn, _, string unaligned) = await Run("workspace", "align", workspace, server, "--withdraw");
        Assert.Equal(InterCatExitCode.Success, withdrawn);
        Assert.Contains($"Session {server} is not aligned any more (alignment revision 2); its earlier revisions are kept.",
            unaligned, StringComparison.Ordinal);
        string apart = (await Run("workspace", "compare", workspace, $"{server}@1.0", $"{client}@1.5")).Output;
        Assert.Contains("No order is stated: no session is aligned, so the investigation has no time across its sessions.", apart,
            StringComparison.Ordinal);
        Assert.Contains($"session {server} at 1.000 s, which has no place in the investigation's time", apart, StringComparison.Ordinal);
        Assert.Equal(2, InvestigationWorkspace.Read(workspace).Alignments.Count);
    }

    [Fact(DisplayName = "§8.2: a session aligned at two instants says the rate they measure its clock running at against the reference's, with its sign, on the command line")]
    public async Task TwoInstantsSayTheClocksRate()
    {
        (string workspace, string client, string server) = await TwoHosts();

        // A second pair of instants naming the sessions the other way round is refused, and nothing is aligned.
        (InterCatExitCode crossed, _, string why) = await Run("workspace", "align", workspace, $"{server}@1", $"{client}@1.75",
            $"{client}@11", $"{server}@11.7501", "--within", "1ms");
        Assert.Equal(InterCatExitCode.InvalidInvocation, crossed);
        Assert.Contains("A second instant names the same two sessions as the first, in the same order: "
            + $"{server}@<seconds> {client}@<seconds>.", why, StringComparison.Ordinal);
        Assert.Empty(InvestigationWorkspace.Read(workspace).Alignments);

        // The server's 1 s is the client's 1.75 s and its 11 s the client's 11.7499 s: while the server's clock counted 10 s,
        // the client's counted 9.9999 s, so the server's runs 10 ppm fast against the client's, and is said to.
        (InterCatExitCode aligned, string shown, string said) = await Run("workspace", "align", workspace, $"{server}@1",
            $"{client}@1.75", $"{server}@11", $"{client}@11.7499", "--within", "1ms");
        Assert.Equal(InterCatExitCode.Success, aligned);
        Assert.Contains($"Session {server} at 1.000 s is session {client} at 1.750 s, and at 11.000 s its 11.7499 s, within ±",
            said, StringComparison.Ordinal);
        Assert.Contains("The two instants measure its clock running +10 ppm against the reference's.", shown, StringComparison.Ordinal);
        Assert.Contains("No bound on the rate's wander was stated, so away from the two instants this session's uncertainty is "
            + "unknown and no order is stated there.", said, StringComparison.Ordinal);
        Assert.Contains("rate +10 ppm, wander not stated", shown, StringComparison.Ordinal);
        Assert.Equal(10.0001, InvestigationWorkspace.MeasuredPartsPerMillion(Assert.Single(InvestigationWorkspace.Read(workspace).Alignments)), 4);

        // Aligned again, with a bound on the rate's wander: the revision before it is kept, and said to be.
        (InterCatExitCode again, string bounded, _) = await Run("workspace", "align", workspace, $"{server}@1", $"{client}@1.75",
            $"{server}@11", $"{client}@11.7499", "--within", "1ms", "--drift-ppm", "0.5");
        Assert.Equal(InterCatExitCode.Success, again);
        Assert.Contains("rate +10 ppm, wander ≤ 0.5 ppm", bounded, StringComparison.Ordinal);
        Assert.Contains("1 earlier alignment revision is kept in the file; icat workspace show --json lists it.",
            (await Run("workspace", "show", workspace)).Output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§8.4: a note is reworded, removed and pinned at an instant, and a view saved, refused backwards and removed, each a revision on the command line")]
    public async Task NotesAndViewsAreRevisions()
    {
        (string workspace, string client, string server) = await TwoHosts();
        Assert.Equal(InterCatExitCode.Success, (await Run(
            "workspace", "align", workspace, server, client, "--wall-clock", "--sync", "5ms", "--drift-ppm", "50")).Code);

        // A note about the whole investigation, named by its identity's first eight digits, is reworded and then removed; every
        // revision is kept.
        (InterCatExitCode noted, _, string added) = await Run("workspace", "note", workspace, "the first words");
        Assert.Equal(InterCatExitCode.Success, noted);
        string note = System.Text.RegularExpressions.Regex.Match(added,
            @"Note ([0-9a-f]{8}) added, about the whole investigation \(note revision 1\)\.").Groups[1].Value;
        Assert.NotEmpty(note);
        (InterCatExitCode reworded, string rewrittenShown, string rewritten) = await Run(
            "workspace", "note", workspace, note, "--replace", "the second words");
        Assert.Equal(InterCatExitCode.Success, reworded);
        Assert.Contains($"Note {note} reworded (note revision 2); its earlier words are kept.", rewritten, StringComparison.Ordinal);
        Assert.Contains("the second words", rewrittenShown, StringComparison.Ordinal);
        Assert.DoesNotContain("the first words", rewrittenShown, StringComparison.Ordinal);
        (InterCatExitCode removed, string removedShown, string gone) = await Run("workspace", "note", workspace, note, "--remove");
        Assert.Equal(InterCatExitCode.Success, removed);
        Assert.Contains($"Note {note} removed (note revision 3); its earlier words are kept.", gone, StringComparison.Ordinal);
        Assert.DoesNotContain("the second words", removedShown, StringComparison.Ordinal);
        Assert.Equal(3, InvestigationWorkspace.Read(workspace).Notes.Count);

        // A note pinned at an instant of a session says where.
        (InterCatExitCode pinned, _, string where) = await Run("workspace", "note", workspace, "the retry storm", "--at", $"{server}@1.5");
        Assert.Equal(InterCatExitCode.Success, pinned);
        Assert.Matches($@"Note [0-9a-f]{{8}} added, pinned at session {server}'s 1\.500 s \(note revision 4\)\.", where);

        // A view of the investigation's time is saved, one whose end is not after its start refused, and one removed, its
        // revisions kept.
        (InterCatExitCode saved, _, string kept) = await Run("workspace", "view", workspace, "incident", "1", "2.5");
        Assert.Equal(InterCatExitCode.Success, saved);
        Assert.Contains("View 'incident' saved: ", kept, StringComparison.Ordinal);
        Assert.Contains(" of the investigation's time (view revision 1).", kept, StringComparison.Ordinal);
        (InterCatExitCode backwards, _, string refusal) = await Run("workspace", "view", workspace, "backwards", "3", "2");
        Assert.Equal(InterCatExitCode.InvalidInvocation, backwards);
        Assert.Contains("A view shows some of the investigation's time, so its end comes after its start; 2.000 s is not after 3.000 s.",
            refusal, StringComparison.Ordinal);
        (InterCatExitCode unsaved, _, string dropped) = await Run("workspace", "view", workspace, "incident", "--remove");
        Assert.Equal(InterCatExitCode.Success, unsaved);
        Assert.Contains("View 'incident' removed (view revision 2); its revisions are kept.", dropped, StringComparison.Ordinal);
        Assert.Equal(2, InvestigationWorkspace.Read(workspace).Views.Count);
    }

    [Fact(DisplayName = "§8.3: icat workspace says why a folder holding no session is not added, refuses a candidate not named by its number and a grouping it does not compare by, and writes a decision as its file records it")]
    public async Task AddsAndDecisionsSayWhatTheyDid()
    {
        (string workspace, string client, _) = await TwoHosts();

        // A folder holding no session is not added, and says why; the investigation is as it was.
        string empty = Directory.CreateDirectory(Path.Combine(folder, "empty")).FullName;
        (InterCatExitCode unadded, _, string why) = await Run("workspace", "add", workspace, empty);
        Assert.Equal(InterCatExitCode.InvalidInvocation, unadded);
        Assert.Contains($"{empty} was not added: ", why, StringComparison.Ordinal);
        Assert.Equal(2, InvestigationWorkspace.Read(workspace).Members.Count);

        // A candidate is named by its number in correlate's list, and a saved comparison groups by process or executable rows.
        (InterCatExitCode unnumbered, _, string number) = await Run("workspace", "join", workspace, "first", "--accept");
        Assert.Equal(InterCatExitCode.InvalidInvocation, unnumbered);
        Assert.Contains("A candidate is named by its number in icat workspace correlate's list; 'first' is not one.", number,
            StringComparison.Ordinal);
        (InterCatExitCode ungrouped, _, string grouping) = await Run("workspace", "comparison", workspace, "saved", "--session", client,
            "--interval", "0:10000000", "--baseline", "10000000:20000000", "--group-by", "thread");
        Assert.Equal(InterCatExitCode.InvalidInvocation, ungrouped);
        Assert.Contains("--group-by compares process or executable rows; 'thread' is neither.", grouping, StringComparison.Ordinal);
        Assert.Empty(InvestigationWorkspace.Read(workspace).Comparisons);

        // Rejected, the candidate is said not to be one connection; as JSON, a decision is written as the file records it.
        (InterCatExitCode rejected, _, string decided) = await Run("workspace", "join", workspace, "1", "--reject");
        Assert.Equal(InterCatExitCode.Success, rejected);
        Assert.Contains("Candidate 1 (10.0.0.10:50100 ⇄ 10.0.0.20:443) is rejected: its two ends are not one connection "
            + "(join revision 1).", decided, StringComparison.Ordinal);
        (InterCatExitCode withdrawn, string written, _) = await Run("workspace", "join", workspace, "1", "--withdraw", "--json");
        Assert.Equal(InterCatExitCode.Success, withdrawn);
        using JsonDocument join = JsonDocument.Parse(written);
        WorkspaceJoin recorded = InvestigationWorkspace.Read(workspace).Joins[^1];
        Assert.Equal((2, "Withdrawn", recorded.First.Key, recorded.Second.Key), (join.RootElement.GetProperty("revision").GetInt32(),
            join.RootElement.GetProperty("decision").GetString(), join.RootElement.GetProperty("first").GetProperty("key").GetString(),
            join.RootElement.GetProperty("second").GetProperty("key").GetString()));
    }

    [Fact(DisplayName = "§8.3: a host is named, hosts are said to be one and a translation is stated on the command line, each withdrawn as a revision")]
    public async Task HostsAndTranslationsAreRevisions()
    {
        (string workspace, string client, string server) = await TwoHosts();
        InvestigationWorkspaceFile made = InvestigationWorkspace.Read(workspace);
        string laptop = Short(made.Members[0].HostId), office = Short(made.Members[1].HostId);

        (InterCatExitCode named, string shown, string said) = await Run("workspace", "alias", workspace, laptop, "laptop");
        Assert.Equal(InterCatExitCode.Success, named);
        Assert.Contains($"Host {laptop} is called 'laptop'.", said, StringComparison.Ordinal);
        Assert.Matches($@"{client}\s+Present\s+2\s+laptop\s", shown);

        (InterCatExitCode confirmed, _, string one) = await Run("workspace", "same-host", workspace, "laptop", office, "--note", "one box");
        Assert.Equal(InterCatExitCode.Success, confirmed);
        Assert.Contains($"Hosts {laptop} and {office} are one host by your confirmation (host revision 1).", one, StringComparison.Ordinal);
        (InterCatExitCode stated, string translated, string statement) = await Run(
            "workspace", "translate", workspace, "10.0.0.20:443", "192.168.1.20:443", "--note", "the office NAT");
        Assert.Equal(InterCatExitCode.Success, stated);
        Assert.Contains("10.0.0.20:443 is 192.168.1.20:443 by your statement (translation revision 1).", statement,
            StringComparison.Ordinal);
        Assert.Contains("10.0.0.20:443 is 192.168.1.20:443, by a person (revision 1): the office NAT", translated,
            StringComparison.Ordinal);

        // A withdrawal is a revision of its own; what it withdraws stays in the file.
        Assert.Contains($"Hosts {laptop} and {office} are two hosts again (host revision 2)",
            (await Run("workspace", "same-host", workspace, laptop, office, "--withdraw")).Error, StringComparison.Ordinal);
        Assert.Contains("10.0.0.20:443 and 192.168.1.20:443 are two endpoints again (translation revision 2)",
            (await Run("workspace", "translate", workspace, "10.0.0.20:443", "192.168.1.20:443", "--withdraw")).Error,
            StringComparison.Ordinal);
        Assert.Contains($"Host {laptop} has no name now.", (await Run("workspace", "alias", workspace, "laptop", "--remove")).Error,
            StringComparison.Ordinal);
        InvestigationWorkspaceFile revised = InvestigationWorkspace.Read(workspace);
        Assert.Equal([WorkspaceHostDecision.Confirmed, WorkspaceHostDecision.Withdrawn],
            revised.HostEquivalences.Select(revision => revision.Decision));
        Assert.Equal([WorkspaceTranslationDecision.Stated, WorkspaceTranslationDecision.Withdrawn],
            revised.AddressTranslations.Select(revision => revision.Decision));
        Assert.Empty(revised.HostAliases);

        // A session moved is relinked to where it is now, and only to itself.
        string moved = Path.Combine(folder, "moved-server");
        CopyDirectory(Path.Combine(folder, "demo", DemoInvestigation.ServerFolder), moved);
        (InterCatExitCode relinked, _, string where) = await Run("workspace", "relink", workspace, server, moved);
        Assert.Equal(InterCatExitCode.Success, relinked);
        Assert.Contains($"Session {server} relinked to moved-server, at generation 2.", where, StringComparison.Ordinal);
        (InterCatExitCode wrong, _, string itself) = await Run("workspace", "relink", workspace, server,
            Path.Combine(folder, "demo", DemoInvestigation.ClientFolder));
        Assert.Equal(InterCatExitCode.InvalidInvocation, wrong);
        Assert.Contains("A session is relinked only to itself.", itself, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§8.4: icat workspace says once that an investigation is missing or unreadable, and refuses to make one beneath a file")]
    public async Task AnInvestigationItCannotReadIsSaidOnce()
    {
        // Each operand would have said it, and the summary after them once more, ending with the summary's exit code.
        string missing = Path.Combine(folder, "missing.icat-workspace");
        (InterCatExitCode code, string output, string said) = await Run("workspace", "add", missing, folder, folder);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Equal($"x No investigation is at {missing}; icat workspace new makes one.", said.TrimEnd());

        string unreadable = Path.Combine(folder, "notes.icat-workspace");
        File.WriteAllText(unreadable, "not an investigation");
        (code, output, said) = await Run("workspace", "add", unreadable, folder, folder);
        Assert.Equal((InterCatExitCode.CorruptedInput, string.Empty), (code, output));
        Assert.StartsWith($"x {unreadable} is not a readable investigation: ", said, StringComparison.Ordinal);
        Assert.Single(said.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        // One cannot be made where a file stands in place of its folder, and the file is named.
        string file = Path.Combine(folder, "a-file");
        File.WriteAllText(file, "not a folder");
        string beneath = Path.Combine(file, "case.icat-workspace");
        (code, output, said) = await Run("workspace", "new", beneath);
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Equal($"x {beneath} cannot be written: {file} is a file, not a folder.", said.TrimEnd());
    }

    [Fact(DisplayName = "§8.3: a session folder named twice in one icat workspace add is added once, and said to be named twice")]
    public async Task AFolderNamedTwiceIsAddedOnce()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string workspace = Path.Combine(folder, "twice" + InvestigationWorkspace.Extension);
        Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);

        // Named again, with a separator after it as a shell's completion leaves one, it is the same folder: added once, and
        // the whole request answered as done rather than refused as a member the second time.
        string client = Path.GetFullPath(demo.ClientSession);
        (InterCatExitCode code, _, string said) = await Run("workspace", "add", workspace, demo.ClientSession,
            demo.ServerSession, client + Path.DirectorySeparatorChar);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"{client} is given more than once; it is added once.", said, StringComparison.Ordinal);
        Assert.Equal(2, said.Split('\n').Count(line => line.Contains("Added session ", StringComparison.Ordinal)));
        Assert.DoesNotContain("was not added", said, StringComparison.Ordinal);
        Assert.Equal(2, InvestigationWorkspace.Read(workspace).Members.Count);
    }

    [Fact(DisplayName = "§8.4: icat workspace package measures, writes and verifies an investigation with its sessions, saying what its file carries")]
    public async Task AnInvestigationIsPackaged()
    {
        (string workspace, string client, string server) = await TwoHosts();
        Assert.Equal(InterCatExitCode.Success, (await Run(
            "workspace", "align", workspace, server, client, "--wall-clock", "--sync", "5ms", "--drift-ppm", "50")).Code);
        Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "note", workspace, "the retry storm")).Code);

        // Measured, nothing is written, and what the file carries is said before anything is saved.
        string package = Path.Combine(folder, "package");
        (InterCatExitCode measured, string measurement, _) = await Run("workspace", "package", workspace, "--check");
        Assert.Equal(InterCatExitCode.Success, measured);
        Assert.Contains("INVESTIGATION PACKAGE, MEASURED ONLY", measurement, StringComparison.Ordinal);
        Assert.Contains("The investigation's file goes with them, holding each session's capture, host and clock identities, one "
            + "alignment revision and one note. Notes are copied as they were written, never pseudonymized.", measurement,
            StringComparison.Ordinal);
        Assert.Contains("This was a measurement; nothing was written.", measurement, StringComparison.Ordinal);
        Assert.False(Directory.Exists(package));
        using (JsonDocument check = JsonDocument.Parse((await Run("workspace", "package", workspace, "--check", "--json")).Output))
        {
            JsonElement answer = check.RootElement;
            Assert.Equal(("workspace-package-v1", false, true), (answer.GetProperty("contract").GetString(),
                answer.GetProperty("performed").GetBoolean(), answer.GetProperty("unredacted").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, answer.GetProperty("directory").ValueKind);
            Assert.Equal([true, true], answer.GetProperty("members").EnumerateArray()
                .Select(member => member.GetProperty("copied").GetBoolean()));
            Assert.Equal(InvestigationPackage.Warning, answer.GetProperty("warning").GetString());
        }

        // Written, it is verified as a recipient would open it, and opens as the same investigation with its sessions present.
        (InterCatExitCode written, string saved, string progress) = await Run("workspace", "package", workspace, "--output", package);
        Assert.Equal(InterCatExitCode.Success, written);
        Assert.Contains("INVESTIGATION PACKAGE, UNREDACTED", saved, StringComparison.Ordinal);
        Assert.Contains("Reopening and verifying session 2 of 2", progress, StringComparison.Ordinal);
        Assert.Contains("! " + InvestigationPackage.Warning, progress, StringComparison.Ordinal);
        string opened = Path.Combine(package, Path.GetFileName(workspace));
        Assert.Contains($"icat workspace show \"{opened}\"", saved, StringComparison.Ordinal);
        (InterCatExitCode shown, string members, _) = await Run("workspace", "show", opened);
        Assert.Equal(InterCatExitCode.Success, shown);
        Assert.Matches($@"{client}\s+Present\s+2\s", members);
        Assert.Matches($@"{server}\s+Present\s+2\s", members);
        Assert.Equal(InvestigationWorkspace.Read(workspace).WorkspaceId, InvestigationWorkspace.Read(opened).WorkspaceId);

        // A folder that exists is refused, and nothing is written into it.
        (InterCatExitCode taken, _, string refusal) = await Run("workspace", "package", workspace, "--output", package);
        Assert.Equal(InterCatExitCode.InvalidInvocation, taken);
        Assert.Contains("exists", refusal, StringComparison.Ordinal);

        // Only the session chosen is copied, the other kept as a reference; that is no shortfall, so it succeeds.
        string chosen = Path.Combine(folder, "chosen");
        using (JsonDocument only = JsonDocument.Parse((await Run(
            "workspace", "package", workspace, "--output", chosen, "--only", client, "--json")).Output))
        {
            Assert.True(only.RootElement.GetProperty("performed").GetBoolean());
            Assert.Equal([true, false], only.RootElement.GetProperty("members").EnumerateArray()
                .Select(member => member.GetProperty("copied").GetBoolean()));
        }

        // A session that is not where it was last found cannot be copied: the package is partial, and its exit code says so.
        Directory.Delete(Path.Combine(folder, "demo", DemoInvestigation.ServerFolder), recursive: true);
        (InterCatExitCode partial, string short_, _) = await Run("workspace", "package", workspace, "--check");
        Assert.Equal(InterCatExitCode.PartialResultSuccess, partial);
        Assert.Matches(@"Copies\s+1 of 2 sessions", short_);
        Assert.Contains($"Not copied: {server} (demo-server), missing.", short_, StringComparison.Ordinal);
    }

    /// <summary>The demo's two sessions, one per host, in an investigation of their own; the client's short identity first.</summary>
    private async Task<(string Workspace, string Client, string Server)> TwoHosts()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string workspace = Path.Combine(folder, "case" + InvestigationWorkspace.Extension);
        (InterCatExitCode created, _, string createdSaid) = await Run("workspace", "new", workspace);
        Assert.Equal(InterCatExitCode.Success, created);
        Assert.Contains($"Investigation made at {workspace}. Add sessions with icat workspace add.", createdSaid, StringComparison.Ordinal);
        Assert.Contains("This investigation names no session yet: icat workspace add <workspace> <session-dir> adds one.",
            (await Run("workspace", "show", workspace)).Output + (await Run("workspace", "show", workspace)).Error, StringComparison.Ordinal);
        (InterCatExitCode added, _, string said) = await Run("workspace", "add", workspace, demo.ClientSession, demo.ServerSession);
        Assert.Equal(InterCatExitCode.Success, added);
        InvestigationWorkspaceFile made = InvestigationWorkspace.Read(workspace);
        string client = Short(made.Members[0].SessionId), server = Short(made.Members[1].SessionId);
        Assert.Contains($"Added session {client} at generation 2, recorded on host {Short(made.Members[0].HostId)}.", said,
            StringComparison.Ordinal);
        Assert.Contains($"Added session {server} at generation 2, recorded on host {Short(made.Members[1].HostId)}.", said,
            StringComparison.Ordinal);
        return (workspace, client, server);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

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
