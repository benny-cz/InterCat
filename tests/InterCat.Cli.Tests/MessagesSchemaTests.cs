using System.Text.Json;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat messages and its --operations (ADR-111): what of the SDK's schema never reached the capture, said beneath what the
/// capture covered, and each process's announcement in their documents.
/// </summary>
public sealed class MessagesSchemaTests : IDisposable
{
    private const string Later = "native.exe · 200 announced the SDK's schema 1.3, later than the 1.2 this InterCat knows: what it added "
        + "never reached the capture, and is counted nowhere here.";

    private readonly TemporarySession session = new();

    public MessagesSchemaTests()
    {
        // A native process announcing a later minor than this InterCat knows, and opening a channel it knows of.
        ObservationRowV1 announced = SdkRecord(10, CooperativeSdk.SdkStarted, 200, 11, null) with
        {
            Kind = ObservationKind.Inventory,
            ResourceName = "native-1.3",
        };
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\native.exe" },
            announced,
        ];
        _ = Publish(session.Store, rows, fields: [Field(announced, SourceField.SdkSchemaMinor, 3), Field(announced, SourceField.SourceClockFrequency, 3_000_000)]);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R21: icat messages and its --operations say what of the SDK's schema never reached the capture - a later minor a process announced - beneath what the capture covered, and their documents hold each announcement")]
    public async Task ItSaysWhatNeverReachedTheCapture()
    {
        string[][] listings = [["messages", session.Path], ["messages", session.Path, "--operations"]];
        foreach (string[] arguments in listings)
        {
            (InterCatExitCode code, string output) = await Run(arguments);
            Assert.Equal(InterCatExitCode.Success, code);
            Assert.Contains(Later, System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " "), StringComparison.Ordinal);

            (_, string json) = await Run([.. arguments, "--json"]);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement announcement = Assert.Single(document.RootElement.GetProperty("announcements").EnumerateArray());
            Assert.Equal((200, 1, 3, "native-1.3", 3_000_000L, @"C:\Tools\native.exe"), (announcement.GetProperty("processId").GetInt32(),
                announcement.GetProperty("major").GetInt32(), announcement.GetProperty("minor").GetInt32(), announcement.GetProperty("library").GetString(),
                announcement.GetProperty("ticksPerSecond").GetInt64(), announcement.GetProperty("process").GetProperty("imagePath").GetString()));
            Assert.Equal([Later], document.RootElement.GetProperty("unreached").EnumerateArray().Select(line => line.GetString()));
        }
    }

    private static async Task<(InterCatExitCode Code, string Output)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        using var answered = new StringWriter();
        Console.SetOut(answered);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString());
        }
        finally
        {
            Console.SetOut(output);
        }
    }
}
