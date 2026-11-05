using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SyncProbe;

/// <summary>
/// Whether a capture can know, as it starts, which type a handle record's number names (ADR-082's fourth addendum): the
/// object manager's records name a handle's object type by a number, and the session names the numbers only in the type
/// rundown at its stop. The object type table, read from user mode by <c>NtQueryObject</c> before a session starts, names
/// each type with its index; this measures whether the two agree, and, over FX-SYNC-001 run under the handle group, how the
/// machine's handle records divide among the types - what a gate admitting only the synchronization types would keep.
/// </summary>
internal static partial class Types
{
    private const int ObjectTypesInformation = 3;
    private const uint StatusInfoLengthMismatch = 0xC000_0004;
    private static readonly Guid ObjectManagerClass = Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7");
    private static readonly string[] Synchronization = ["Event", "Mutant", "Semaphore"];

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string truth)
    {
        // The table as user mode reads it before the session: each type's name by its index.
        Dictionary<int, string> before = ReadTypeTable();
        var rundown = new Dictionary<int, string>();
        var creates = new Dictionary<int, long>();
        var closes = new Dictionary<int, long>();
        var namedCloses = new Dictionary<int, long>();
        var duplicates = new Dictionary<int, long>();
        int lost;
        string sessionName = "InterCat-sync-types-" + Guid.NewGuid().ToString("N");
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 256 })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            if (GroupMask.Add(sessionName, [GroupMask.ObjectHandles]) is { } problem)
            {
                throw new InvalidOperationException("The handle group could not be added to the probe's session. " + problem);
            }

            session.Source.Kernel.ObjectCreateHandle += data => Count(creates, data.ObjectType);
            session.Source.Kernel.ObjectCloseHandle += data =>
            {
                Count(closes, data.ObjectType);
                if (data.EventDataLength > data.PointerSize + 8)
                {
                    Count(namedCloses, data.ObjectType);
                }
            };
            session.Source.Kernel.ObjectDuplicateHandle += data => Count(duplicates, data.ObjectType);
            session.Source.Kernel.ObjectTypeDCEnd += data => rundown[data.ObjectType] = data.ObjectTypeName;
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(1_500).ConfigureAwait(false);
            using (Process run = Process.Start(new ProcessStartInfo(workload, ["sync-objects", "--truth", truth, "--rounds", "64", "--hold-ms", "5"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await run.WaitForExitAsync().ConfigureAwait(false);
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        // The table read again after: a type registered meanwhile would show as one the first read lacks.
        Dictionary<int, string> after = ReadTypeTable();
        int[] numbers = [.. before.Keys.Union(rundown.Keys).Union(after.Keys).Order()];
        string[] disagreements = [.. numbers.Where(number => !(before.TryGetValue(number, out string? first)
                && rundown.TryGetValue(number, out string? stop) && string.Equals(first, stop, StringComparison.Ordinal)))
            .Select(number => string.Create(CultureInfo.InvariantCulture,
                $"{number}: before {before.GetValueOrDefault(number) ?? "none"}, rundown {rundown.GetValueOrDefault(number) ?? "none"}, after {after.GetValueOrDefault(number) ?? "none"}"))];

        long Total(Dictionary<int, long> counts) => counts.Values.Sum();
        long Of(Dictionary<int, long> counts, IEnumerable<string> names) =>
            counts.Where(pair => before.TryGetValue(pair.Key, out string? name) && names.Contains(name)).Sum(pair => pair.Value);
        Dictionary<string, object> ByType(Dictionary<int, long> counts) => counts.OrderByDescending(pair => pair.Value).Take(12)
            .ToDictionary(pair => before.GetValueOrDefault(pair.Key) ?? pair.Key.ToString(CultureInfo.InvariantCulture), pair => (object)pair.Value);

        return new()
        {
            ["question"] = "Whether the object type table read from user mode before a capture starts names each type number as the "
                + "type rundown at the session's stop does, and how the machine's handle records divide among the types.",
            ["typesBefore"] = before.Count,
            ["typesInRundown"] = rundown.Count,
            ["typesAfter"] = after.Count,
            ["disagreements"] = disagreements,
            ["synchronizationTypes"] = Synchronization.ToDictionary(name => name, name => (object?)before.FirstOrDefault(pair => pair.Value == name).Key),
            ["creates"] = Total(creates),
            ["createsOfSynchronizationTypes"] = Of(creates, Synchronization),
            ["closes"] = Total(closes),
            ["closesOfSynchronizationTypes"] = Of(closes, Synchronization),
            ["namedCloses"] = Total(namedCloses),
            ["namedClosesOfSynchronizationTypes"] = Of(namedCloses, Synchronization),
            ["duplicates"] = Total(duplicates),
            ["duplicatesOfSynchronizationTypes"] = Of(duplicates, Synchronization),
            ["createsByType"] = ByType(creates),
            ["namedClosesByType"] = ByType(namedCloses),
            ["eventsLost"] = lost,
        };
    }

    private static void Count(Dictionary<int, long> counts, int type) => counts[type] = counts.GetValueOrDefault(type) + 1;

    // OBJECT_TYPES_INFORMATION: a count, then each OBJECT_TYPE_INFORMATION - its name as a UNICODE_STRING, counters, and at
    // offset 90 its index - followed by its name's characters, the next entry at the pointer-aligned end of those.
    private static unsafe Dictionary<int, string> ReadTypeTable()
    {
        int size = 64 * 1024;
        while (true)
        {
            byte[] buffer = new byte[size];
            fixed (byte* start = buffer)
            {
                uint status = NtQueryObject(0, ObjectTypesInformation, start, (uint)size, out uint needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, (int)needed);
                    continue;
                }

                if (status != 0)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"NtQueryObject failed: 0x{status:X8}."));
                }

                var types = new Dictionary<int, string>();
                int count = *(int*)start;
                byte* entry = start + IntPtr.Size;
                for (int index = 0; index < count; index++)
                {
                    ushort length = *(ushort*)entry;
                    ushort maximum = *(ushort*)(entry + 2);
                    char* name = *(char**)(entry + 8);
                    int typeIndex = *(entry + 90);
                    types[typeIndex] = new string(name, 0, length / sizeof(char));
                    long next = (long)(entry + 104 + maximum);
                    entry = (byte*)((next + IntPtr.Size - 1) & ~(long)(IntPtr.Size - 1));
                }

                return types;
            }
        }
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial uint NtQueryObject(nint handle, int informationClass, byte* information, uint length, out uint returnLength);
}
