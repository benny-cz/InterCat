using System.Globalization;
using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// FX-SYNC-002 (ADR-082's fifth addendum): FX-SYNC-001's two processes with the opener also given a handle of each object by
/// a duplication - the creator pushing its event's, the opener pulling the semaphore's, the coordinator duplicating the
/// mutex's between the two - measured by tools/InterCat.SyncProbe --duplication under a private system logger of its own.
/// A duplication's record names the process it made a handle in and that handle, and is raised in the process that asked
/// for it; at version 3, the only version this build raises it at, its fourth field names the process it came from, which
/// no layout the machine registers describes. A kernel handle's records are raised wherever the kernel ran, and only the
/// stop's rundown names its holder.
/// </summary>
public sealed class SyncObjectDuplicationTests
{
    // The run's processes that logged a truth, and a handle's records other than a duplication's and the stop's rundown.
    private static readonly string[] Roles = ["creator", "opener", "coordinator"];
    private static readonly string[] Made = ["creations", "closes"];

    [Fact(DisplayName = "R22: FX-SYNC-002's duplications are each raised in the process that asked for it - a push in its source, a pull in its target, one between two others in a third - at version 3, its fourth field naming its source process, with its source's handle and its target, and no creation of the handle made, whose close its target raises naming the object")]
    public void ADuplicationIsRaisedInTheProcessThatAskedForIt()
    {
        Row[] rows = Ledger();
        (string Role, string Kind, JsonElement Ends)[] duplications = [.. Roles
            .SelectMany(role => TruthLog($"truth-{role}.jsonl")).Where(record => record.GetProperty("kind").GetString() == "HandleDuplicated")
            .Select(record => (record.GetProperty("role").GetString()!, record.GetProperty("status").GetString()!, record.GetProperty("duplication").Clone()))];

        // The truth: three duplications, each from one of the creator's handles into the opener, asked for by each process.
        Assert.Equal([("coordinator", "mutex"), ("creator", "event"), ("opener", "semaphore")],
            duplications.Select(made => (made.Role, made.Kind)).Order());
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "scenario.json")));
        int creator = scenario.RootElement.GetProperty("creatorProcessId").GetInt32();
        int opener = scenario.RootElement.GetProperty("openerProcessId").GetInt32();
        Assert.All(duplications, made => Assert.Equal((creator, opener),
            (made.Ends.GetProperty("sourceProcessId").GetInt32(), made.Ends.GetProperty("targetProcessId").GetInt32())));

        foreach ((string role, string kind, _) in duplications)
        {
            // Its one record is raised in the process that asked for it, whichever end that is, or neither; it names the
            // handle it made in its target, and the handle it started from by its value, the creator's.
            Row duplicated = Assert.Single(rows, row => row is { Record: "Duplicate" } && row.Kind == kind);
            Assert.Equal((role, "opener"), (duplicated.Role, duplicated.TargetRole));

            // At version 3, whose fourth field - after the type, where no layout the machine registers reads - names the
            // process it was duplicated from, whichever process asked.
            Assert.Equal(("3", "creator"), (duplicated.Version, duplicated.SourceRole));
            Row created = Assert.Single(rows, row => row is { Record: "Create", Role: "creator" } && row.Kind == kind);
            Assert.Equal(created.Handle, duplicated.Handle);

            // No creation of the handle it made is raised anywhere; its close is raised in its target and names the object.
            Assert.DoesNotContain(rows, row => row.Record == "Create" && row.Handle == duplicated.TargetHandle);
            Row closed = Assert.Single(rows, row => row.Record == "Close" && row.Handle == duplicated.TargetHandle);
            Assert.Equal(("opener", "True"), (closed.Role, closed.NameCarried));
        }

        // Every run measured the same, nothing lost.
        using JsonDocument measurement = Measurement();
        Assert.All(measurement.RootElement.GetProperty("runs").EnumerateArray(), run =>
        {
            Assert.Equal(0, run.GetProperty("eventsLost").GetInt32());
            Assert.Equal(["version 3, 26 bytes"],
                run.GetProperty("machineDuplications").GetProperty("ofEveryTypeByShape").EnumerateObject().Select(shape => shape.Name));
            Assert.All(run.GetProperty("duplications").EnumerateArray(), made =>
            {
                Assert.Equal(1, made.GetProperty("records").GetInt32());
                Assert.Equal(("3", "26", "the source process"), (Assert.Single(made.GetProperty("headerVersions").EnumerateObject()).Name,
                    Assert.Single(made.GetProperty("payloadBytes").EnumerateObject()).Name,
                    Assert.Single(made.GetProperty("valueAfterType").EnumerateObject()).Name));
                Assert.Equal(made.GetProperty("askedBy").GetString(), Assert.Single(made.GetProperty("raisedIn").EnumerateObject()).Name);
                Assert.True(made.GetProperty("sourceHandleCarried").GetBoolean());
                Assert.Empty(made.GetProperty("creationsOfTheHandleMade").EnumerateObject());
                Assert.Equal(["opener"], made.GetProperty("closesOfTheHandleMade").EnumerateObject().Select(closed => closed.Name));
            });
        });
    }

    [Fact(DisplayName = "R22: a kernel handle's records are raised wherever the kernel ran, never in its holder - FX-SYNC-002's synchronization kernel handles created and closed outside the system process in every run, and the stop's rundown naming it the holder of each")]
    public void AKernelHandlesRecordsNameNoHolder()
    {
        using JsonDocument measurement = Measurement();
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(4, runs.Length);
        Assert.All(runs, run =>
        {
            JsonElement kernel = run.GetProperty("kernelHandles");

            // Created and closed in other processes' contexts, never the system process's, which holds them.
            foreach (string records in Made)
            {
                JsonElement counts = kernel.GetProperty(records);
                Assert.Equal(0, counts.GetProperty("kernelRaisedInSystem").GetInt32());
                Assert.InRange(counts.GetProperty("kernelRaisedElsewhere").GetInt32(), 1, int.MaxValue);
            }

            // A duplication that made a kernel handle made it in the system process's table, raised in another's context.
            JsonElement duplicated = kernel.GetProperty("duplications");
            Assert.Equal(duplicated.GetProperty("kernelHandles").GetInt32(), duplicated.GetProperty("kernelHeldBySystem").GetInt32());
            Assert.Equal(0, duplicated.GetProperty("kernelRaisedInSystem").GetInt32());

            // The stop's rundown names the system process the holder of every kernel handle, raising its records elsewhere.
            JsonElement rundown = kernel.GetProperty("rundown");
            Assert.InRange(rundown.GetProperty("kernelHandles").GetInt32(), 1, int.MaxValue);
            Assert.Equal(rundown.GetProperty("kernelHandles").GetInt32(), rundown.GetProperty("kernelHeldBySystem").GetInt32());
            Assert.Equal(0, rundown.GetProperty("kernelRaisedInSystem").GetInt32());
        });

        // Wherever the kernel ran: across the runs, each kind in several processes' contexts, not one process's alone.
        Assert.All(Made, records => Assert.InRange(runs.Max(run =>
            run.GetProperty("kernelHandles").GetProperty(records).GetProperty("kernelRaisedInProcesses").GetInt32()), 2, int.MaxValue));

        // Each run's report is the bench result the measurement names.
        Assert.All(runs, run => Assert.True(File.Exists(Path.Combine(RepositoryRoot(), run.GetProperty("report").GetString()!))));
    }

    private static Row[] Ledger() =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), "ledger.csv")).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(','))
            .Select(field => new Row(long.Parse(field[0], CultureInfo.InvariantCulture), field[1], field[2], field[3], field[4], field[5],
                field[6], field[7], field[8], field[9], field[10], field[11], field[12])),
    ];

    private static JsonElement[] TruthLog(string file) =>
    [
        .. File.ReadLines(Path.Combine(Evidence(), file)).Where(line => line.Length > 0).Select(line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            return record.RootElement.Clone();
        }),
    ];

    private static JsonDocument Measurement() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "measurement.json")));

    private static string Evidence() => Path.Combine(RepositoryRoot(), "fixtures", "FX-SYNC-002", "evidence");

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    /// <summary>
    /// One ledger row: a record of the run's three objects by role and kind, its handles by ordinal, its header's version, and
    /// a duplication's source by the role of the process its fourth field names.
    /// </summary>
    private sealed record Row(long UtcTicks, string Record, string Role, string Kind, string Object, string Handle, string TargetRole,
        string TargetHandle, string Type, string TypeName, string NameCarried, string Version, string SourceRole);
}
