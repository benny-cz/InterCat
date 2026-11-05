using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SyncProbe;

/// <summary>
/// FX-SYNC-003: named objects made and ended one generation after another - the creator making each generation's three under
/// names of their own, the opener opening them, the two taking their rounds through them, and both closing them before the
/// next is made - under a private system logger of the probe's own collecting the object manager's handle group, machine-wide.
/// A capture through the product numbers each object over its address and keeps no address, so only this probe can say where
/// the kernel made each generation's objects: each object's address, read from the creator's close that names it, and how many
/// the kernel made at an address an earlier generation's object held - an address a capture's records alone would join to
/// that earlier object. It writes counts alone, never an address.
/// </summary>
internal static class Generations
{
    // The object manager's handle records, by opcode: a handle's creation and its close.
    private const int Create = 32;
    private const int Close = 33;

    private static readonly string[] Synchronization = ["Event", "Mutant", "Semaphore"];

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string output, int generations, int rounds, int hold)
    {
        string truth = Path.Combine(output, "truth");
        var records = new List<Record>();
        var types = new Dictionary<int, string>();
        int lost;
        string sessionName = "InterCat-sync-generations-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 256 })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            if (GroupMask.Add(sessionName, [GroupMask.ObjectHandles]) is { } problem)
            {
                throw new InvalidOperationException("The handle group could not be added to the probe's session. " + problem);
            }

            session.Source.Kernel.ObjectCreateHandle += data => records.Add(Read(data, Create));
            session.Source.Kernel.ObjectCloseHandle += data => records.Add(Read(data, Close));
            session.Source.Kernel.ObjectTypeDCEnd += data => types[data.ObjectType] = data.ObjectTypeName;
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(1_500).ConfigureAwait(false);
            using (Process started = Process.Start(new ProcessStartInfo(workload,
                ["sync-objects", "--truth", truth, "--generations", generations.ToString(CultureInfo.InvariantCulture),
                    "--rounds", rounds.ToString(CultureInfo.InvariantCulture), "--hold-ms", hold.ToString(CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await started.WaitForExitAsync().ConfigureAwait(false);
                if (started.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {started.ExitCode}.");
                }
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        JsonElement root = scenario.RootElement;
        int creator = root.GetProperty("creatorProcessId").GetInt32();
        int opener = root.GetProperty("openerProcessId").GetInt32();
        string[] objects = [.. root.GetProperty("objects").EnumerateArray().Select(name => name.GetString()!)];
        HashSet<int> synchronization = [.. types.Where(type => Synchronization.Contains(type.Value, StringComparer.Ordinal)).Select(type => type.Key)];

        // Each object's address, from the creator's close that names it - a creation names nothing - and whether the creator's
        // creation of a handle at that address and the opener's close of one there came before and after it. The workload
        // lists its objects a generation at a time, so an earlier generation's are those listed before.
        int perGeneration = objects.Length / generations;
        var earlier = new HashSet<ulong>();
        var generation = new List<ulong>();
        var previousOfKind = new Dictionary<int, ulong>();
        var addresses = new HashSet<ulong>();
        int withOneAddress = 0;
        int creationsSeen = 0;
        int openerClosesSeen = 0;
        int reused = 0;
        int reusedOfItsKind = 0;
        int typed = 0;
        for (int index = 0; index < objects.Length; index++)
        {
            string part = objects[index][(objects[index].IndexOf('\\', StringComparison.Ordinal) + 1)..];
            Record[] closes = [.. records.Where(record => record.Opcode == Close && record.Name.EndsWith("\\" + part, StringComparison.Ordinal))];
            ulong[] at = [.. closes.Where(record => record.RaisedBy == creator).Select(static record => record.Object).Distinct()];
            if (at.Length == 1)
            {
                ulong address = at[0];
                withOneAddress++;
                addresses.Add(address);
                DateTime closed = closes.Where(record => record.RaisedBy == creator).Min(static record => record.Utc);
                creationsSeen += records.Any(record => record.Opcode == Create && record.RaisedBy == creator && record.Object == address
                    && record.Utc <= closed) ? 1 : 0;
                openerClosesSeen += closes.Any(record => record.RaisedBy == opener && record.Object == address) ? 1 : 0;
                typed += closes.All(record => synchronization.Contains(record.Type)) ? 1 : 0;
                reused += earlier.Contains(address) ? 1 : 0;
                int kind = index % perGeneration;
                reusedOfItsKind += previousOfKind.TryGetValue(kind, out ulong before) && before == address ? 1 : 0;
                previousOfKind[kind] = address;
                generation.Add(address);
            }

            // A generation's objects are earlier ones only once the whole generation is read: they live together.
            if ((index + 1) % perGeneration == 0)
            {
                earlier.UnionWith(generation);
                generation.Clear();
            }
        }

        return new()
        {
            ["question"] = "Where the kernel makes named objects made and ended one generation after another: how many of a later "
                + "generation's objects it made at an address an earlier generation's object held, which a capture's handle records "
                + "alone would join to that earlier object.",
            ["scenario"] = "FX-SYNC-003",
            ["generations"] = generations,
            ["roundsPerGeneration"] = rounds,
            ["holdMilliseconds"] = hold,
            ["objects"] = objects.Length,
            ["objectsWithOneAddress"] = withOneAddress,
            ["creationsSeenAtTheirAddress"] = creationsSeen,
            ["openerClosesAtTheirAddress"] = openerClosesSeen,
            ["closesOfASynchronizationType"] = typed,
            ["distinctAddresses"] = addresses.Count,
            ["objectsAtAnAddressAnEarlierGenerationsObjectHeld"] = reused,
            ["objectsAtTheAddressOfTheirKindInThePreviousGeneration"] = reusedOfItsKind,
            ["synchronizationTypes"] = Synchronization.ToDictionary(name => name, name => (object?)types.FirstOrDefault(type => type.Value == name).Key),
            ["handleRecords"] = records.Count,
            ["eventsLost"] = lost,
        };
    }

    // One handle's creation or close by the layout the machine registers (§4.2), read from its payload as the product reads
    // it: the object's address, the handle, the type and a close's name, with the process its header names.
    private static Record Read(TraceEvent data, int opcode)
    {
        byte[] payload = data.EventData();
        int pointer = data.PointerSize;
        DateTime utc = data.TimeStamp.ToUniversalTime();
        if (payload.Length < pointer + 6)
        {
            return new(opcode, utc, data.ProcessID, 0, -1, string.Empty);
        }

        // Object, Handle, ObjectType, ObjectName.
        ulong address = pointer == 8 ? BitConverter.ToUInt64(payload, 0) : BitConverter.ToUInt32(payload, 0);
        return new(opcode, utc, data.ProcessID, address, BitConverter.ToUInt16(payload, pointer + 4), NameAt(payload, pointer + 6));
    }

    // The name a record carries after its fixed fields; empty when it names none.
    private static string NameAt(byte[] payload, int offset)
    {
        if (payload.Length <= offset + sizeof(char))
        {
            return string.Empty;
        }

        string text = Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>One handle's creation or close: its opcode, time, the process its header names, the object's address, type and name.</summary>
    private readonly record struct Record(int Opcode, DateTime Utc, int RaisedBy, ulong Object, int Type, string Name);
}
