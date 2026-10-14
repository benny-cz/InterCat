using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// A session's byte totals and a metric's records in the words icat metric answers in (R5, R18): the bytes' domain and the
/// accounting that took them, never their identifiers, and a count of one in the singular; and the paths of icat metric and
/// icat session no other test ran - a process focus, the matrix, the canonical request and a session's kept content.
/// </summary>
public sealed class MeasurementWordsTests : IDisposable
{
    private readonly TemporarySession session = new();

    public MeasurementWordsTests()
    {
        // Two processes, one sending 64 bytes to the other over loopback, and a send of 6 more.
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Create, 100, 1),
            Lifecycle(2, ObservationKind.Create, 200, 2),
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        ];
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 64),
        [
            Content(rows[2], new byte[40], 64),
            Content(rows[3], new byte[100], 64),
        ]));
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R5: icat session states a segment's byte totals and its kept content in the words icat metric answers in")]
    public async Task ASessionsByteTotalsAreSaidInWords()
    {
        (InterCatExitCode code, string report, string said) = await Run("session", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);

        // Each total's domain and accounting as a metric answer names them, its bytes as every size is written.
        Assert.Matches(@"(?m)^  Bytes +Accounting +Total +Measured on\r?$", report);
        Assert.Matches(@"(?m)^  carried by the transport +sender-accounted +70 B +100\.0 ?% of 2 declared contributions\r?$", report);
        Assert.Matches(@"(?m)^  carried by the transport +receiver-accounted +64 B +100\.0 ?% of 1 declared contribution\r?$", report);
        foreach (string identifier in new[] { "TransportObserved", "SendSide", "ReceiveSide" })
        {
            Assert.DoesNotContain(identifier, report, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"\d Bytes", report);

        // What the capture kept of its messages, under which policy, and where it never goes.
        Assert.Contains("RESTRICTED CONTENT", report, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Kept +104 B in 1 chunk beside the journal\r?$", report);
        Assert.Matches(@"(?m)^  Messages +2: 1 kept whole, 1 cut to the record limit, 0 not kept once the content limit was reached\r?$",
            report);
        Assert.Matches(@"(?m)^  Policy +test-scoped-content-v1: at most 64 B a record; its bytes may be shown as hex and text\r?$", report);
        Assert.Contains("Content is restricted evidence (ADR-036). icat export and a redacted package never carry it",
            report, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R5: icat metric states how many declared contributions it measured, and each record's accounting, in words")]
    public async Task AMetricsContributionsAndRecordsAreSaidInWords()
    {
        (InterCatExitCode code, string answer, string said) = await Run(
            "metric", session.Path, "--metric", "bytes-received", "--byte-domain", "TransportObserved", "--side", "receive",
            "--evidence", "3");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Measured on +100\.0 ?% of 1 declared contribution\r?$", answer);
        Assert.Matches(@"(?m)^  Time +Mechanism +Kind +Owner PID +Bytes +Side +Journal record\r?$", answer);
        Assert.Matches(@"(?m)^  0\.000011 s +TCP +receive +200 +64 +receiver-accounted +none\r?$", answer);
        Assert.DoesNotContain("ReceiveSide", answer, StringComparison.Ordinal);

        // A record that measures no bytes has no accounting to name.
        (_, string observations, _) = await Run("metric", session.Path, "--metric", "observations", "--evidence", "1");
        Assert.Matches(@"(?m)^  0\.000000 s +Process +create +100 +n/a +n/a +none\r?$", observations);
    }

    [Fact(DisplayName = "R18: icat metric names a process focus by its role, and refuses an other end with no focus to be relative to")]
    public async Task AProcessFocusIsNamedByItsRole()
    {
        (_, string listed, _) = await Run("processes", session.Path, "--json");
        string[] instances = [.. Regex.Matches(listed, @"""instanceId"": ""([0-9a-f]{32})""").Select(match => match.Groups[1].Value)];
        Assert.Equal(2, instances.Length);
        string[] bytesSent = ["metric", session.Path, "--metric", "bytes-sent", "--byte-domain", "TransportObserved", "--side", "send"];
        foreach ((string option, string role, string value) in new[]
        {
            ("--owner", "owner", "70 B"), ("--sender", "sender", "70 B"), ("--participant", "participant", "70 B"),
        })
        {
            (InterCatExitCode code, string answer, string said) = await Run([.. bytesSent, option, instances[0]]);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches($@"(?m)^  Process focus +{role} {instances[0]}\r?$", answer);
            Assert.Matches($@"(?m)^  Value +{value}\r?$", answer);
        }

        (InterCatExitCode received, string receiver, _) = await Run(
            "metric", session.Path, "--metric", "bytes-received", "--byte-domain", "TransportObserved", "--side", "receive",
            "--receiver", instances[1]);
        Assert.Equal(InterCatExitCode.Success, received);
        Assert.Matches($@"(?m)^  Process focus +receiver {instances[1]}\r?$", receiver);
        Assert.Matches(@"(?m)^  Value +64 B\r?$", receiver);

        (InterCatExitCode refused, _, string refusal) = await Run([.. bytesSent, "--peer", instances[1]]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
        Assert.Contains("A peer filter is relative to a focused process", refusal, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat metric prints its matrix and a request's canonical form as a person reads them")]
    public async Task TheMatrixAndTheCanonicalRequestAreRead()
    {
        (InterCatExitCode matrixCode, string matrix, _) = await Run("metric", "--matrix");
        Assert.Equal(InterCatExitCode.Success, matrixCode);
        Assert.Contains("METRIC COMPATIBILITY (SECTION 5.3)", matrix, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  BytesSent +yes +yes +no +one of TransportObserved, CompletedIo +one of SendSide, ReceiveSide, CanonicalOwner\r?$",
            matrix);
        Assert.Matches(@"(?m)^  Rate +yes +yes +no +from the numerator +from the numerator\r?$", matrix);
        Assert.Matches(@"(?m)^  MappingCapacity +no +no +yes +fixed Capacity +n/a\r?$", matrix);
        Assert.Matches(@"(?m)^  Errors +yes +yes +no +n/a +optional\r?$", matrix);
        Assert.Contains("A metric outside its basis is rejected with the compatible ones named, never substituted.", matrix,
            StringComparison.Ordinal);

        // The canonical request alone goes to standard output, as it is what is hashed; its identity is said beside it.
        (InterCatExitCode canonicalCode, string canonical, string identity) = await Run(
            "metric", session.Path, "--metric", "bytes-sent", "--byte-domain", "TransportObserved", "--side", "send",
            "--group-by", "process", "--top", "5", "--print-canonical");
        Assert.Equal(InterCatExitCode.Success, canonicalCode);
        Assert.StartsWith("{\"specVersion\":1,", canonical, StringComparison.Ordinal);
        Assert.Single(canonical.TrimEnd().Split('\n'));
        Assert.Matches(@"Query identity v1:sha256:[0-9a-f]{64}, top 5 \(query-identity-v1\)\.", identity);
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
