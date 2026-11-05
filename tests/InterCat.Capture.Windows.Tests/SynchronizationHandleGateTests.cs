using System.Globalization;
using Xunit;
using static InterCat.Capture.Windows.SynchronizationHandleGate;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The synchronization handle gate (ADR-083): it follows an event's, a mutant's or a semaphore's object from the first handle
/// a capture sees made at its address to the close of the last one it saw made, numbers it in the capture, and numbers a
/// handle made before the capture alone, as nothing joins it to another record at its address.
/// </summary>
public sealed class SynchronizationHandleGateTests
{
    private const int Event = 21;
    private const int Mutant = 22;
    private const int Semaphore = 24;
    private const int File = 37;
    private const int SystemProcess = 4;
    private static readonly int[] Synchronization = [Event, Mutant, Semaphore];

    [Fact(DisplayName = "R22: a synchronization object's handles are one object from the first the capture sees made at its address to the close of the last it saw made, and the next handle made there begins another")]
    public void AnObjectEndsWithTheLastHandleSeenMade()
    {
        var gate = new SynchronizationHandleGate(Synchronization);
        SynchronizationObjectIncarnation made = Admitted(gate.Read(new(Create, 0x1000, 0x10, Event, 100)));
        Assert.Equal(new(1, HandleFirstSeen.Creation), made);

        // Another process opens it by name, then each closes its handle: one object throughout, followed until the last. A
        // close at its address of a handle the gate never saw made is its own, and ends nothing.
        Assert.Equal(made, Admitted(gate.Read(new(Create, 0x1000, 0x24, Event, 200))));
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x1000, 0x30, Event, 300))));
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x1000, 0x10, Event, 100))));
        Assert.Equal(1, gate.Following);
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x1000, 0x24, Event, 200))));
        Assert.Equal(0, gate.Following);

        // The kernel reuses the address once the object is gone: the next handle made there is another object's.
        Assert.Equal(new(2, HandleFirstSeen.Creation), Admitted(gate.Read(new(Create, 0x1000, 0x10, Mutant, 100))));
    }

    [Fact(DisplayName = "R22: a close or the stop's rundown of a handle made before the capture is numbered alone, never joined to another record at its address")]
    public void AHandleMadeBeforeTheCaptureIsNumberedAlone()
    {
        var gate = new SynchronizationHandleGate(Synchronization);

        // Two closes at one address of handles made before the capture: their other handles unseen, two numbers.
        Assert.Equal(new(1, HandleFirstSeen.Close), Admitted(gate.Read(new(Close, 0x2000, 0x30, Semaphore, 100))));
        Assert.Equal(new(2, HandleFirstSeen.Close), Admitted(gate.Read(new(Close, 0x2000, 0x44, Semaphore, 200))));
        Assert.Equal(0, gate.Following);

        // At the stop: a followed object's open handle is its own, and one made before the capture is numbered alone.
        SynchronizationObjectIncarnation held = Admitted(gate.Read(new(Create, 0x3000, 0x50, Mutant, 100)));
        Assert.Equal(held, Admitted(gate.Read(new(Rundown, 0x3000, 0x50, Mutant, 100))));
        Assert.Equal(new(4, HandleFirstSeen.Rundown), Admitted(gate.Read(new(Rundown, 0x4000, 0x60, Mutant, 300))));
        Assert.Equal(1, gate.Following);
    }

    [Fact(DisplayName = "R22: a duplication makes its handle in its target process, which holds the object until it closes it")]
    public void ADuplicationMakesAHandleInItsTarget()
    {
        var gate = new SynchronizationHandleGate(Synchronization);
        SynchronizationObjectIncarnation made = Admitted(gate.Read(new(Create, 0x5000, 0x10, Event, 100)));
        Assert.Equal(made, Admitted(gate.Read(new(Duplicate, 0x5000, 0x10, Event, 100, TargetProcess: 200, TargetHandle: 0x88))));

        // The source closes its handle; the target's still holds the object, and its close ends it.
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x5000, 0x10, Event, 100))));
        Assert.Equal(1, gate.Following);
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x5000, 0x88, Event, 200))));
        Assert.Equal(0, gate.Following);

        // A duplication at an address no followed object holds begins following it, from the handle it made.
        Assert.Equal(new(2, HandleFirstSeen.Duplication),
            Admitted(gate.Read(new(Duplicate, 0x6000, 0x14, Semaphore, 100, TargetProcess: 300, TargetHandle: 0x90))));
        Assert.Equal(1, gate.Following);
    }

    [Fact(DisplayName = "R22: the gate follows only the synchronization types, by the numbers measured for the capture's build, and holds no more handles than its bound, so follows no more objects")]
    public void TheGateFollowsOnlyItsTypesWithinItsBound()
    {
        // Another type's handles are never followed, whatever their address.
        var gate = new SynchronizationHandleGate(Synchronization, capacity: 1);
        Assert.Equal((HandleGateOutcome.OtherType, (SynchronizationObjectIncarnation?)null), gate.Read(new(Create, 0x7000, 0x10, File, 100)));
        Assert.Equal((HandleGateOutcome.OtherType, (SynchronizationObjectIncarnation?)null), gate.Read(new(Close, 0x7000, 0x10, File, 100)));

        // Beyond its bound a handle that would begin an object is not followed; one of a followed object is still its
        // object's, but not held.
        SynchronizationObjectIncarnation made = Admitted(gate.Read(new(Create, 0x8000, 0x10, Event, 100)));
        Assert.Equal((HandleGateOutcome.Untracked, (SynchronizationObjectIncarnation?)null), gate.Read(new(Create, 0x9000, 0x10, Event, 100)));
        Assert.Equal(made, Admitted(gate.Read(new(Create, 0x8000, 0x14, Event, 200))));
        Assert.Equal((1, 1), (gate.Following, gate.Holding));

        // So the close of the handle it holds ends the object, and the other's close, met at an address it no longer
        // follows, is numbered alone: two numbers for one object, never one for two.
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0x8000, 0x10, Event, 100))));
        Assert.Equal((0, 0), (gate.Following, gate.Holding));
        Assert.Equal(new(2, HandleFirstSeen.Close), Admitted(gate.Read(new(Close, 0x8000, 0x14, Event, 200))));

        // A type table - a session's rundown - names them by their names, whatever their numbers there.
        Assert.Equal([Event, Mutant, Semaphore], TypesIn(new Dictionary<int, string>
        {
            [2] = "Type", [Event] = "Event", [Mutant] = "Mutant", [Semaphore] = "Semaphore", [File] = "File", [23] = "Section",
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Read(new(48, 0x8000, 0, Event, 100)));
    }

    [Fact(DisplayName = "R22: the gate follows no kernel handle - its creation, close or rundown, or a duplication into the system process's table - and a duplication from one makes its target a handle of its own")]
    public void TheGateFollowsNoKernelHandle()
    {
        // A kernel handle, its value's top bit set, made and closed in processes the kernel ran for: never followed, and
        // never ending or beginning an object at its address.
        var gate = new SynchronizationHandleGate(Synchronization);
        (HandleGateOutcome, SynchronizationObjectIncarnation?) kernel = (HandleGateOutcome.KernelHandle, null);
        Assert.Equal(kernel, gate.Read(new(Create, 0xA000, 0x8000_0010, Event, 100)));
        Assert.Equal(0, gate.Following);
        SynchronizationObjectIncarnation made = Admitted(gate.Read(new(Create, 0xA000, 0x10, Event, 100)));
        Assert.Equal(kernel, gate.Read(new(Close, 0xA000, 0x8000_0010, Event, 200)));
        Assert.Equal(kernel, gate.Read(new(Rundown, 0xA000, 0x8000_0010, Event, SystemProcess)));
        Assert.Equal(kernel, gate.Read(new(Duplicate, 0xA000, 0x10, Event, 100, TargetProcess: SystemProcess, TargetHandle: 0x8000_0020)));
        Assert.Equal((1, 1), (gate.Following, gate.Holding));

        // A duplication from a kernel handle into a process makes that process a handle the gate holds, of the object.
        Assert.Equal(made, Admitted(gate.Read(new(Duplicate, 0xA000, 0x8000_0010, Event, 300, TargetProcess: 300, TargetHandle: 0x44))));
        Assert.Equal((1, 2), (gate.Following, gate.Holding));
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0xA000, 0x10, Event, 100))));
        Assert.Equal(made, Admitted(gate.Read(new(Close, 0xA000, 0x44, Event, 300))));
        Assert.Equal((0, 0), (gate.Following, gate.Holding));
    }

    [Fact(DisplayName = "P12: the synchronization handle gate, which reads its records on the capture's callback, allocates nothing per record once its tables hold what it followed at once")]
    public void TheGateAllocatesNothingPerRecord()
    {
        // A thousand objects begin - each created, opened by another process and duplicated into a third, beside another
        // type's handles - and end, closed by all three. Read again once the gate's tables have grown to what it held at
        // once, the same records allocate nothing, though every object begins and ends again.
        ulong[] addresses = [.. Enumerable.Range(0, 1_024).Select(index => 0x10_0000 + ((ulong)index * 0x40))];
        ObjectHandleRecord[] records =
        [
            .. addresses.SelectMany(address => new ObjectHandleRecord[]
            {
                new(Create, address, 0x10, Event, 100),
                new(Create, address, 0x24, Event, 200),
                new(Duplicate, address, 0x24, Event, 200, TargetProcess: 300, TargetHandle: 0x88),
                new(Create, address + 8, 0x30, File, 100),
            }),
            .. addresses.SelectMany(address => new ObjectHandleRecord[]
            {
                new(Close, address, 0x10, Event, 100),
                new(Close, address, 0x24, Event, 200),
                new(Close, address, 0x88, Event, 300),
                new(Close, address + 8, 0x30, File, 100),
            }),
        ];
        var gate = new SynchronizationHandleGate(Synchronization);
        Assert.Equal(0, Read(gate, records));
        Assert.Equal((0, 0), (gate.Following, gate.Holding));

        long before = GC.GetAllocatedBytesForCurrentThread();
        int untracked = Read(gate, records);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((0, 0L), (untracked, allocated));
        Assert.Equal((0, 0), (gate.Following, gate.Holding));

        // How many records the gate did not follow, counted without an assertion's allocations among the gate's.
        static int Read(SynchronizationHandleGate gate, ObjectHandleRecord[] records)
        {
            int untracked = 0;
            foreach (ObjectHandleRecord record in records)
            {
                if (gate.Read(in record).Outcome == HandleGateOutcome.Untracked)
                {
                    untracked++;
                }
            }

            return untracked;
        }
    }

    [Fact(DisplayName = "R22: FX-SYNC-001's handle records, read through the gate as delivered, are three objects - each created by the creator, opened by the opener and closed by both - numbered apart")]
    public void FxSync001sHandlesAreThreeObjects()
    {
        string ledger = Path.Combine(RepositoryRoot(), "fixtures", "FX-SYNC-001", "evidence", "ledger.csv");
        string[][] rows = [.. System.IO.File.ReadLines(ledger).Skip(1).Select(line => line.Split(','))
            .Where(field => field[1] is "Create" or "Close")];
        var gate = new SynchronizationHandleGate(Synchronization);
        var objects = new Dictionary<string, HashSet<SynchronizationObjectIncarnation>>(StringComparer.Ordinal);
        foreach (string[] field in rows)
        {
            // The ledger's ordinals stand for the addresses and handle values it never states, and its roles for the processes.
            var record = new ObjectHandleRecord(
                field[1] == "Create" ? Create : Close,
                ulong.Parse(field[4], CultureInfo.InvariantCulture) * 0x1000,
                uint.Parse(field[5], CultureInfo.InvariantCulture),
                int.Parse(field[6], CultureInfo.InvariantCulture),
                field[2] == "creator" ? 1 : 2);
            SynchronizationObjectIncarnation incarnation = Admitted(gate.Read(record));
            objects.TryAdd(field[3], []);
            objects[field[3]].Add(incarnation);
        }

        Assert.Equal(12, rows.Length);
        Assert.Equal(["event", "mutex", "semaphore"], objects.Keys.Order(StringComparer.Ordinal));
        Assert.All(objects.Values, incarnations => Assert.Equal(HandleFirstSeen.Creation, Assert.Single(incarnations).FirstSeen));
        Assert.Equal(3, objects.Values.Select(incarnations => incarnations.Single().Number).Distinct().Count());
        Assert.Equal(0, gate.Following);
    }

    [Fact(DisplayName = "§4.2: the gate's synchronization types for this build are the numbers the probe's object type table and the session's rundown named them, and a build none measured has no gate")]
    public void TheGatesTypesAreTheBuildsMeasuredNumbers()
    {
        // The probe read the table before a session and set it against the rundown at its stop; the product never reads it.
        string report = Path.Combine(Assert.Single(Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "bench", "results"),
            "sync-objects-types-*")), "report.json");
        using var types = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(report));
        System.Text.Json.JsonElement measured = types.RootElement.GetProperty("synchronizationTypes");
        Assert.Equal(SynchronizationTypeNames.Select(name => measured.GetProperty(name).GetInt32()), MeasuredTypes("10.0.26220.0-x64"));
        Assert.Null(MeasuredTypes("10.0.19045.0-x64"));
    }

    private static SynchronizationObjectIncarnation Admitted((HandleGateOutcome Outcome, SynchronizationObjectIncarnation? Incarnation) read)
    {
        Assert.Equal(HandleGateOutcome.Admitted, read.Outcome);
        return read.Incarnation!.Value;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !System.IO.File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
