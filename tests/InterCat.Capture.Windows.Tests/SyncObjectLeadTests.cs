using System.Globalization;
using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The named synchronization objects work package's capability result (plan M6, ADR-082): the kernel's handle group, which
/// a lab session's private system logger collected against FX-SYNC-001's three named objects two processes share, records
/// each process's handles to each - its creation naming nothing, its close naming the object - at one address with the
/// object's type, and the object group each object's creation and deletion. The dispatcher's ready thread, set against the
/// thread its processor ran, readies every wait the kernel blocked from the signaling process's thread, and names no object.
/// </summary>
public sealed class SyncObjectLeadTests
{
    private static readonly string[] Kinds = ["event", "semaphore", "mutex"];

    private static readonly string[] Roles = ["creator", "opener"];

    // A paired ready readied its waiter in a user-mode wait, or a thread the session did not see switched out waiting.
    private static readonly string[] PairedWaits = ["UserRequest", "not seen waiting"];

    // The object manager's handle creations, closes and handle rundown, by their opcodes.
    private static readonly string[] HandleOpcodes = ["32", "33", "39"];

    // A wait's span is the waiter's two records about it; the two clocks are read apart by this much.
    private static readonly long Slack = TimeSpan.FromMilliseconds(0.02).Ticks;

    private sealed record Row(long Utc, string Record, string Role, string Kind, string Object, string Handle, string Type, string TypeName,
        string NameCarried, string AwakenedRole, string AwakenedWait, string AdjustReason, string FromDpc, string Thread);

    private sealed record Truth(string Kind, long? Round, string? Status, string? Resource, long Utc);

    private sealed record Wait(string Waiter, string Signaler, string Kind, long Round, long SignalUtc, long BlockedAfterUtc, long EndedUtc);

