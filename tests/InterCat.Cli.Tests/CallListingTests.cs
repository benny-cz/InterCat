using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat operations as a person runs it on a process's calls (R18, R21): each call listed with its other end, named as its
/// group's other end is; how many calls of each side listed found theirs; and every option it cannot read refused with
/// what it expects.
/// </summary>
public sealed class CallListingTests : IDisposable
{
    private static readonly Guid Unreached = Guid.Parse("12345678-1234-4123-8123-123456789abc");

    private readonly TemporarySession session = new();

    public CallListingTests()
    {
        // A caller's three calls to the service control manager, whose executable is witnessed, two served, and a server
        // call of PID 2020 no client call reached, still open when the capture ended.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store,
        [
            .. rows.Select(row => row is { Mechanism: Mechanism.ProcessLifecycle, OwnerProcessId: 1_960 }
                ? row with { ResourceName = @"C:\Windows\System32\services.exe" }
                : row),
            RpcCall(400, ObservationKind.RequestStart, Direction.Inbound, raisedBy: 2_020, 70,
                activity: Guid.Parse("21212121-2222-4333-8444-555555555555"), interfaceUuid: Unreached),
        ], fields: fields, coverage: RpcLedger(alpc: true));
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat processes --pid says a process's RPC call records, which no transport record holds, and where its calls are listed with their other ends")]
    public async Task AProcesssDetailPointsToItsCalls()
    {
        // The caller's three calls, a start and a stop each, beside its saying it made no transport record.
        (InterCatExitCode code, string caller, string said) = await Run("processes", session.Path, "--pid", "400");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  RPC +6 call records; icat operations <directory> --pid 400 lists its calls, each with the process at its "
            + @"other end\r?$", caller);
        Assert.Contains("It made no transport send or receive record in this session", caller, StringComparison.Ordinal);

        // The JSON counts them for the PID asked about, and for no other.
        (_, string json, _) = await Run("processes", session.Path, "--pid", "1960", "--json");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] instances = [.. document.RootElement.GetProperty("instances").EnumerateArray()];
        Assert.Equal(6, instances.Single(instance => instance.GetProperty("process").GetProperty("processId").GetInt32() == 1_960)
            .GetProperty("rpcRecords").GetInt64());
        Assert.All(instances.Where(instance => instance.GetProperty("process").GetProperty("processId").GetInt32() != 1_960),
            instance => Assert.Equal(JsonValueKind.Null, instance.GetProperty("rpcRecords").ValueKind));
    }

    [Fact(DisplayName = "R18: icat operations lists a process's calls, each with its other end named as its group's is")]
    public async Task AProcessCallsNameTheirOtherEnds()
    {
        // The caller's calls: two reached the host, named by its executable and PID, and one sent nothing.
        (InterCatExitCode code, string client, string said) = await Run("operations", session.Path, "--pid", "400");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("ALPC sends and receives: 2 of the 3 client calls listed were served\n", client.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("  executable not witnessed · 400, client calls to 367abb81-9844-35f1-ad32-98f038001003: the first 3 of 3",
            client, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Start +Duration +Status +State +Procedure +Other end\r?$", client);
        Assert.Matches(@"(?m)^  executable not witnessed · 400 +client +.* +services\.exe: 2/3\r?$", client);
        Assert.Matches(@"(?m)^  0\.000010 s +2\.0 µs +0 +completed +7 +services\.exe · 1960\r?$", client);
        Assert.Matches(@"(?m)^  0\.000020 s +1\.0 µs +0 +completed +7 +no ALPC send on its thread during the call\r?$", client);
        Assert.Matches(@"(?m)^  0\.000030 s +4\.0 µs +5 +completed +7 +services\.exe · 1960\r?$", client);

        // The host's calls: two a client call reached - one whose executable was not witnessed, named by its PID as a
        // group's other end is - and one none did; a listing of servers alone counts no client call.
        (_, string server, _) = await Run("operations", session.Path, "--pid", "1960");
        Assert.Contains("ALPC sends and receives: 2 of the 3 server calls listed were reached by a client call\n",
            server.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  0\.000011 s +500 ns +0 +completed +7 +PID 400\r?$", server);
        Assert.Contains("  services.exe · 1960, server calls to 367abb81-9844-35f1-ad32-98f038001003: the first 3 of 3", server,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PID ·", server, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  0\.000021 s +200 ns +0 +completed +7 +no linked client call reached it\r?$", server);
        Assert.DoesNotContain("client calls listed", server, StringComparison.Ordinal);

        // None of a lone server call reached is said as none, agreeing with its total.
        (_, string unreached, _) = await Run("operations", session.Path, "--pid", "2020");
        Assert.Contains("ALPC sends and receives: none of the 1 server call listed was reached by a client call\n",
            unreached.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  0\.000040 s +untimed +none +open at capture end +none +no linked client call reached it\r?$", unreached);

        // Every group listed says both sides, and --calls bounds each group's calls.
        (_, string both, _) = await Run("operations", session.Path, "--interface", ServiceControlInterface.ToString(), "--calls", "1");
        Assert.Contains("ALPC sends and receives: 2 of the 3 client calls listed were served, and 2 of the 3 server calls listed "
            + "were reached by a client call\n", both.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("client calls to 367abb81-9844-35f1-ad32-98f038001003: the first 1 of 3", both, StringComparison.Ordinal);
        Assert.Contains("server calls to 367abb81-9844-35f1-ad32-98f038001003: the first 1 of 3", both, StringComparison.Ordinal);

        // The whole session's groups, one shown with how many --top 0 would add.
        (_, string all, _) = await Run("operations", session.Path, "--top", "1");
        Assert.Contains("2 of the 3 client calls listed were served, and 2 of the 4 server calls listed were reached by a client "
            + "call", all, StringComparison.Ordinal);
        Assert.Contains("  2 more groups; --top 0 lists every one.", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Start ", all, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "R18: icat operations refuses an option it cannot read, saying what it expects")]
    [InlineData("--pid", "4x", "--pid expects a process id; '4x' is not one.")]
    [InlineData("--interface", "scm", "--interface expects an RPC interface UUID; 'scm' is not one.")]
    [InlineData("--calls", "lots", "--calls expects a number of calls per group up to 10,000; 'lots' is not one.")]
    [InlineData("--top", "-1", "--top expects a number of groups up to 100,000, or 0 for all; '-1' is not one.")]
    [InlineData("--evidence-policy", "strict",
        "--evidence-policy expects direct-only, include-correlated, include-candidates or all-including-conflicting; 'strict' is not one.")]
    public async Task AnOptionItCannotReadIsRefused(string option, string value, string expected)
    {
        (InterCatExitCode code, string output, string said) = await Run("operations", session.Path, option, value);

        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains(expected, said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat operations needs a session's folder, and says so of none and of one that is not there")]
    public async Task AFolderIsRequired()
    {
        (InterCatExitCode none, _, string noneSaid) = await Run("operations");
        Assert.Equal(InterCatExitCode.InvalidInvocation, none);
        Assert.Contains("A session directory is required: icat operations <directory>", noneSaid, StringComparison.Ordinal);
        Assert.Contains("--evidence-policy      Which bindings name a process", noneSaid, StringComparison.Ordinal);

        string missing = Path.Combine(session.Path, "nowhere");
        (InterCatExitCode gone, _, string goneSaid) = await Run("operations", missing);
        Assert.Equal(InterCatExitCode.InvalidInvocation, gone);
        Assert.Contains($"No session directory at {missing}.", goneSaid, StringComparison.Ordinal);
    }

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
