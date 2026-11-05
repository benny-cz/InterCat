using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.MailslotProbe;

/// <summary>
/// FX-MAIL-001 under two sessions of the probe's own, machine-wide: a private system logger (ADR-035) collecting the kernel's
/// classic file I/O class - its create, read, write, cleanup, close, name and operation-end events - beside the process flag,
/// and an owned session enabling Microsoft-Windows-Kernel-File at the keywords ADR-003 measured named pipes under. What each
/// records of the run's mailslot - the server's creation, the client's open, each message written and read with the size
/// asked for and the size moved, each close - is set against both processes' truth, and the records each delivered of the
/// two processes' other files are its control: a source live for them that records no mailslot record.
/// </summary>
internal static class Measure
{
    // Microsoft-Windows-Kernel-File's FILENAME, FILEIO, OP_END, CREATE, READ and WRITE keywords, as ADR-003 enabled them.
    private const ulong KernelFileKeywords = 0x3F0;

    private static readonly string[] TruthFiles = ["truth-server.jsonl", "truth-client.jsonl"];

    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string output, int messages, int bytes, int delay,
        string? dump = null)
    {
        string truth = Path.Combine(output, "truth");
        var classic = new List<Record>();
        var manifest = new List<Record>();
        int classicLost;
        int manifestLost;
        string classicName = "InterCat-mailslot-fileio-" + Guid.NewGuid().ToString("N");
        string manifestName = "InterCat-mailslot-kernelfile-" + Guid.NewGuid().ToString("N");
        using (var system = new TraceEventSession(classicName) { StopOnDispose = true, BufferSizeMB = 256 })
        using (var owned = new TraceEventSession(manifestName) { StopOnDispose = true, BufferSizeMB = 256 })
        {
            system.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
            KernelTraceEventParser kernel = system.Source.Kernel;
            kernel.FileIOCreate += data => classic.Add(new("Create", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileObject, data.IrpPtr, data.FileName, null, null));
            kernel.FileIORead += data => classic.Add(new("Read", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileObject, data.IrpPtr, data.FileName, data.IoSize, null));
            kernel.FileIOWrite += data => classic.Add(new("Write", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileObject, data.IrpPtr, data.FileName, data.IoSize, null));
            kernel.FileIOCleanup += data => classic.Add(new("Cleanup", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileObject, data.IrpPtr, data.FileName, null, null));
            kernel.FileIOClose += data => classic.Add(new("Close", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileObject, data.IrpPtr, data.FileName, null, null));
            kernel.FileIOOperationEnd += data => classic.Add(new("OperationEnd", data.TimeStamp, data.ProcessID, data.ThreadID, 0, data.IrpPtr, null, null, (long)data.ExtraInfo) { Status = data.NtStatus });
            // A name event keys its file by the file's key, which the class's other events carry beside their file object.
            kernel.FileIOName += data => classic.Add(new("Name", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileKey, 0, data.FileName, null, null));
            kernel.FileIOFileCreate += data => classic.Add(new("FileCreate", data.TimeStamp, data.ProcessID, data.ThreadID, data.FileKey, 0, data.FileName, null, null));

            owned.EnableProvider("Microsoft-Windows-Kernel-File", TraceEventLevel.Informational, KernelFileKeywords);
            owned.Source.Dynamic.All += data =>
            {
                if (data.ProviderName != "Microsoft-Windows-Kernel-File")
                {
                    return;
                }

                manifest.Add(new(data.EventName, data.TimeStamp, data.ProcessID, data.ThreadID, Pointer(data, "FileObject"), Pointer(data, "Irp"),
                    data.PayloadByName("FileName") as string, Number(data, "IOSize"), Number(data, "ExtraInformation")) { Status = (int?)Number(data, "Status") });
            };

            Task classicProcessing = Task.Run(() => system.Source.Process());
            Task manifestProcessing = Task.Run(() => owned.Source.Process());
            await Task.Delay(2_000).ConfigureAwait(false);
            using (Process started = Process.Start(new ProcessStartInfo(workload,
                ["mailslot-loopback", "--truth", truth, "--messages", messages.ToString(CultureInfo.InvariantCulture),
                    "--bytes", bytes.ToString(CultureInfo.InvariantCulture), "--delay", delay.ToString(CultureInfo.InvariantCulture)])
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

            await Task.Delay(2_000).ConfigureAwait(false);
            classicLost = system.EventsLost;
            manifestLost = owned.EventsLost;
            system.Stop();
            owned.Stop();
            await Task.WhenAll(classicProcessing, manifestProcessing).ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        JsonElement root = scenario.RootElement;
        var run = new Run(
            root.GetProperty("name").GetString()!,
            root.GetProperty("serverProcessId").GetInt32(),
            root.GetProperty("clientProcessId").GetInt32(),
            root.GetProperty("readBytes").GetInt64());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        TruthRecord[] logged = [.. TruthFiles
            .SelectMany(file => File.ReadLines(Path.Combine(truth, file)))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<TruthRecord>(line, options)!)];

        // A local diagnosis of every file record of the two processes, never part of a committed result: it names their files.
        if (dump is not null)
        {
            using var writer = new StreamWriter(dump);
            await writer.WriteLineAsync("source,kind,role,thread,fileObject,irp,size,extra,name").ConfigureAwait(false);
            foreach ((string source, List<Record> list) in new[] { ("classic", classic), ("kernel-file", manifest) })
            {
                foreach (Record record in list.Where(record => record.Process == run.Server || record.Process == run.Client))
                {
                    await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{source},{record.Kind},{(record.Process == run.Server ? "server" : "client")},{record.Thread},{record.FileObject:X},{record.Irp:X},{record.Size},{record.Extra},\"{record.Name}\"")).ConfigureAwait(false);
                }
            }
        }
        return new()
        {
            ["question"] = "Whether the kernel's classic file I/O class, collected by a private system logger, or "
                + "Microsoft-Windows-Kernel-File records a mailslot a server made and a client opened by its name - the mailslot's "
                + "creation, the client's open, each message written and read with the size asked for and the size moved, each "
                + "close - and the two processes' other files, the control.",
            ["scenario"] = "FX-MAIL-001",
            ["truth"] = new Dictionary<string, object?>
            {
                ["messagesWritten"] = logged.Count(record => record.Kind == TruthEventKind.MessageSent),
                ["messagesRead"] = logged.Count(record => record.Kind == TruthEventKind.MessageReceived),
                ["bytesWritten"] = logged.Where(record => record.Kind == TruthEventKind.MessageSent).Sum(record => record.CompletedBytes ?? 0),
                ["bytesRead"] = logged.Where(record => record.Kind == TruthEventKind.MessageReceived).Sum(record => record.CompletedBytes ?? 0),
                ["readAskedFor"] = run.ReadBytes,
            },
            ["classicFileIo"] = Analyze(classic, run, logged, classicLost),
            ["kernelFile"] = Analyze(manifest, run, logged, manifestLost),
        };
    }

    /// <summary>What one source recorded of the run's mailslot and of the two processes' other files.</summary>
    private static Dictionary<string, object?> Analyze(List<Record> records, Run run, TruthRecord[] logged, int lost)
    {
        // The mailslot's file objects: those any record names by the run's own name, the part after the mailslot root.
        string part = run.Name[(run.Name.IndexOf(@"\mailslot\", StringComparison.OrdinalIgnoreCase) + @"\mailslot\".Length)..];
        bool Named(Record record) => record.Name is { } name && name.Contains(part, StringComparison.OrdinalIgnoreCase);
        HashSet<ulong> objects = [.. records.Where(record => Named(record) && record.FileObject != 0).Select(static record => record.FileObject)];
        Record[] mailslot = [.. records.Where(record => Named(record) || (record.FileObject != 0 && objects.Contains(record.FileObject)))];
        HashSet<Record> ofTheMailslot = [.. mailslot];
        // Each operation's end by its IRP, which joins it to the read or write it ended; an IRP is reused once it ended, so a
        // read takes the first end after it.
        Record[] ends = [.. records.Where(static record => record.Kind.Contains("OperationEnd", StringComparison.Ordinal)).OrderBy(static record => record.Utc)];
        Record? EndOf(Record operation) => ends.FirstOrDefault(end => end.Irp == operation.Irp && end.Utc >= operation.Utc);

        string Role(int process) => process == run.Server ? "server" : process == run.Client ? "client" : "other";
        long[] written = [.. logged.Where(record => record.Kind == TruthEventKind.MessageSent).Select(record => record.CompletedBytes ?? 0)];
        long[] read = [.. logged.Where(record => record.Kind == TruthEventKind.MessageReceived).Select(record => record.CompletedBytes ?? 0)];
        Record[] writes = [.. mailslot.Where(record => record.Kind.Contains("Write", StringComparison.Ordinal))];
        Record[] reads = [.. mailslot.Where(record => record.Kind.Contains("Read", StringComparison.Ordinal))];
        long?[] readsMoved = [.. reads.Select(record => EndOf(record)?.Extra)];
        return new()
        {
            ["recordsDelivered"] = records.Count,
            ["eventsLost"] = lost,
            ["mailslotFileObjects"] = objects.Count,
            ["mailslotRecordsByKindAndRole"] = mailslot.GroupBy(record => $"{record.Kind} by {Role(record.Process)}")
                .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()),
            ["mailslotNamesSeen"] = mailslot.Where(record => record.Name is not null).Select(record => Generalize(record.Name!, part))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ["writesWithTheTruthsSizes"] = writes.Length == written.Length && writes.Select(record => record.Size).SequenceEqual(written.Select(size => (long?)size)),
            ["readsAskingTheTruthsSize"] = reads.Length > 0 && reads.All(record => record.Size == run.ReadBytes),
            ["readsWhoseEndMovedTheTruthsSize"] = reads.Length == read.Length && readsMoved.SequenceEqual(read.Select(size => (long?)size)),
            // A mailslot's reads and writes would carry no name where its creation and open were not recorded: the two processes'
            // reads and writes of the truth's sizes, whatever file they name, are counted apart.
            ["clientWritesOfATruthMessagesSize"] = records.Count(record => record.Process == run.Client
                && record.Kind.Contains("Write", StringComparison.Ordinal) && record.Size is { } size && written.Contains(size)),
            ["serverReadsAskingTheTruthsSize"] = records.Count(record => record.Process == run.Server
                && record.Kind.Contains("Read", StringComparison.Ordinal) && record.Size == run.ReadBytes),
            ["readsAndWritesOfTheTwoByKind"] = records.Where(record => (record.Process == run.Server || record.Process == run.Client)
                && (record.Kind.Contains("Read", StringComparison.Ordinal) || record.Kind.Contains("Write", StringComparison.Ordinal)))
                .GroupBy(record => $"{record.Kind} by {Role(record.Process)}").OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count()),
            ["controlRecordsOfTheServer"] = records.Count(record => record.Process == run.Server && !ofTheMailslot.Contains(record)),
            ["controlRecordsOfTheClient"] = records.Count(record => record.Process == run.Client && !ofTheMailslot.Contains(record)),
            ["controlFilesOfTheTwo"] = records.Where(record => (record.Process == run.Server || record.Process == run.Client) && record.Name is not null
                && !Named(record)).Select(record => record.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
    }

    // A name with the run's own part taken out, which says nothing of the run.
    private static string Generalize(string name, string part) =>
        name.Replace(part, "<run>", StringComparison.OrdinalIgnoreCase);

    private static ulong Pointer(TraceEvent data, string field) => data.PayloadByName(field) switch
    {
        ulong value => value,
        long value => unchecked((ulong)value),
        uint value => value,
        int value => unchecked((uint)value),
        _ => 0,
    };

    private static long? Number(TraceEvent data, string field) => data.PayloadByName(field) switch
    {
        ulong value => unchecked((long)value),
        long value => value,
        uint value => value,
        int value => value,
        _ => null,
    };

    /// <summary>One file record of a source: its kind, time, process, thread, file object, IRP, name, size and extra information.</summary>
    private sealed record Record(string Kind, DateTime Utc, int Process, int Thread, ulong FileObject, ulong Irp, string? Name, long? Size, long? Extra)
    {
        public int? Status { get; init; }
    }

    /// <summary>The run's mailslot, its two processes and what each read asked for.</summary>
    private sealed record Run(string Name, int Server, int Client, long ReadBytes);
}
