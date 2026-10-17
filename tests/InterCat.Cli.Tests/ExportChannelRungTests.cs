using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat export reaches an RPC channel's rung from its process's by the channel's key and exports its calls, as the window
/// does (R18): every one of the scope up to --limit, saying when the limit left some out.
/// </summary>
public sealed class ExportChannelRungTests
{
    [Fact(DisplayName = "R18: icat export --at an RPC channel's key exports its calls, up to --limit, and says when the limit left some out")]
    public async Task IcatExportExportsAChannelsCalls()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields,
            coverage: RpcLedger(alpc: true));
        ProcessNode client = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 400);
        string channel = Assert.Single(SessionRpcCalls.Channels(session.Store, client.Id).Channels).Key;
        session.Store.ReleaseSegmentReaders();
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string[] at = ["--at", client.GroupKey, "--at", client.Id.ToString(), "--at", channel];
            string whole = Path.Combine(folder, "calls.json");
            (InterCatExitCode code, string text, string said) = await Run(["export", session.Path, "--output", whole, .. at]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"Rows\s+3\b", text);
            using (JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(whole)))
            {
                Assert.Equal(["RPC call at +0.000010 s", "RPC call at +0.000020 s", "RPC call at +0.000030 s"],
                    json.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("label").GetString()));
            }

            // --limit bounds the calls as it bounds an evidence export's records, and the file and the report say so.
            string limited = Path.Combine(folder, "limited.json");
            (code, text, said) = await Run(["export", session.Path, "--output", limited, .. at, "--limit", "2"]);
            Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
            Assert.Matches(@"Complete\s+no\b", text);
            Assert.Contains("The export holds this scope's first 2 calls, leaving the rest out; raise --limit for them.", text, Ordinal);

            // A limit it cannot use is refused with what it bounds.
            (code, _, said) = await Run(["export", session.Path, "--output", Path.Combine(folder, "x.json"), .. at, "--limit", "0"]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("--limit must be an integer from 1 to 1,000,000: the records an --evidence export holds, or the calls "
                + "or exchanges of an RPC channel's or HTTP exchanges' rung.", said, Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
