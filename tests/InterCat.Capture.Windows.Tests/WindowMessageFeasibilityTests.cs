using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Window messages (ADR-086): FX-WMSG-001's messages, one process sending and posting another's window, measured under
/// Microsoft-Windows-Win32k's message pump keyword: every one at both ends, each send naming its receiver, each end joined to
/// the other by its queue message pointer, and none naming which message it was; what collecting the keyword costs; and the
/// keyword inventoried as the mechanism's lead.
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
    }

    [Fact(DisplayName = "P27: window messages have an inventoried lead - Win32k's message pump keyword at the informational level, requested by no profile and admitting no event, its measured notes and Low cost stated beside its throughput regression - and no tier on any build")]
    public void WindowMessagesHaveALeadAndNoTier()
    {
        WindowsSourceDefinition win32k = WindowsSourceCatalog.Find(WindowsSourceCatalog.Win32kSourceId)!;
        Assert.Equal((SourceKind.ManifestProvider, "Microsoft-Windows-Win32k", "win:Informational"), (win32k.Kind, win32k.ProviderName, win32k.Level));
        Assert.Equal([Mechanism.WindowMessage], win32k.Mechanisms);
        Assert.Equal((SourceContractStatus.Experimental, false), (win32k.ContractStatus, win32k.SupportsCaptureSideProcessFilter));
        Assert.Empty(win32k.AdmittedEvents);

        // Its keyword is the one FX-WMSG-001 was measured under, by the name and mask the inspection read from this build's
        // registered schema.
        Assert.Equal((0x40_0000UL, 0UL), (win32k.MatchAnyKeyword, win32k.MatchAllKeyword));
        Assert.Equal(["MessagePump"], win32k.RequestedKeywords);
        using (JsonDocument inspection = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "bench", "results",
            "ui-message-provider-inspection-20261105T163256Z", "report.json"))))
        {
            JsonElement provider = inspection.RootElement.GetProperty("providers").EnumerateArray()
                .Single(candidate => candidate.GetProperty("name").GetString() == win32k.ProviderName);
            Assert.Contains(provider.GetProperty("keywords").EnumerateArray(), keyword
                => (keyword.GetProperty("name").GetString(), keyword.GetProperty("mask").GetString()) == ("MessagePump", "0x400000"));
        }

        // Its cost is measured, Low in the processor time it adds, with the throughput regression its notes state beside the class.
        Assert.Equal(OverheadClass.Low, win32k.Overhead);
        Assert.Matches(@"^bench/results/window-message-impact-\d{8}T\d{6}Z/impact\.json$", win32k.OverheadEvidence);
        using (JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), win32k.OverheadEvidence!))))
        {
            Assert.Equal((win32k.Overhead.ToString(), false), (impact.RootElement.GetProperty("attributedOverhead").GetString(),
                impact.RootElement.GetProperty("throughputTargetMet").GetBoolean()));
        }

        Assert.Contains(win32k.Notes, note => note.Contains("past the 5% target", StringComparison.Ordinal) && note.Contains("never Explore", StringComparison.Ordinal));
        Assert.Contains(win32k.Notes, note => note.Contains("FX-WMSG-001", StringComparison.Ordinal) && note.Contains("at both ends", StringComparison.Ordinal));
        Assert.Contains(win32k.Notes, note => note.StartsWith("No record names which message it was", StringComparison.Ordinal));
        Assert.Contains(win32k.Notes, note => note.Contains("only within a message's life", StringComparison.Ordinal));

        // No profile requests it, no other source claims window messages, and no build carries a tier for them.
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures),
            profile => profile.Sources.Any(source => source.SourceId == win32k.SourceId));
        Assert.StartsWith("No capture profile requests it.", win32k.FilteringNotes, StringComparison.Ordinal);
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != win32k.SourceId),
            source => Assert.DoesNotContain(Mechanism.WindowMessage, source.Mechanisms));
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
