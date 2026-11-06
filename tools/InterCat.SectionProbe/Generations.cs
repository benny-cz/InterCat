using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SectionProbe;

/// <summary>
/// FX-SECT-002 watched, beside the product's capture, by a private system logger of the probe's own (ADR-035), machine-wide:
/// process lifecycle and the kernel's file view flag, with the object manager's handle group added to its masks, as
/// FX-SECT-001's measurement collects them. A capture through the product numbers a section by its handles over its object's
/// address and by its views over the value they carry, and keeps neither, so only this probe can say where the kernel made
/// each generation's sections: each section's object address, read from the creator's close that names it, and the value its
/// views carry, read from the records of the views the truth mapped - and how many of a later generation's sections the
/// kernel made at an address, or whose views it gave a value, an earlier generation's section held, which a capture's
/// records alone would join to that earlier section. It writes counts alone, never an address, a base or a value.
/// </summary>
internal sealed class Generations : IDisposable
{
    // The object manager's handle records and the file view flag's mapping, by opcode.
    private const int Create = 32;
    private const int Close = 33;
    private const int Map = 37;

    // Every FX-SECT section's name begins so after its namespace; a close naming none is not kept.
    private const string Prefix = "InterCat-FX-SECT-";

    private static readonly Guid FileIoClass = Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    // How far from its truth line a mapping's record may lie, as FX-SECT-001's measurement matches them (ADR-089).
    private static readonly TimeSpan MatchWindow = TimeSpan.FromMilliseconds(5);

    private static readonly string[] Kinds = ["memory", "file"];

    private static readonly string[] TruthFiles = ["truth-creator.jsonl", "truth-opener.jsonl"];

    private readonly TraceEventSession session;
    private readonly Task processing;
    private readonly List<Handle> creations = [];
    private readonly List<Handle> closes = [];
    private readonly List<Mapping> mappings = [];
    private readonly Dictionary<int, string> types = [];

    private Generations(TraceEventSession session)
    {
        this.session = session;
        TraceEventDispatcher source = session.Source;

        // Every creation, which names nothing, by its process and object; every close naming one of the runs' sections.
        source.Kernel.ObjectCreateHandle += data => creations.Add(new(Create, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.Object, data.ObjectType, string.Empty));
        source.Kernel.ObjectCloseHandle += data =>
        {
            string name = NameAt(data, data.PointerSize + 6);
            if (name.Contains(Prefix, StringComparison.Ordinal))
            {
                closes.Add(new(Close, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.Object, data.ObjectType, name));
            }
        };
        source.Kernel.ObjectTypeDCEnd += data => types[data.ObjectType] = data.ObjectTypeName;

        // Every view's mapping, which TraceEvent does not parse, read by the layout FX-SECT-001 measured (ADR-091): its base,
        // the value its section's views carry, its size and, where the record ends, its process.
        source.AllEvents += data =>
        {
            if (data.TaskGuid != FileIoClass || (int)data.Opcode != Map)
            {
                return;
            }

            byte[] payload = data.EventData();
            int pointer = data.PointerSize;
            if (payload.Length < (3 * pointer) + 8 + 4)
            {
                return;
            }

            ulong Pointer(int offset) => pointer == 8 ? BitConverter.ToUInt64(payload, offset) : BitConverter.ToUInt32(payload, offset);
            mappings.Add(new(data.TimeStamp.ToUniversalTime(), BitConverter.ToInt32(payload, payload.Length - 4), Pointer(0), Pointer(pointer),
                (long)Pointer((2 * pointer) + 8)));
        };
        processing = Task.Run(() => source.Process());
    }

