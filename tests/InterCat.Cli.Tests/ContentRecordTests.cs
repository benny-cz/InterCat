using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat content --record (ADR-112's addendum): a record named by its raw identity, which every generation keeps - a message's
/// content record as icat messages --json names it - shown as the locator form shows a record: its facts, and its bytes only
/// when asked.
/// </summary>
public sealed class ContentRecordTests : IDisposable
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    private readonly TemporarySession session = new();
    private readonly ObservationRowV1 content;

    public ContentRecordTests()
    {
        // A message published with its bytes, which its publisher said in a content record of their own.
        ObservationRowV1 published = SdkRecord(10, CooperativeSdk.MessagePublished, 100, 11, Frames, 8);
        content = SdkRecord(11, CooperativeSdk.MessageContent, 100, 12, Frames);
        ObservationRowV1[] rows = [Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\publisher.exe" }, published, content];
        SourceFieldRowV1[] fields =
        [
            .. new[] { published, content }.SelectMany(static row => new[]
            {
                Field(row, SourceField.ChannelEpoch, 1),
                Field(row, SourceField.MessageSequence, 1),
            }),
        ];
        _ = Publish(session.Store, rows, fields: fields, plan: Plan(),
            content: (ContentHeader(recordLimit: 4_096), [Content(content, "12345678"u8.ToArray(), 4_096, ContentEncodingV1.Binary)]));
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat content --record shows a record named by its raw identity, as icat messages --json names a message's content record - its facts, and its bytes only when asked - and refuses a record the session holds none of, or a locator beside it")]
    public async Task ItShowsARecordByItsRawIdentity()
    {
        // The session's identity and the content record's raw identity, as icat messages --json names them.
        (_, string json, _) = await Run("messages", session.Path, "--channel", Frames.ToString(), "--json");
        using JsonDocument messages = JsonDocument.Parse(json);
        string sessionId = messages.RootElement.GetProperty("sessionId").GetString()!;
        JsonElement record = messages.RootElement.GetProperty("channels")[0].GetProperty("list")[0].GetProperty("content").GetProperty("record");
        string raw = $"{record.GetProperty("stream").GetUInt32()}/{record.GetProperty("sourceEpoch").GetUInt32()}/{record.GetProperty("ordinal").GetUInt64()}";
        Assert.Equal($"{content.RawStreamId}/{content.RawSourceEpoch}/{content.RawRecordOrdinal}", raw);

        // Its facts, and its bytes only when asked, shown inert.
        string bytes = ContentBytesView.Rows("12345678"u8, 0).Single().Line;
        (InterCatExitCode code, string facts, string said) = await Run("content", session.Path, "--session-id", sessionId, "--record", raw);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("raw " + raw, facts, StringComparison.Ordinal);
        Assert.DoesNotContain(bytes, facts, StringComparison.Ordinal);
        (code, string shown, said) = await Run("content", session.Path, "--session-id", sessionId, "--record", raw, "--reveal");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains(bytes, shown, StringComparison.Ordinal);

        // In JSON, its facts alone, as the locator form states them.
        (code, string document, _) = await Run("content", session.Path, "--session-id", sessionId, "--record", raw, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument stated = JsonDocument.Parse(document);
        Assert.True(stated.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(8, stated.RootElement.GetProperty("fragment").GetProperty("kept").GetInt32());
        Assert.DoesNotContain("12345678", document, StringComparison.Ordinal);

        // Found by its raw identity alone, as the window will find it, with what the generation keeps of it; another session's
        // identity finds nothing of this one's, and a record it holds none of is none.
        Guid id = Guid.Parse(sessionId);
        SessionEvidenceRecord found = SessionRecords.Find(session.Store, id, content.RawStreamId, content.RawSourceEpoch, content.RawRecordOrdinal)!;
        Assert.Equal((content.RawRecordOrdinal, CooperativeSdk.MessageContent, 8), (found.Observation.RawRecordOrdinal, found.Observation.EventId,
            found.Content!.Fragment.Kept));
        Assert.Null(SessionRecords.Find(session.Store, id, content.RawStreamId, content.RawSourceEpoch, 999));
        Assert.Equal(SessionStore.AnotherSession, Assert.Throws<InvalidOperationException>(() =>
            SessionRecords.Find(session.Store, Guid.NewGuid(), content.RawStreamId, content.RawSourceEpoch, content.RawRecordOrdinal)).Message);
        session.Store.ReleaseSegmentReaders();

        // A raw identity restarts in every capture, so another session's identity reads none of this one's records.
        (code, _, said) = await Run("content", session.Path, "--session-id", Guid.NewGuid().ToString(), "--record", raw, "--reveal");
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Contains("This folder now holds another session.", said, StringComparison.Ordinal);
        Assert.DoesNotContain(bytes, said, StringComparison.Ordinal);

        // A record the session holds none of, a raw identity that is not one, and a locator beside it are refused in words.
        foreach ((string[] arguments, string refusal) in new (string[], string)[]
        {
            (["--record", "1/1/999"], "The session holds no record raw 1/1/999 in its current generation."),
            (["--record", "1/1"], "--record"),
            (["--record", raw, "--row", "0"], "--record names a record by its raw identity, which every generation keeps"),
        })
        {
            (code, _, said) = await Run(["content", session.Path, "--session-id", sessionId, .. arguments]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains(refusal, said, StringComparison.Ordinal);
        }
    }

    /// <summary>The normalizer plan of a capture that admitted the SDK's events, its content's among them.</summary>
    private static byte[] Plan() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        contract = "normalizer-plan-v1",
        descriptors = Enumerable.Range(2, 9).Select(static id => new { providerGuid = CooperativeSdk.ProviderId, eventId = id, mechanism = (int)Mechanism.ApplicationSdk }),
    });

    private static async Task<(InterCatExitCode Code, string Output, string Said)> Run(params string[] args)
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
            return (code, answered.ToString(), said.ToString() + answered.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
