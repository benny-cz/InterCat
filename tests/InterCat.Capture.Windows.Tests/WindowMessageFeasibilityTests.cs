using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Window messages (ADR-086): FX-WMSG-001's messages, one process sending and posting another's window, measured under
/// Microsoft-Windows-Win32k's message pump keyword: every one at both ends, each send naming its receiver, each end joined to
/// the other by its queue message pointer, and none naming which message it was; and what collecting the keyword costs.
/// </summary>
public sealed class WindowMessageFeasibilityTests
{
    [Fact(DisplayName = "P7: FX-WMSG-001's every message one process sent or posted another's window is recorded at both ends by Win32k's message pump - a send's start naming its receiver's process and thread, each end joined to the other by its queue message pointer - and none names which message it was")]
    public void EveryMessageIsRecordedAtBothEnds()
    {
        string root = RepositoryRoot();
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "FX-WMSG-001", "evidence", "measurement.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        foreach (JsonElement run in runs)
        {
            // The run's own report, as the probe wrote it, says what its measurement states.
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, run.GetProperty("bench").GetString()!, "report.json")));
            JsonElement r = report.RootElement;
            int copies = r.GetProperty("truth").GetProperty("copiesSent").GetInt32();
            int posts = r.GetProperty("truth").GetProperty("messagesPosted").GetInt32();
            Assert.Equal((copies, posts), (run.GetProperty("sends").GetProperty("truth").GetInt32(), run.GetProperty("posts").GetProperty("truth").GetInt32()));
            Assert.Equal(0, r.GetProperty("eventsLost").GetInt32());
            Assert.True(r.GetProperty("receiversWindowFound").GetBoolean());

            // Every send at both ends: its start at the sender naming the receiver's process and one thread of it, its stop, and
            // its retrieval's start and stop at the receiver, each joined to the start by the queue message's pointer.
            Assert.Equal((copies, copies, copies, copies, copies),
                (r.GetProperty("sentStartsAtTheSender").GetInt32(), r.GetProperty("sentStartsNamingTheReceiversProcess").GetInt32(),
                    r.GetProperty("sentStopsAtTheSenderJoinedToAStart").GetInt32(), r.GetProperty("sendsRetrievedAtTheReceiverJoinedToAStart").GetInt32(),
                    r.GetProperty("retrievalStopsAtTheReceiverJoinedToAStart").GetInt32()));
            Assert.True(r.GetProperty("sentStartsNamingOneReceiverThread").GetBoolean());

            // Every post, and the close after them, queued in the sender and retrieved in the receiver, joined by its pointer.
            Assert.Equal((posts + 1, posts + 1, posts + 1, posts + 1),
                (r.GetProperty("postsQueuedToTheWindow").GetInt32(), r.GetProperty("postsQueuedRaisedIn").GetProperty("sender").GetInt32(),
                    r.GetProperty("postsRetrievedAtTheReceiver").GetInt32(), r.GetProperty("postsRetrievedJoinedToTheirQueueingByPointer").GetInt32()));
            Assert.Equal(["sender"], r.GetProperty("postsQueuedRaisedIn").EnumerateObject().Select(raised => raised.Name));

            // And no record of the two processes says which message it was: no number, no parameter.
            Assert.Equal((0, 0), (r.GetProperty("workloadRecordsCarryingAMessageNumber").GetInt32(), r.GetProperty("workloadRecordsCarryingAParameter").GetInt32()));
        }

        // No catalog source records window messages yet, and no build carries a tier for them.
        Assert.DoesNotContain(WindowsSourceCatalog.All, source => source.Mechanisms.Contains(Mechanism.WindowMessage));
        Assert.DoesNotContain(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.WindowMessage);
    }

    [Fact(DisplayName = "§12: collecting Win32k's message pump is measured in two series of pairs over a message-bound workload - Low in the processor time it adds, the workload's own a twentieth more and its throughput past §12's target, nothing lost - and the machine's other windows raise its records whatever the workload")]
    public void TheMessagePumpsCollectionCostIsMeasured()
    {
        string results = Path.Combine(RepositoryRoot(), "bench", "results");
        string[] series = [.. Directory.EnumerateDirectories(results, "window-message-impact-*")
            .Select(directory => Path.Combine(directory, "impact.json")).Where(File.Exists).Order(StringComparer.Ordinal)];
        Assert.Equal(2, series.Length);
        Assert.All(series, file =>
        {
            // Nine pairs of FX-WMSG-001's sender sending and posting its receiver's window 20,000 messages each, nothing lost.
            using JsonDocument impact = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = impact.RootElement;
            Assert.Equal((9, 0L), (root.GetProperty("pairs").GetArrayLength(), root.GetProperty("eventsLost").GetInt64()));
            Assert.Equal(("window-message --truth {truth} --copies 20000 --posts 20000 --bytes 64", "MessagePump (0x400000)"),
                (root.GetProperty("workload").GetString(), root.GetProperty("keyword").GetString()));

            // What the session added in processor time, the workload's and the consumer's, is Low in the median and in every
            // pair; the machine's busy share, which its other load moves, is within the processor target.
            double attributed = root.GetProperty("medianAttributedImpactPercentagePoints").GetDouble();
            Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(attributed));
            Assert.Equal(nameof(OverheadClass.Low), root.GetProperty("attributedOverhead").GetString());
            Assert.All(root.GetProperty("pairs").EnumerateArray(), pair => Assert.InRange(
                pair.GetProperty("attributedImpactPercentagePoints").GetDouble(), -OverheadClassCalculator.LowCeilingPercentagePoints, OverheadClassCalculator.LowCeilingPercentagePoints));
            Assert.True(root.GetProperty("cpuTargetMet").GetBoolean());

            // The workload's own processor time is about a twentieth more, each record raised in the thread that sends, posts
            // or retrieves its message, and its throughput falls past §12's 5% target.
            Assert.InRange(root.GetProperty("medianWorkloadCpuIncreasePercent").GetDouble(), 2, 10);
            Assert.True(root.GetProperty("medianThroughputRegressionPercent").GetDouble() > 5, file);
            Assert.False(root.GetProperty("throughputTargetMet").GetBoolean());

            // The consumer's time is about half a microsecond a record; the workload's run raises tens of thousands of records a
            // second, and the machine's other windows thousands in the second before it.
            Assert.InRange(root.GetProperty("medianConsumerMicrosecondsPerEvent").GetDouble(), 0, 5);
            Assert.InRange(root.GetProperty("medianMessagePumpEventsPerSecond").GetDouble(), 40_000, 70_000);
            Assert.InRange(root.GetProperty("medianMessagePumpEventsInTheSecondBefore").GetDouble(), 1_000, 10_000);
        });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