    [Fact(DisplayName = "R22: FX-SYNC-001's handle records join each process's handles to a named object by its address - each creation named by nothing but its close, one object to an address, the object's type, and no handle of it elsewhere")]
    public void HandlesJoinByTheirObjectsAddress()
    {
        Row[] rows = Ledger("ledger.csv");
        var types = new Dictionary<string, (string Type, string Name)>
        {
            ["event"] = ("21", "Event"),
            ["semaphore"] = ("24", "Semaphore"),
            ["mutex"] = ("22", "Mutant"),
        };
        foreach (string kind in Kinds)
        {
            // Each process created and closed one handle to the object: four records, one object.
            Row[] handles = [.. rows.Where(row => row.Kind == kind)];
            Assert.Equal(["Close creator", "Close opener", "Create creator", "Create opener"],
                handles.Select(row => $"{row.Record} {row.Role}").Order(StringComparer.Ordinal));
            Assert.Single(handles.Select(row => row.Object).Distinct());
            Assert.All(handles, row => Assert.Equal(types[kind], (row.Type, row.TypeName)));

            // A creation carries no name; its close carries the object's and closes the creation's handle, later.
            Assert.All(handles, row => Assert.Equal(row.Record == "Close" ? "True" : "False", row.NameCarried));
            foreach (string role in Roles)
            {
                Row created = Assert.Single(handles, row => row.Record == "Create" && row.Role == role);
                Row closed = Assert.Single(handles, row => row.Record == "Close" && row.Role == role);
                Assert.Equal(created.Handle, closed.Handle);
                Assert.True(created.Utc < closed.Utc);
            }
        }

        // The objects' handle records were raised in the two processes and nowhere else, and each run measured them alike.
        Assert.All(rows.Where(row => row.Record is "Create" or "Close"), row => Assert.Contains(row.Role, Roles));
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal(3, run.GetProperty("objectsWhole").GetInt32());
            Assert.Equal(0, run.GetProperty("eventsLost").GetInt32());
            Assert.Equal(0, run.GetProperty("counts").GetProperty("handleCreationsNamed").GetInt32());
        });
    }

    [Fact(DisplayName = "R22: an object's address names it only from its creation to its deletion - FX-SYNC-001's object group records each named object created in the creator before its first handle and deleted after its last close, at the address its handles carry")]
    public void AnObjectLivesFromItsCreationToItsDeletion()
    {
        Row[] rows = Ledger("ledger-objects.csv");
        foreach (string kind in Kinds)
        {
            Row[] handles = [.. rows.Where(row => row.Kind == kind)];
            string address = Assert.Single(handles.Select(row => row.Object).Distinct());
            Row created = Assert.Single(rows, row => row.Record == "Created" && row.Object == address);
            Row deleted = Assert.Single(rows, row => row.Record == "Deleted" && row.Object == address);
            Assert.Equal("creator", created.Role);
            Assert.Equal(handles[0].Type, created.Type);
            Assert.True(created.Utc <= handles.Min(row => row.Utc), $"the {kind} is created after a handle to it");
            Assert.True(deleted.Utc >= handles.Max(row => row.Utc), $"the {kind} is deleted before a handle to it closes");
        }

        // The object group's references are the cost of its lifetimes: millions of records a run.
        using JsonDocument measurement = Measurement();
        JsonElement objects = measurement.RootElement.GetProperty("runs").EnumerateArray().Single(run => run.GetProperty("run").GetString() == "objects-1");
        Assert.InRange(objects.GetProperty("counts").GetProperty("references").GetInt64(), 1_000_000, long.MaxValue);
    }

    [Fact(DisplayName = "P8: FX-SYNC-001's every wait the kernel blocked has the ready of its thread from the signaling process inside the wait's span, and no ready of a thread in a user-mode wait between the two processes is left unpaired - a ready names no object, so only a truth's wait makes one a signal")]
    public void EveryWaitTheKernelBlockedHasItsReady()
    {
        Row[] rows = Ledger("ledger.csv");
        var logs = new Dictionary<string, Truth[]> { ["creator"] = TruthLog("truth-creator.jsonl"), ["opener"] = TruthLog("truth-opener.jsonl") };

        // The clocks' offset: each handle's kernel record against the truth record its process wrote once the call returned.
        long[] anchors = [.. Kinds.SelectMany(kind => Roles.SelectMany(role => new[]
        {
            rows.Single(row => row.Kind == kind && row.Record == "Create" && row.Role == role).Utc
                - logs[role].Single(record => record.Kind is "ObjectCreated" or "ObjectOpened" && KindOf(record.Resource) == kind).Utc,
            rows.Single(row => row.Kind == kind && row.Record == "Close" && row.Role == role).Utc
                - logs[role].Single(record => record.Kind == "HandleClosed" && KindOf(record.Resource) == kind).Utc,
        })).Order()];
        long offset = anchors[anchors.Length / 2];

        // Each wait whose first look failed, the span its waiter's log bounds it in, and the signal of the other process's
        // that ended it: the opener's waits end by the creator's signals of their round; the creator's on the semaphore by
        // the opener's of its round, and on the mutex by the opener's release in the round before.
        var waits = new List<Wait>();
        foreach ((string waiter, string signaler) in new[] { ("opener", "creator"), ("creator", "opener") })
        {
            Truth[] log = logs[waiter];
            for (int index = 1; index < log.Length; index++)
            {
                if (log[index] is not { Kind: "WaitEnded", Status: "blocked", Round: { } round } wait)
                {
                    continue;
                }

                string kind = KindOf(wait.Resource)!;
                long signalRound = waiter == "creator" && kind == "mutex" ? round - 1 : round;
                Truth? signal = logs[signaler].FirstOrDefault(record => record.Kind == "ObjectSignaled" && record.Resource == wait.Resource && record.Round == signalRound);
                if (signal is not null)
                {
                    waits.Add(new(waiter, signaler, kind, round, signal.Utc, log[index - 1].Utc, wait.Utc));
                }
            }
        }

        // Each wait pairs with the ready nearest its signal's record, from the signaler to the waiter, not from a DPC, inside
        // its span on the kernel's clock moved by the offset; a ready pairs once.
        Row[] readies = [.. rows.Where(row => row is { Record: "Ready", FromDpc: "False" })];
        var taken = new Dictionary<Row, Wait>();
        var distances = new List<long>();
        foreach (Wait wait in waits.OrderBy(wait => wait.SignalUtc))
        {
            Row? ready = readies
                .Where(row => !taken.ContainsKey(row) && row.Role == wait.Signaler && row.AwakenedRole == wait.Waiter
                    && row.Utc - offset >= wait.BlockedAfterUtc - Slack && row.Utc - offset <= wait.EndedUtc + Slack)
                .MinBy(row => Math.Abs(row.Utc - offset - wait.SignalUtc));
            if (ready is not null)
            {
                taken[ready] = wait;
                distances.Add(ready.Utc - offset - wait.SignalUtc);
            }
        }

        // A wait the kernel blocked switched its waiting thread - the thread its paired readies readied - out in a user-mode
        // wait inside its span; every one has its ready, each within microseconds of its signal's record.
        Dictionary<string, string> threads = taken.GroupBy(pair => pair.Value.Waiter)
            .ToDictionary(group => group.Key, group => group.GroupBy(pair => pair.Key.Thread).MaxBy(thread => thread.Count())!.Key);
        Wait[] blocked = [.. waits.Where(wait => rows.Any(row => row.Record == "Waited" && row.Thread == threads[wait.Waiter]
            && row.Utc - offset >= wait.BlockedAfterUtc - Slack && row.Utc - offset <= wait.EndedUtc + Slack))];
        Assert.All(blocked, wait => Assert.Contains(wait, taken.Values));
        Assert.All(distances, distance => Assert.InRange(distance, -TimeSpan.FromMilliseconds(0.05).Ticks, TimeSpan.FromMilliseconds(0.05).Ticks));

        // What the first run measured, reproduced; and no ready of a thread in a user-mode wait between the two processes
        // is left unpaired: the rest readied threads in the kernel's own waits, or in none seen.
        using JsonDocument measurement = Measurement();
        JsonElement first = measurement.RootElement.GetProperty("runs")[0];
        Assert.Equal(first.GetProperty("waitsTheKernelBlocked").GetInt32(), blocked.Length);
        Assert.Equal(first.GetProperty("waitsTheKernelBlockedWithTheirReady").GetInt32(), blocked.Count(taken.ContainsValue));
        Assert.Equal(first.GetProperty("waitsPaired").GetInt32(), taken.Count);
        Assert.Equal(first.GetProperty("truthWaitsWhoseFirstLookFailed").GetInt32(), waits.Count);
        Assert.DoesNotContain(readies, row => !taken.ContainsKey(row) && row.AwakenedWait == "UserRequest");
        Assert.All(taken.Keys, row => Assert.Contains(row.AwakenedWait, PairedWaits));
    }

    [Fact(DisplayName = "P27: named synchronization objects have an inventoried lead - the object manager's handle group, enabled by a group mask and no kernel flag, requested by no profile and admitting no event, its measured notes and Low cost stated beside its throughput regression - and no tier on any build")]
    public void NamedObjectsHaveALeadAndNoTier()
    {
        WindowsSourceDefinition handles = WindowsSourceCatalog.Find(WindowsSourceCatalog.KernelObjectHandlesSourceId)!;
        Assert.Equal((SourceKind.KernelFlagGroup, 0UL, WindowsSourceCatalog.ObjectManagerEventClass),
            (handles.Kind, handles.KernelFlags, handles.ClassicEventClass));
        Assert.Equal([WindowsSourceCatalog.ObjectHandlesGroupMask], handles.KernelGroupMasks);
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != handles.SourceId), source => Assert.Empty(source.KernelGroupMasks));
        Assert.Equal([Mechanism.Synchronization], handles.Mechanisms);
        Assert.Equal((SourceContractStatus.Experimental, false), (handles.ContractStatus, handles.SupportsCaptureSideProcessFilter));
        Assert.Empty(handles.AdmittedEvents);
        Assert.Contains("PERF_OB_HANDLE", handles.FilteringNotes, StringComparison.Ordinal);

        // Its cost is measured, Low in processor time, with the throughput regression its notes state beside the class.
        Assert.Equal(OverheadClass.Low, handles.Overhead);
        using (JsonDocument impact = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), handles.OverheadEvidence!))))
        {
            Assert.Equal((handles.Overhead.ToString(), false), (impact.RootElement.GetProperty("overhead").GetString(),
                impact.RootElement.GetProperty("throughputTargetMet").GetBoolean()));
        }

        Assert.Contains(handles.Notes, note => note.Contains("past the 5% target", StringComparison.Ordinal) && note.Contains("never Explore", StringComparison.Ordinal));
        Assert.Contains(handles.Notes, note => note.Contains("FX-SYNC-001", StringComparison.Ordinal) && note.Contains("named at its close", StringComparison.Ordinal));
        Assert.Contains(handles.Notes, note => note.Contains("never a signal by itself", StringComparison.Ordinal));

        // No profile requests it, no other source claims named synchronization objects, and no build carries a tier for them.
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures),
            profile => profile.Sources.Any(source => source.SourceId == handles.SourceId));
        Assert.All(WindowsSourceCatalog.All.Where(source => source.SourceId != handles.SourceId),
            source => Assert.DoesNotContain(Mechanism.Synchronization, source.Mechanisms));
        Assert.DoesNotContain(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.Synchronization);
    }

    [Fact(DisplayName = "§9.2: a capture whose plan holds the handle group alone started its private system logger with the process flag, set the group's mask by its handle, and was delivered the object manager's records through the product's session, none admitted, nothing lost and no session left")]
    public void ACaptureSetsTheHandleGroupsMaskOnItsLogger()
    {
        using JsonDocument check = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "sync-objects-session-check-*")),
            "report.json")));
        JsonElement root = check.RootElement;

        // The plan the product compiles for the group: the process flag to start the logger, the handle group's mask.
        Assert.True(root.GetProperty("started").GetBoolean());
        Assert.Equal("0x1", root.GetProperty("planKernelFlags").GetString());
        Assert.Equal(["0x80000040"], root.GetProperty("planGroupMasks").EnumerateArray().Select(mask => mask.GetString()));

        // The session held the mask while it recorded - the fifth word, the object manager's handle bit - beside its flags.
        string[] masks = [.. root.GetProperty("masksReadWhileRecording").EnumerateArray().Select(mask => mask.GetString()!)];
        Assert.Equal(8, masks.Length);
        Assert.Equal("0x00000040", masks[4]);
        Assert.Equal(1u, uint.Parse(masks[0][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 1);

        // It was delivered the object manager's handle creations, closes and rundowns, none admitted as none is planned, and
        // stopped its own session through the stop, losing nothing and leaving none behind.
        JsonElement opcodes = root.GetProperty("objectManagerByOpcode");
        Assert.All(HandleOpcodes, opcode => Assert.InRange(opcodes.GetProperty(opcode).GetInt64(), 1, long.MaxValue));
        Assert.Equal(0, root.GetProperty("admitted").GetInt64());
        Assert.Equal(root.GetProperty("deliveredByClass").GetProperty("Kernel object manager handles (PERF_OB_HANDLE)").GetInt64(),
            root.GetProperty("omittedNotAdmitted").GetInt64());
        Assert.Equal((true, true, true, 0L, 0L), (root.GetProperty("lossFree").GetBoolean(), root.GetProperty("providersStopped").GetBoolean(),
            root.GetProperty("deliveredThroughStop").GetBoolean(), root.GetProperty("eventsLost").GetInt64(), root.GetProperty("buffersLost").GetInt64()));
        Assert.False(root.GetProperty("sessionLeftBehind").GetBoolean());
    }

    [Fact(DisplayName = "§4.2: the object type table read from user mode before a capture names every type number its handle records carry as the type rundown at its stop does, so a capture can tell an event's, a mutant's or a semaphore's handle as it arrives - about one handle record in thirty")]
    public void ACaptureCanKnowAHandlesTypeAsItStarts()
    {
        using JsonDocument types = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "sync-objects-types-*")),
            "report.json")));
        JsonElement root = types.RootElement;

        // The table before and after the session alike, and the rundown naming every number the table does, but the type
        // of types, which no handle record carries.
        Assert.Equal(root.GetProperty("typesBefore").GetInt32(), root.GetProperty("typesAfter").GetInt32());
        Assert.Equal(root.GetProperty("typesBefore").GetInt32() - 1, root.GetProperty("typesInRundown").GetInt32());
        string disagreement = Assert.Single(root.GetProperty("disagreements").EnumerateArray()).GetString()!;
        Assert.Equal("2: before Type, rundown none, after Type", disagreement);

        // The synchronization types by the numbers FX-SYNC-001's records carried, and their share of the machine's handles.
        JsonElement synchronization = root.GetProperty("synchronizationTypes");
        Assert.Equal((21, 22, 24), (synchronization.GetProperty("Event").GetInt32(), synchronization.GetProperty("Mutant").GetInt32(),
            synchronization.GetProperty("Semaphore").GetInt32()));
        double share = (double)root.GetProperty("createsOfSynchronizationTypes").GetInt64() / root.GetProperty("creates").GetInt64();
        Assert.InRange(share, 0.01, 0.1);
        Assert.InRange(root.GetProperty("namedClosesOfSynchronizationTypes").GetInt64(), 1, root.GetProperty("closesOfSynchronizationTypes").GetInt64());
        Assert.Equal(0, root.GetProperty("eventsLost").GetInt64());
    }

    [Fact(DisplayName = "§12: collecting the handle group is measured in two series of pairs over a handle-heavy workload - Low in processor time, the workload slower past §12's throughput target - and the object group beside it Moderate, millions of records a second, so only an opt-in profile stating it may admit either")]
    public void TheGroupsCollectionCostIsMeasured()
    {
        string results = Path.Combine(RepositoryRoot(), "bench", "results");
        string[] handles = [.. Directory.EnumerateDirectories(results, "sync-objects-impact-handles-*")
            .Select(directory => Path.Combine(directory, "impact.json")).Where(File.Exists).Order(StringComparer.Ordinal)];
        Assert.Equal(2, handles.Length);
        Assert.All(handles, file =>
        {
            // Nine pairs of FX-SYNC-001's opener churning handles, nothing lost: processor time is Low, and the workload is
            // slower than §12's 5% target in its median pair.
            using JsonDocument impact = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = impact.RootElement;
            Assert.Equal((9, 0L), (root.GetProperty("pairs").GetArrayLength(), root.GetProperty("eventsLost").GetInt64()));
            Assert.Equal(["PERF_OB_HANDLE"], root.GetProperty("groups").EnumerateArray().Select(group => group.GetString()));
            Assert.Equal(nameof(OverheadClass.Low), root.GetProperty("overhead").GetString());
            Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(root.GetProperty("medianCaptureImpactPercentagePoints").GetDouble()));
            Assert.True(root.GetProperty("medianThroughputRegressionPercent").GetDouble() > 5, file);
            Assert.False(root.GetProperty("throughputTargetMet").GetBoolean());
            Assert.Contains("--churn", root.GetProperty("workload").GetString(), StringComparison.Ordinal);
        });

        // The object group beside it: its references come with it, millions of records a second, Moderate in processor time
        // and the workload a fifth slower and more.
        using JsonDocument objects = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.EnumerateDirectories(results, "sync-objects-impact-objects-*")), "impact.json")));
        JsonElement both = objects.RootElement;
        Assert.Equal((9, 0L), (both.GetProperty("pairs").GetArrayLength(), both.GetProperty("eventsLost").GetInt64()));
        Assert.Equal(["PERF_OB_HANDLE", "PERF_OB_OBJECT"], both.GetProperty("groups").EnumerateArray().Select(group => group.GetString()));
        Assert.Equal(nameof(OverheadClass.Moderate), both.GetProperty("overhead").GetString());
        Assert.InRange(both.GetProperty("medianThroughputRegressionPercent").GetDouble(), 20, double.MaxValue);
        Assert.InRange(both.GetProperty("medianObjectManagerEventsPerSecond").GetDouble(), 1_000_000, double.MaxValue);
    }

    private static string? KindOf(string? resource) => Kinds.FirstOrDefault(kind => resource?.EndsWith("-" + kind, StringComparison.Ordinal) == true);

    private static Row[] Ledger(string file) =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), file)).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(','))
            .Select(field => new Row(long.Parse(field[0], CultureInfo.InvariantCulture), field[1], field[2], field[3], field[4], field[5],
                field[6], field[7], field[8], field[9], field[10], field[11], field[12], field[13])),
    ];

    private static Truth[] TruthLog(string file) =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), file)).Where(line => line.Length > 0).Select(line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            JsonElement root = record.RootElement;
            return new Truth(
                root.GetProperty("kind").GetString()!,
                root.TryGetProperty("callId", out JsonElement call) && call.ValueKind == JsonValueKind.Number ? call.GetInt64() : null,
                root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
                root.TryGetProperty("resourceName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                DateTimeOffset.Parse(root.GetProperty("recordedUtc").GetString()!, CultureInfo.InvariantCulture).UtcTicks);
        }),
    ];

    private static JsonDocument Measurement() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));

    private static string Evidence() => Path.Combine(RepositoryRoot(), "fixtures", "FX-SYNC-001", "evidence");

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
