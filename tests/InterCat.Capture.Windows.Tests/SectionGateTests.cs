using System.Globalization;
using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The section gates (ADR-090): a section followed by its handles from the first a capture sees made at its address to the
/// close of the last, and a view followed by its process and base from its mapping to its unmapping, its section by the value
/// every view of one section carries, from the first followed view to the last unmapped - never an address kept.
/// </summary>
public sealed class SectionGateTests
{
    private const int Section = 49;
    private const int FileType = 37;

    [Fact(DisplayName = "R22: the section handle gate follows a section from the first handle a capture sees made at its address to the close of the last - both processes' handles one number, the address's next section another - and a handle made before the capture, met at its close, is numbered alone; another type's handle is never followed")]
    public void TheHandleGateFollowsASectionFromItsFirstHandleToItsLastClose()
    {
        var gate = new SectionHandleGate([Section]);
        ObjectIncarnation first = Admit(gate, new(ObjectHandleGate.Create, 0x1000, 4, Section, 400));
        Assert.Equal(first, Admit(gate, new(ObjectHandleGate.Create, 0x1000, 8, Section, 500)));
        Assert.Equal(first, Admit(gate, new(ObjectHandleGate.Close, 0x1000, 4, Section, 400)));
        Assert.Equal(first, Admit(gate, new(ObjectHandleGate.Close, 0x1000, 8, Section, 500)));
        Assert.Equal((0, 0, HandleFirstSeen.Creation), (gate.Following, gate.Holding, first.FirstSeen));

        // The address's next section is another, though both were one name's.
        ObjectIncarnation next = Admit(gate, new(ObjectHandleGate.Create, 0x1000, 4, Section, 400));
        Assert.NotEqual(first.Number, next.Number);

        // Two handles made before the capture, closed at one address: each numbered alone, never joined by the address.
        ObjectIncarnation before = Admit(gate, new(ObjectHandleGate.Close, 0x2000, 12, Section, 600));
        ObjectIncarnation another = Admit(gate, new(ObjectHandleGate.Close, 0x2000, 16, Section, 700));
        Assert.Equal((HandleFirstSeen.Close, HandleFirstSeen.Close), (before.FirstSeen, another.FirstSeen));
        Assert.NotEqual(before.Number, another.Number);

        // A file's handle, and a kernel handle to a section, are never followed.
        Assert.Equal(HandleGateOutcome.OtherType, gate.Read(new(ObjectHandleGate.Create, 0x3000, 4, FileType, 400)).Outcome);
        Assert.Equal(HandleGateOutcome.KernelHandle, gate.Read(new(ObjectHandleGate.Create, 0x4000, 0x8000_0004, Section, 4)).Outcome);
    }

