using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat messages --operations (ADR-109): the operations applications instrumented with InterCat's SDK said they started and
/// completed, each under the operation it was started under - another process's, or one the capture never recorded - with
/// what became of it, in words and as a document.
/// </summary>
public sealed class MessagesOperationsTests : IDisposable
{
    private static readonly Guid Request = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid Serving = Guid.Parse("1a2b3c4d-5e6f-4a0b-9c1d-2e3f4a5b6c7d");
    private static readonly Guid Answer = Guid.Parse("2b3c4d5e-6f7a-4b1c-8d2e-3f4a5b6c7d8e");
    private static readonly Guid Elsewhere = Guid.Parse("3c4d5e6f-7a8b-4c2d-9e3f-4a5b6c7d8e9f");
    private static readonly Guid Unrecorded = Guid.Parse("4d5e6f7a-8b9c-4d3e-8f4a-5b6c7d8e9fa0");
    private static readonly Guid Pending = Guid.Parse("5e6f7a8b-9cad-4e4f-9a5b-6c7d8e9fa0b1");

    private readonly TemporarySession session = new();

    public MessagesOperationsTests()
    {
        // The client's request, served by the server under it, answered by the client under the serving; a serving under a
        // request of another capture's, which failed; and a request still open when the capture ended.
        ulong ordinal = 10;
        ObservationRowV1 Start(long ticks, int process, Guid operation, Guid? parent, string name) =>
            SdkRecord(ticks, CooperativeSdk.OperationStarted, process, ++ordinal, operation) with
            {
                Kind = ObservationKind.RequestStart,
                RelatedActivityId = parent,
                ResourceName = name,
            };
        ObservationRowV1 Complete(long ticks, int process, Guid operation, long status) =>
            SdkRecord(ticks, CooperativeSdk.OperationCompleted, process, ++ordinal, operation) with
            {
                Kind = ObservationKind.RequestEnd,
                StatusCode = status,
                StatusAvailability = FieldAvailability.Present,
            };
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
            Start(10, 100, Request, null, "request-1"),
            Start(20, 200, Serving, Request, "serve-1"),
            Start(30, 100, Answer, Serving, "answer-1"),
            Complete(35, 100, Answer, 0),
            Complete(40, 200, Serving, 0),
            Complete(50, 100, Request, 0),
            Start(60, 200, Elsewhere, Unrecorded, "serve-9"),
            Complete(65, 200, Elsewhere, 3),
            Start(70, 100, Pending, null, "request-2"),
        ];
        _ = Publish(session.Store, rows);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat messages --operations lists each operation under the one it was started under, in its process, with what became of it - those under a parent the capture never recorded headed by it - and says what the capture covered of the SDK")]
    public async Task ItListsTheTree()
    {
        (InterCatExitCode code, string output, _) = await Run("messages", session.Path, "--operations");
        Assert.Equal(InterCatExitCode.Success, code);
        string[] lines = output.ReplaceLineEndings("\n").Split('\n');
        string words = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        Assert.Contains("COOPERATIVE OPERATIONS", words, StringComparison.Ordinal);
        Assert.Contains("Operations 5: 4 completed (1 failed), 1 open, 0 with no start recorded, 0 conflicting", words, StringComparison.Ordinal);
        Assert.Contains("Application SDK's coverage over the session is unknown: this generation publishes no coverage ledger.",
            words, StringComparison.Ordinal);

        // Each operation a line, indented by its depth: the client's request, the server's serving under it, the client's answer
        // under that; then the open request; then, headed by the parent never recorded, the serving under it.
        string[] tree = [.. lines.SkipWhile(line => !line.Contains("request-1", StringComparison.Ordinal)).Where(line => line.Length > 0)];
        Assert.Equal(
            [
                "  request-1 · client.exe · 100 · succeeded in 40.0 µs",
                "    serve-1 · server.exe · 200 · succeeded in 20.0 µs",
                "      answer-1 · client.exe · 100 · succeeded in 5.0 µs",
                "  request-2 · client.exe · 100 · open: no completion recorded",
                $"  Under operation {Unrecorded}, which this capture did not record: another capture's, or one begun before it:",
                "    serve-9 · server.exe · 200 · failed with status 3 in 5.0 µs",
            ],
            tree);
    }

    [Fact(DisplayName = "R18: icat messages --operations --json is the cooperative-operations-v1 document: every operation in the tree's order with its depth, place, state, status and records, each record's process, and the parents never recorded")]
    public async Task ItsDocumentHoldsTheTree()
    {
        (InterCatExitCode code, string output, _) = await Run("messages", session.Path, "--operations", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(("cooperative-operations-v1", "cooperative-operation-v1", 0L),
            (root.GetProperty("contract").GetString(), root.GetProperty("operationRule").GetString(), root.GetProperty("withoutOperation").GetInt64()));
        JsonElement counts = root.GetProperty("counts");
        Assert.Equal((5, 4, 1, 1, 0, 0), (counts.GetProperty("operations").GetInt32(), counts.GetProperty("completed").GetInt32(),
            counts.GetProperty("failed").GetInt32(), counts.GetProperty("open").GetInt32(), counts.GetProperty("startUnseen").GetInt32(),
            counts.GetProperty("conflicting").GetInt32()));
        Assert.Equal([Unrecorded], root.GetProperty("unrecordedParents").EnumerateArray().Select(parent => parent.GetGuid()));

        JsonElement[] operations = [.. root.GetProperty("operations").EnumerateArray()];
        Assert.Equal(
            [(Request, 0, "Root"), (Serving, 1, "Child"), (Answer, 2, "Child"), (Pending, 0, "Root"), (Elsewhere, 0, "UnderUnrecorded")],
            operations.Select(operation => (operation.GetProperty("operation").GetGuid(), operation.GetProperty("depth").GetInt32(),
                operation.GetProperty("place").GetString()!)));
        JsonElement serving = operations[1];
        Assert.Equal((Request, "serve-1", "Completed", 0L, 20_000L), (serving.GetProperty("parent").GetGuid(), serving.GetProperty("name").GetString(),
            serving.GetProperty("state").GetString(), serving.GetProperty("status").GetInt64(), serving.GetProperty("durationNanoseconds").GetInt64()));
        Assert.Equal((200, @"C:\Tools\server.exe"), (serving.GetProperty("start").GetProperty("processId").GetInt32(),
            serving.GetProperty("start").GetProperty("process").GetProperty("imagePath").GetString()));

        // An open operation has no completion, status or duration; a failure states its status.
        JsonElement pending = operations[3];
        Assert.Equal(("Open", JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (pending.GetProperty("state").GetString(),
            pending.GetProperty("completion").ValueKind, pending.GetProperty("status").ValueKind, pending.GetProperty("durationNanoseconds").ValueKind));
        Assert.Equal((Unrecorded, 3L), (operations[4].GetProperty("parent").GetGuid(), operations[4].GetProperty("status").GetInt64()));
    }

    [Fact(DisplayName = "R18: icat messages --operations lists operations, not a channel's messages, so it refuses --channel and --messages, saying why")]
    public async Task ItRefusesAChannelsOptions()
    {
        (InterCatExitCode code, string output, string said) = await Run("messages", session.Path, "--operations", "--channel", Guid.Empty.ToString());
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains("--operations lists the operations applications started, not a channel's messages: use it without --channel or --messages.",
            said, StringComparison.Ordinal);
        Assert.Equal(InterCatExitCode.InvalidInvocation, (await Run("messages", session.Path, "--operations", "--messages", "5")).Code);
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
