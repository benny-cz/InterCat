using System.Globalization;
using System.Text;
using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The decoder fixture corpus (`contracts/decoders-v1.md` §3a, plan M8): every decoder InterCat lists has a corpus at its
/// version in fixtures/decoders, and decodes each of its cases exactly as the corpus says, so a decoder whose decoding of a
/// case changes cannot keep its version; and no corpus stands for a decoder or version InterCat no longer has.
/// </summary>
public sealed class DecoderCorpusTests
{
    [Fact(DisplayName = "P2: every decoder InterCat lists has a corpus at its version, and decodes each of its cases exactly as the corpus says")]
    public void EveryDecoderDecodesItsCorpus()
    {
        string directory = Path.Combine(RepositoryRoot(), "fixtures", "decoders");
        Assert.Equal(
            ContentDecoders.Known.Select(decoder => Corpus(decoder.Id, decoder.Version)).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var wrong = new List<string>();
        foreach (ContentDecoderInfo decoder in ContentDecoders.Known)
        {
            using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, Corpus(decoder.Id, decoder.Version))));
            JsonElement top = corpus.RootElement;
            Assert.Equal((decoder.Id, decoder.Version), (top.GetProperty("decoder").GetString(), top.GetProperty("version").GetInt32()));
            JsonElement[] cases = [.. top.GetProperty("cases").EnumerateArray()];
            Assert.NotEmpty(cases);
            foreach (JsonElement one in cases)
            {
                DecodedContent decoded = decoder.Id switch
                {
                    "http1-head" => Head(one),
                    "intercat-content-fixture" => Fixture(one),
                    _ => throw new InvalidOperationException($"No corpus reader decodes {decoder.Id}'s cases."),
                };
                string expected = Expected(one);
                string made = Said(decoded);
                if ((decoded.DecoderId, decoded.DecoderVersion) != (decoder.Id, decoder.Version) || expected != made)
                {
                    wrong.Add($"{decoder.Id} v{decoder.Version}, {one.GetProperty("name").GetString()}:\n  corpus {expected}\n  decoded {made}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    private static string Corpus(string id, int version) => string.Create(CultureInfo.InvariantCulture, $"{id}.v{version}.json");

    /// <summary>A case's head decoded whole, as a buffer of its part asks for it.</summary>
    private static DecodedContent Head(JsonElement one)
    {
        bool request = one.GetProperty("head").GetString() == "request";
        byte[] head = Bytes(one.GetProperty("bytes").GetString()!);
        var part = new SessionContentPartDetail
        {
            IsPart = true,
            Name = request ? "the request head of exchange 7" : "the response head of exchange 7",
            Complete = true,
            Length = head.Length,
            Bytes = head,
            BytesRead = true,
            Statement = "Whole.",
        };
        ObservationRowV1 row = Transfer(1, ObservationKind.Send, AccountingSide.SendSide, 1, null, 9) with
        {
            Mechanism = Mechanism.Http,
            EventId = request ? (ushort)2001 : (ushort)2003,
            HeaderProcessId = 4_242,
        };
        return ContentDecoders.DecodePart(part, row, new RawRecordId(Capture, 1, 1, 9))!;
    }

    /// <summary>A case's message kept by the content fixture up to its record limit, and decoded as a person asks.</summary>
    private static DecodedContent Fixture(JsonElement one)
    {
        int limit = one.GetProperty("recordLimit").GetInt32();
        ObservationRowV1 row = Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 1) with
        {
            ProviderId = System.Diagnostics.Tracing.EventSource.GetGuid(typeof(ContentFixtureEventSource)),
            EventId = ContentFixtureEventSource.MessageSentId,
            Mechanism = Mechanism.ApplicationSdk,
            Layer = ObservationLayer.Application,
            ByteDomain = ByteDomain.ApplicationPayload,
        };
        using var session = new TemporarySession();
        Publish(session.Store, [row], content: (ContentHeader(recordLimit: limit),
            [Content(row, Bytes(one.GetProperty("message").GetString()!), limit, ContentEncodingV1.Binary)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        return ContentDecoders.Decode(SessionContentQuery.Read(session.Store, page.SessionId, page.Records.Single(), revealBytes: true))!;
    }

    /// <summary>A corpus string's bytes: each character one byte, from U+0000 to U+00FF.</summary>
    private static byte[] Bytes(string text)
    {
        Assert.All(text, character => Assert.True(character <= 'ÿ', "A corpus's bytes are characters from U+0000 to U+00FF."));
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>What a case's corpus says its decoding is, in one line.</summary>
    private static string Expected(JsonElement one)
    {
        JsonElement read = one.GetProperty("read");
        string fields = string.Join("; ", one.GetProperty("fields").EnumerateArray().Select(field =>
            string.Create(CultureInfo.InvariantCulture,
                $"{field[0].GetString()} {field[1].GetInt64()}-{field[2].GetInt64()} {field[3].GetString()}")));
        return Line(read.ValueKind == JsonValueKind.Null ? null : (read[0].GetInt64(), read[1].GetInt64()), fields,
            one.GetProperty("notDecoded").GetString(), one.GetProperty("reachedEnd").GetBoolean());
    }

    /// <summary>What a decoding made, in the same line.</summary>
    private static string Said(DecodedContent decoded) => Line(
        decoded.Read is { } read ? (read.First, read.Last) : null,
        string.Join("; ", decoded.Fields.Select(field =>
            string.Create(CultureInfo.InvariantCulture, $"{field.Name} {field.Bytes.First}-{field.Bytes.Last} {field.Value}"))),
        decoded.NotDecoded,
        decoded.ReachedEnd);

    private static string Line((long First, long Last)? read, string fields, string? notDecoded, bool reachedEnd) =>
        string.Create(CultureInfo.InvariantCulture,
            $"read {(read is { } range ? $"{range.First}-{range.Last}" : "none")} | fields [{fields}] | not decoded: {notDecoded ?? "-"} | reached end: {reachedEnd}");

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root holding InterCat.slnx was not found.");
    }
}