    [Fact(DisplayName = "§4.2: the section handle gate carries the Section type's number measured for the build - 49, as the object type table and the session's type rundown both named it - finds it in a capture's type rundown, and a build none measured has no number")]
    public void TheHandleGateCarriesTheMeasuredNumber()
    {
        using JsonDocument types = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Assert.Single(Directory.GetDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "section-types-*")), "report.json")));
        string build = types.RootElement.GetProperty("build").GetString()!;
        Assert.Equal([types.RootElement.GetProperty("sectionType").GetProperty("rundown").GetInt32()], SectionHandleGate.MeasuredTypes(build));
        Assert.Null(SectionHandleGate.MeasuredTypes("10.0.19045.0-x64"));
        Assert.Equal([49], SectionHandleGate.TypesIn(new Dictionary<int, string> { [21] = "Event", [37] = "File", [49] = "Section" }));
        Assert.Empty(SectionHandleGate.TypesIn(new Dictionary<int, string> { [21] = "Event" }));
    }

    [Fact(DisplayName = "R22: the section view gate follows a view by its process and base from its mapping to its unmapping, and a section by the value its views carry from the first followed view to the last unmapped - two processes' views at one base two views of one section, the value's next view another section - and a base its process maps again is another view")]
    public void TheViewGateFollowsViewsAndTheirSection()
    {
        var gate = new SectionViewGate();
        ViewIncarnation creator = Admit(gate, new(SectionViewGate.Map, 0xA000, 0x77, 400));

        // The opener maps the section at the base the creator's view has in its own process: another view, the same section.
        ViewIncarnation opener = Admit(gate, new(SectionViewGate.Map, 0xA000, 0x77, 500));
        Assert.Equal((creator.Section, ViewFirstSeen.Mapping), (opener.Section, opener.FirstSeen));
        Assert.NotEqual(creator.View, opener.View);
        Assert.Equal(opener, Admit(gate, new(SectionViewGate.Unmap, 0xA000, 0x77, 500)));

        // The opener maps the section again at that base: another view of the same section.
        ViewIncarnation again = Admit(gate, new(SectionViewGate.Map, 0xA000, 0x77, 500));
        Assert.Equal(creator.Section, again.Section);
        Assert.NotEqual(opener.View, again.View);

        // Another section's view mapped at a base the process's followed view held: that view ended, unseen.
        ViewIncarnation other = Admit(gate, new(SectionViewGate.Map, 0xA000, 0x99, 500));
        Assert.NotEqual(creator.Section, other.Section);
        Assert.Equal((2, 2), (gate.Following, gate.Sections));

        // Once no followed view carries the value, the section ended: the value's next view begins another.
        Assert.Equal(creator, Admit(gate, new(SectionViewGate.Unmap, 0xA000, 0x77, 400)));
        Assert.Equal(other, Admit(gate, new(SectionViewGate.Unmap, 0xA000, 0x99, 500)));
        Assert.Equal((0, 0), (gate.Following, gate.Sections));
        Assert.NotEqual(creator.Section, Admit(gate, new(SectionViewGate.Map, 0xC000, 0x77, 400)).Section);
    }

    [Fact(DisplayName = "R22: a view the section view gate never saw begin is numbered alone at its unmapping or the stop's rundown, joining the section its value names only while a followed view holds it; the start's rundown begins views; a view naming no value joins no section; and beyond its bound a view is not followed")]
    public void ViewsBeforeTheCaptureAreNumberedAlone()
    {
        var gate = new SectionViewGate(capacity: 3);

        // The start's rundown begins a view, followed as a mapping is.
        ViewIncarnation seeded = Admit(gate, new(SectionViewGate.StartRundown, 0xA000, 0x55, 400));
        Assert.Equal(ViewFirstSeen.StartRundown, seeded.FirstSeen);

        // An unmapping of a view never seen begin, carrying the value a followed view holds: a view of its own, that section.
        ViewIncarnation joined = Admit(gate, new(SectionViewGate.Unmap, 0xF000, 0x55, 500));
        Assert.Equal((ViewFirstSeen.Unmapping, seeded.Section), (joined.FirstSeen, joined.Section));
        Assert.NotEqual(seeded.View, joined.View);

        // Two unmappings carrying a value no followed view holds: each view and each section alone.
        ViewIncarnation one = Admit(gate, new(SectionViewGate.Unmap, 0xF000, 0x66, 600));
        ViewIncarnation two = Admit(gate, new(SectionViewGate.Unmap, 0xF000, 0x66, 700));
        Assert.NotEqual(one.Section, two.Section);

        // The stop's rundown of a followed view is that view; of one never seen begin, a view numbered alone.
        Assert.Equal(seeded, Admit(gate, new(SectionViewGate.StopRundown, 0xA000, 0x55, 400)));
        Assert.Equal((ViewFirstSeen.StopRundown, seeded.Section), Of(Admit(gate, new(SectionViewGate.StopRundown, 0xE000, 0x55, 800))));

        // A view whose record names no value joins no section.
        Assert.Equal(0, Admit(gate, new(SectionViewGate.Map, 0xD000, 0, 400)).Section);

        // Bound: three views followed, the fourth not.
        _ = Admit(gate, new(SectionViewGate.Map, 0x1000, 0x88, 400));
        Assert.Equal(ViewGateOutcome.Untracked, gate.Read(new(SectionViewGate.Map, 0x2000, 0x88, 400)).Outcome);
        Assert.Equal(3, gate.Following);

        static (ViewFirstSeen, long) Of(ViewIncarnation view) => (view.FirstSeen, view.Section);
    }

    [Fact(DisplayName = "P12: the section view gate, which is to read its records on the capture's callback, grows its tables only as far as its bound and, once they hold what it followed at once, allocates nothing per record")]
    public void TheViewGateAllocatesOnlyAsItsTablesGrow()
    {
        // The gate's bound reached by views each of a section of its own - the most both its tables hold - and one view more
        // not followed: growing to the bound allocated 57.3 MiB, the arrays its tables outgrew included, held to 64 MiB.
        var gate = new SectionViewGate();
        SectionViewRecord[] mappings = [.. Enumerable.Range(0, SectionViewGate.DefaultCapacity + 1).Select(static view => Mapping(view))];
        long growing = GC.GetAllocatedBytesForCurrentThread();
        int untracked = Read(gate, mappings);
        growing = GC.GetAllocatedBytesForCurrentThread() - growing;
        Assert.Equal((1, SectionViewGate.DefaultCapacity, SectionViewGate.DefaultCapacity), (untracked, gate.Following, gate.Sections));
        Assert.InRange(growing, 16L << 20, 64L << 20);

        // Once grown, a thousand views unmapped and mapped again - each section ending and another beginning at its value - and
        // the stop's rundown of each allocate nothing. Under a full suite's load a measured pass may allocate what another
        // thread's work did, so the least of three passes is held to none, and the message names each pass's bytes.
        SectionViewRecord[] records =
        [
            .. Enumerable.Range(0, 1_000).SelectMany(static view => new[]
            {
                Mapping(view) with { Opcode = SectionViewGate.Unmap },
                Mapping(view),
                Mapping(view) with { Opcode = SectionViewGate.StopRundown },
            }),
        ];
        long[] allocated = new long[3];
        for (int pass = 0; pass < allocated.Length; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            untracked += Read(gate, records);
            allocated[pass] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(1, untracked);
        Assert.True(allocated.Min() == 0, $"Each warmed pass allocated {string.Join(", ", allocated)} bytes; growing to the bound {growing}.");
        Assert.Equal((SectionViewGate.DefaultCapacity, SectionViewGate.DefaultCapacity), (gate.Following, gate.Sections));

        static SectionViewRecord Mapping(int view) => new(SectionViewGate.Map, 0x10_0000 + ((ulong)view << 16), 0xFFFF_0000_0000 + ((ulong)view << 4), 400);

        // How many records the gate did not follow, counted without an assertion's allocations among the gate's.
        static int Read(SectionViewGate gate, SectionViewRecord[] records)
        {
            int untracked = 0;
            foreach (SectionViewRecord record in records)
            {
                if (gate.Read(in record).Outcome == ViewGateOutcome.Untracked)
                {
                    untracked++;
                }
            }

            return untracked;
        }
    }

    [Fact(DisplayName = "R22: FX-SECT-001's committed ledger read through the section gates as it was delivered is its two sections - each section's handles in both processes one number, every view of a section in both processes one section number, and each mapping a view of its own though the opener mapped them all at one base")]
    public void TheCommittedLedgerReadsThroughTheGates()
    {
        // The ledger's ordinals stand for the addresses, bases and values they number, equal where those were equal; each
        // process's handle to a section is the one value it closes.
        var handles = new SectionHandleGate([Section]);
        var views = new SectionViewGate();
        var objects = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var sections = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var maps = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (string[] field in File.ReadLines(Path.Combine(RepositoryRoot(), "fixtures", "FX-SECT-001", "evidence", "ledger.csv"))
            .Skip(1).Where(static line => line.Length > 0).Select(static line => line.Split(',')))
        {
            int process = field[1] == "creator" ? 400 : 500;
            string section = field[2];
            if (field[0] is "Create" or "Close")
            {
                uint handle = section == "memory" ? 4u : 8u;
                ObjectIncarnation incarnation = Admit(handles, new(field[0] == "Create" ? ObjectHandleGate.Create : ObjectHandleGate.Close,
                    ulong.Parse(field[3], CultureInfo.InvariantCulture), handle, Section, process));
                Add(objects, section, incarnation.Number);
                continue;
            }

            ViewIncarnation view = Admit(views, new(field[0] == "MapView" ? SectionViewGate.Map : SectionViewGate.Unmap,
                ulong.Parse(field[6], CultureInfo.InvariantCulture), ulong.Parse(field[9], CultureInfo.InvariantCulture), process));
            Add(sections, section, view.Section);
            if (field[0] == "MapView")
            {
                (maps.TryGetValue(section, out List<long>? list) ? list : maps[section] = []).Add(view.View);
            }
        }

        // Each section one number of its handles and one of its views, the two sections' apart; each mapping its own view.
        Assert.All(objects.Values, numbers => Assert.Single(numbers));
        Assert.All(sections.Values, numbers => Assert.Single(numbers));
        Assert.NotEqual(sections["memory"].Single(), sections["file"].Single());
        Assert.All(maps.Values, list => Assert.Equal(list.Count, list.Distinct().Count()));
        Assert.Equal((0, 0, 0), (handles.Following, views.Following, views.Sections));

        static void Add(Dictionary<string, HashSet<long>> into, string key, long value) =>
            (into.TryGetValue(key, out HashSet<long>? set) ? set : into[key] = []).Add(value);
    }

    private static ObjectIncarnation Admit(SectionHandleGate gate, ObjectHandleRecord record)
    {
        (HandleGateOutcome outcome, ObjectIncarnation? incarnation) = gate.Read(record);
        Assert.Equal(HandleGateOutcome.Admitted, outcome);
        return incarnation!.Value;
    }

    private static ViewIncarnation Admit(SectionViewGate gate, SectionViewRecord record)
    {
        (ViewGateOutcome outcome, ViewIncarnation? incarnation) = gate.Read(record);
        Assert.Equal(ViewGateOutcome.Admitted, outcome);
        return incarnation!.Value;
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
