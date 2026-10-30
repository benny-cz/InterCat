using InterCat.Analysis.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat export ranks by the connections each process made or accepted, as the window's selector does (R18): the
/// `--rank-by` names connections-made and connections-accepted, each export naming the ranking its rows use.
/// </summary>
public sealed class ExportConnectionRankingTests
{
    [Fact(DisplayName = "R18: icat export --rank-by connections-made and connections-accepted rank the rows by each process's connections, and name the ranking")]
    public async Task IcatExportRanksByConnections()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\service.exe" },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        session.Store.ReleaseSegmentReaders();
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach ((string name, string first) in new[] { ("connections-made", "client.exe"), ("connections-accepted", "service.exe") })
            {
                string output = Path.Combine(folder, name + ".json");
                (InterCatExitCode code, _, string said) = await Run("export", session.Path, "--output", output, "--rank-by", name);
                Assert.True(code == InterCatExitCode.Success, said);
                string content = await File.ReadAllTextAsync(output);
                Assert.Contains($"\"rankedBy\": \"{name}\"", content, Ordinal);
                Assert.True(content.IndexOf(first, Ordinal) < content.IndexOf(first == "client.exe" ? "service.exe" : "client.exe", Ordinal),
                    $"{name}: {first} should rank first");
            }

            // A ranking it does not know is refused with every one it does.
            (InterCatExitCode refused, _, string why) = await Run("export", session.Path, "--output", Path.Combine(folder, "x.json"),
                "--rank-by", "connections");
            Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
            Assert.Contains("active-peers, connections-made or connections-accepted; 'connections' is not one.", why, Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "R18: icat export ranks by every name --rank-by takes, and where the capture collected no RPC call says so and ranks by records")]
    public async Task IcatExportRanksByEveryName()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\service.exe" },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ], coverage: TransportLedger(tcp: true, udp: false));
        session.Store.ReleaseSegmentReaders();
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Each transport ranking ranks by itself, and says so in its file.
            foreach (string name in new[] { "records", "bytes-sent", "bytes-received", "bytes-sent-and-received", "active-peers" })
            {
                string output = Path.Combine(folder, name + ".json");
                (InterCatExitCode code, string text, string said) = await Run("export", session.Path, "--output", output, "--rank-by", name);
                Assert.True(code == InterCatExitCode.Success, said);
                Assert.Contains($"\"rankedBy\": \"{name}\"", await File.ReadAllTextAsync(output), Ordinal);
                Assert.DoesNotContain("RPC calls were asked to rank the rows", text, Ordinal);
            }

            // A capture that collected TCP and no RPC ranks by records instead, and says why rather than ranking by nothing.
            foreach (string name in new[] { "rpc-calls-made", "rpc-calls-served", "rpc-errors", "rpc-call-time-median", "rpc-serve-time-median" })
            {
                string output = Path.Combine(folder, name + ".json");
                (InterCatExitCode code, string text, string said) = await Run("export", session.Path, "--output", output, "--rank-by", name);
                Assert.True(code == InterCatExitCode.Success, said);
                string content = await File.ReadAllTextAsync(output);
                Assert.Contains("\"rankedBy\": \"records\"", content, Ordinal);
                Assert.Contains("RPC calls were asked to rank the rows, but ", text, Ordinal);
                Assert.Contains("; the rows rank by records.", content, Ordinal);
            }
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