    /// <summary>Starts the probe's own session, and returns once it runs, so the workload starts inside it.</summary>
    public static async Task<Generations> StartAsync()
    {
        // A session of its own name: a kernel provider in a session not named NT Kernel Logger is a private system logger,
        // which this probe owns and stops, and which touches no other session - the product's capture's among them.
        string name = "InterCat-section-generations-" + Guid.NewGuid().ToString("N");
        var session = new TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 256 };
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.VAMap);
            if (GroupMask.Add(name, [GroupMask.ObjectHandles]) is { } problem)
            {
                throw new InvalidOperationException("The handle group could not be added to the probe's session. " + problem);
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var watch = new Generations(session);
        await Task.Delay(1_500).ConfigureAwait(false);
        return watch;
    }

    /// <summary>Stops the probe's session once the workload is done, and counts where the kernel made the run's sections.</summary>
    public async Task<Dictionary<string, object?>> FinishAsync(string truth)
    {
        await Task.Delay(1_500).ConfigureAwait(false);
        int lost = session.EventsLost;
        session.Stop();
        await processing.ConfigureAwait(false);
        return Count(truth, lost);
    }

    public void Dispose() => session.Dispose();

    private Dictionary<string, object?> Count(string truth, int lost)
    {
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        JsonElement root = scenario.RootElement;
        int creator = root.GetProperty("creatorProcessId").GetInt32();
        int opener = root.GetProperty("openerProcessId").GetInt32();
        int generations = root.GetProperty("generations").GetInt32();
        string[] sections = [.. root.GetProperty("sections").EnumerateArray().Select(static name => name.GetString()!)];
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        TruthRecord[] truthMappings =
        [
            .. TruthFiles.SelectMany(file => File.ReadAllLines(Path.Combine(truth, file)))
                .Where(static line => line.Length > 0).Select(line => JsonSerializer.Deserialize<TruthRecord>(line, json)!)
                .Where(static record => record.Kind == TruthEventKind.ViewMapped),
        ];
        HashSet<int> sectionTypes = [.. types.Where(static type => type.Value == "Section").Select(static type => type.Key)];
        Handle[] creatorCreations = [.. creations.Where(creation => creation.ProcessId == creator)];
        Mapping[] workloadMappings = [.. mappings.Where(mapping => mapping.ProcessId == creator || mapping.ProcessId == opener)];
        var taken = new HashSet<int>();

        // The workload lists its sections a generation at a time, so an earlier generation's are those listed before; a
        // generation's sections are earlier ones only once the whole generation is read, as they live together.
        int perGeneration = sections.Length / generations;
        var earlierAddresses = new HashSet<ulong>();
        var earlierValues = new HashSet<ulong>();
        var earlierBases = new HashSet<(int, ulong)>();
        var generationAddresses = new List<ulong>();
        var generationValues = new List<ulong>();
        var generationBases = new List<(int, ulong)>();
        var previousAddressOfKind = new Dictionary<int, ulong>();
        var previousValueOfKind = new Dictionary<int, ulong>();
        var addresses = new HashSet<ulong>();
        var values = new HashSet<ulong>();
        var counts = Kinds.ToDictionary(static kind => kind, static _ => new int[3]);
        int withOneAddress = 0, creationsSeen = 0, openerCloses = 0, typed = 0, reusedAddresses = 0, reusedAddressesOfKind = 0;
        int mappingsTruth = 0, mappingsSeen = 0, withOneValue = 0, reusedValues = 0, reusedValuesOfKind = 0, reusedBases = 0;
        for (int index = 0; index < sections.Length; index++)
        {
            string section = sections[index];
            string tail = "\\" + section[(section.IndexOf('\\', StringComparison.Ordinal) + 1)..];
            int kind = index % perGeneration;
            int[] ofKind = counts[Kinds[kind % Kinds.Length]];
            ofKind[0]++;

            // By its handles: its object's address, from the creator's close that names it - a creation names nothing - and
            // whether the creator's creation of a handle at that address and the opener's close of one there were seen.
            Handle[] named = [.. closes.Where(close => close.Name.EndsWith(tail, StringComparison.Ordinal))];
            ulong[] at = [.. named.Where(close => close.ProcessId == creator).Select(static close => close.Object).Distinct()];
            if (at.Length == 1)
            {
                ulong address = at[0];
                withOneAddress++;
                addresses.Add(address);
                DateTime closed = named.Where(close => close.ProcessId == creator).Min(static close => close.Utc);
                creationsSeen += creatorCreations.Any(creation => creation.Object == address && creation.Utc <= closed) ? 1 : 0;
                openerCloses += named.Any(close => close.ProcessId == opener && close.Object == address) ? 1 : 0;
                typed += named.All(close => sectionTypes.Contains(close.Type)) ? 1 : 0;
                bool reused = earlierAddresses.Contains(address);
                reusedAddresses += reused ? 1 : 0;
                ofKind[1] += reused ? 1 : 0;
                reusedAddressesOfKind += previousAddressOfKind.TryGetValue(kind, out ulong before) && before == address ? 1 : 0;
                previousAddressOfKind[kind] = address;
                generationAddresses.Add(address);
            }

            // By its views: each truth mapping's record - the mapping in its process, of its size, nearest its log line within
            // the window and taken by no earlier one - and the value they carry, and the base each was mapped at in its process.
            var own = new HashSet<ulong>();
            TruthRecord[] ownMappings = [.. truthMappings.Where(record => record.ResourceName == section)];
            mappingsTruth += ownMappings.Length;
            int seen = 0;
            foreach (TruthRecord mapping in ownMappings)
            {
                int found = -1;
                long nearest = long.MaxValue;
                for (int candidate = 0; candidate < workloadMappings.Length; candidate++)
                {
                    Mapping record = workloadMappings[candidate];
                    long distance = Math.Abs(record.Utc.Ticks - mapping.RecordedUtc.UtcTicks);
                    if (record.ProcessId == mapping.ProcessId && record.Size == mapping.DeclaredBytes && distance <= MatchWindow.Ticks
                        && distance < nearest && !taken.Contains(candidate))
                    {
                        (found, nearest) = (candidate, distance);
                    }
                }

                if (found < 0)
                {
                    continue;
                }

                taken.Add(found);
                seen++;
                Mapping view = workloadMappings[found];
                own.Add(view.Value);
                reusedBases += earlierBases.Contains((view.ProcessId, view.Base)) ? 1 : 0;
                generationBases.Add((view.ProcessId, view.Base));
            }

            mappingsSeen += seen;
            if (seen == ownMappings.Length && own.Count == 1)
            {
                ulong value = own.Single();
                withOneValue++;
                values.Add(value);
                bool reused = earlierValues.Contains(value);
                reusedValues += reused ? 1 : 0;
                ofKind[2] += reused ? 1 : 0;
                reusedValuesOfKind += previousValueOfKind.TryGetValue(kind, out ulong before) && before == value ? 1 : 0;
                previousValueOfKind[kind] = value;
                generationValues.Add(value);
            }

            if ((index + 1) % perGeneration == 0)
            {
                earlierAddresses.UnionWith(generationAddresses);
                earlierValues.UnionWith(generationValues);
                earlierBases.UnionWith(generationBases);
                generationAddresses.Clear();
                generationValues.Clear();
                generationBases.Clear();
            }
        }

        return new()
        {
            ["question"] = "Where the kernel makes sections made and ended one generation after another: how many of a later "
                + "generation's sections it made at an object address an earlier generation's section held, and how many whose views "
                + "it gave the value an earlier generation's section's views carried - either of which a capture's records alone would "
                + "join to that earlier section.",
            ["scenario"] = "FX-SECT-002",
            ["generations"] = generations,
            ["sections"] = sections.Length,
            ["sectionsWithOneAddress"] = withOneAddress,
            ["creationsSeenAtTheirAddress"] = creationsSeen,
            ["openerClosesAtTheirAddress"] = openerCloses,
            ["closesOfTheSectionType"] = typed,
            ["distinctAddresses"] = addresses.Count,
            ["sectionsAtAnAddressAnEarlierGenerationsSectionHeld"] = reusedAddresses,
            ["sectionsAtTheAddressOfTheirKindInThePreviousGeneration"] = reusedAddressesOfKind,
            ["truthMappings"] = mappingsTruth,
            ["mappingsSeen"] = mappingsSeen,
            ["sectionsWithOneViewValue"] = withOneValue,
            ["distinctViewValues"] = values.Count,
            ["sectionsWhoseViewsCarriedAValueAnEarlierGenerationsSectionsViewsCarried"] = reusedValues,
            ["sectionsWhoseViewsCarriedTheValueOfTheirKindInThePreviousGeneration"] = reusedValuesOfKind,
            ["viewValuesThatAreASectionsAddress"] = values.Count(addresses.Contains),
            ["mappingsAtABaseAnEarlierGenerationsViewHeldInItsProcess"] = reusedBases,
            ["byKind"] = counts.ToDictionary(static pair => pair.Key, static pair => (object)new Dictionary<string, int>
            {
                ["sections"] = pair.Value[0],
                ["atAnAddressAnEarlierGenerationsSectionHeld"] = pair.Value[1],
                ["withAViewValueAnEarlierGenerationsSectionsViewsCarried"] = pair.Value[2],
            }),
            ["sectionType"] = types.Where(static type => type.Value == "Section").Select(static type => (int?)type.Key).FirstOrDefault(),
            ["sectionClosesKept"] = closes.Count,
            ["mappingRecords"] = mappings.Count,
            ["eventsLost"] = lost,
        };
    }

    // The name a handle record carries, read by the layout the machine registers (§4.2) after its fixed fields, which
    // TraceEvent does not read; empty when the record names none.
    private static string NameAt(TraceEvent data, int offset)
    {
        byte[] payload = data.EventData();
        if (payload.Length <= offset + sizeof(char))
        {
            return string.Empty;
        }

        string text = Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>One handle's creation or close: its opcode, time, the process its header names, its object's address, type and name.</summary>
    private readonly record struct Handle(int Opcode, DateTime Utc, int ProcessId, ulong Object, int Type, string Name);

    /// <summary>One view's mapping: its time, the process its payload names, its base, the value its section's views carry and its size.</summary>
    private readonly record struct Mapping(DateTime Utc, int ProcessId, ulong Base, ulong Value, long Size);
}
