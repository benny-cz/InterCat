using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-SECT-001 run: the size of each view, and how many times the opener maps each section.</summary>
internal sealed record SectionOptions
{
    public const string ScenarioId = "FX-SECT-001";

    public required string TruthDirectory { get; init; }

    /// <summary>The sections' shared name, which the coordinator chooses and gives both processes; null for the coordinator.</summary>
    public string? Name { get; init; }

    /// <summary>The size of each section and of every view of it, in bytes.</summary>
    public int Bytes { get; init; } = 65_536;

    /// <summary>How many times the opener maps and unmaps a view of each section, checking the creator's bytes each time.</summary>
    public int Rounds { get; init; } = 4;

    /// <summary>The seed of the bytes the creator writes into each section.</summary>
    public int Seed { get; init; } = 20_261_106;
}

/// <summary>
/// FX-SECT-001: two processes share two named sections of the session's namespace, one backed by the paging file and one by
/// a file of the run's own. A creator makes both under a name of the run's own, maps a view of each into its address space
/// and writes seeded bytes into it; an opener opens each by its name and maps and unmaps a view of it that many times,
/// reading the creator's bytes through each. Then the opener closes its handles, and the creator unmaps its views, closes its
/// handles and deletes the file. Each process logs every section it made or opened, every view it mapped or unmapped, with
/// its size, and every handle it closed, by the section's name. Neither reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class SectionScenario
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0x000F_001F;
    private static readonly nint InvalidHandle = -1;

    /// <summary>The section backed by the paging file, its status in a truth log and its name's suffix.</summary>
    private const string MemoryKind = "section";

    /// <summary>The section backed by a file of the run's own.</summary>
    private const string FileKind = "file section";

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Starts the creator, then the opener once the creator's views hold their bytes, and ends the creator once the opener is done.</summary>
    public static async Task<int> RunCoordinatorAsync(SectionOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // A name of its own each run, in the session's namespace, so no other run's sections or any other program's share it.
        string name = $@"Local\InterCat-{SectionOptions.ScenarioId}-{Guid.NewGuid():N}";
        using Process creator = StartChild(executable, "section-share-creator", options with { Name = name });
        if (await creator.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "ready")
        {
            await creator.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The creator exited before its sections held their bytes.").ConfigureAwait(false);
            return 1;
        }

        using Process opener = StartChild(executable, "section-share-opener", options with { Name = name });
        await opener.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        // The creator unmaps, closes and deletes only once the opener is done with the sections.
        await creator.StandardInput.WriteLineAsync("done").ConfigureAwait(false);
        await creator.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        await creator.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var summary = new
        {
            scenarioId = SectionOptions.ScenarioId,
            name,
            sections = new[] { SectionName(name, MemoryKind), SectionName(name, FileKind) },
            options.Bytes,
            options.Rounds,
            options.Seed,
            coordinatorProcessId = Environment.ProcessId,
            creatorProcessId = creator.Id,
            openerProcessId = opener.Id,
            creatorExitCode = creator.ExitCode,
            openerExitCode = opener.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return creator.ExitCode != 0 || opener.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>
    /// The creator: makes both sections, maps a view of each and writes its seeded bytes, says so, and once told the opener
    /// is done unmaps its views, closes its handles and deletes the file.
    /// </summary>
    public static async Task<int> RunCreatorAsync(SectionOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The creator takes the sections' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-creator.jsonl"), SectionOptions.ScenarioId, "creator");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: name);
        string backing = Path.Combine(options.TruthDirectory, name[(name.IndexOf('\\', StringComparison.Ordinal) + 1)..] + ".section");
        var sections = new List<(string Kind, string Name, nint Section, nint View)>();
        using (var file = new FileStream(backing, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            file.SetLength(options.Bytes);
            nint fileHandle = file.SafeFileHandle.DangerousGetHandle();
            foreach ((string kind, nint backedBy) in new[] { (MemoryKind, InvalidHandle), (FileKind, fileHandle) })
            {
                string sectionName = SectionName(name, kind);
                nint section = CreateFileMappingW(backedBy, 0, PageReadWrite, 0, (uint)options.Bytes, sectionName);
                if (section == 0)
                {
                    truth.Write(TruthEventKind.Failure, status: $"The {kind} could not be made ({Marshal.GetLastPInvokeError()}).", resourceName: sectionName);
                    return 1;
                }

                truth.Write(TruthEventKind.ObjectCreated, status: kind, resourceName: sectionName);
                nint view = MapViewOfFile(section, FileMapAllAccess, 0, 0, (nuint)options.Bytes);
                if (view == 0)
                {
                    truth.Write(TruthEventKind.Failure, status: $"A view of the {kind} could not be mapped.", resourceName: sectionName);
                    return 1;
                }

                truth.Write(TruthEventKind.ViewMapped, callId: 1, declaredBytes: options.Bytes, status: kind, resourceName: sectionName);
                Marshal.Copy(Bytes(options, kind), 0, view, options.Bytes);
                sections.Add((kind, sectionName, section, view));
            }

            Console.WriteLine("ready");
            Console.Out.Flush();
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            foreach ((string kind, string sectionName, nint section, nint view) in sections)
            {
                _ = UnmapViewOfFile(view);
                truth.Write(TruthEventKind.ViewUnmapped, callId: 1, status: kind, resourceName: sectionName);
                _ = CloseHandle(section);
                truth.Write(TruthEventKind.HandleClosed, status: kind, resourceName: sectionName);
            }
        }

        File.Delete(backing);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>
    /// The opener: opens each section by its name, maps and unmaps a view of it that many times, reading the creator's bytes
    /// through each, and closes its handle.
    /// </summary>
    public static async Task<int> RunOpenerAsync(SectionOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The opener takes the sections' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-opener.jsonl"), SectionOptions.ScenarioId, "opener");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: name);
        int differed = 0;
        foreach (string kind in new[] { MemoryKind, FileKind })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sectionName = SectionName(name, kind);
            nint section = OpenFileMappingW(FileMapAllAccess, false, sectionName);
            if (section == 0)
            {
                truth.Write(TruthEventKind.Failure, status: $"The {kind} could not be opened ({Marshal.GetLastPInvokeError()}).", resourceName: sectionName);
                return 1;
            }

            truth.Write(TruthEventKind.ObjectOpened, status: kind, resourceName: sectionName);
            byte[] expected = Bytes(options, kind);
            byte[] read = new byte[options.Bytes];
            for (int round = 1; round <= options.Rounds; round++)
            {
                nint view = MapViewOfFile(section, FileMapAllAccess, 0, 0, (nuint)options.Bytes);
                if (view == 0)
                {
                    truth.Write(TruthEventKind.Failure, callId: round, status: $"A view of the {kind} could not be mapped.", resourceName: sectionName);
                    return 1;
                }

                truth.Write(TruthEventKind.ViewMapped, callId: round, declaredBytes: options.Bytes, status: kind, resourceName: sectionName);
                Marshal.Copy(view, read, 0, options.Bytes);
                bool same = read.AsSpan().SequenceEqual(expected);
                differed += same ? 0 : 1;
                _ = UnmapViewOfFile(view);

                // The bytes read through the view are the creator's when they all match what it wrote.
                truth.Write(TruthEventKind.ViewUnmapped, callId: round, completedBytes: same ? options.Bytes : 0, status: kind, resourceName: sectionName);
            }

            _ = CloseHandle(section);
            truth.Write(TruthEventKind.HandleClosed, status: kind, resourceName: sectionName);
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return differed == 0 ? 0 : 1;
    }

    /// <summary>A section's name: the run's name and the section's kind.</summary>
    private static string SectionName(string name, string kind) => name + (kind == MemoryKind ? "-memory" : "-file");

    // The seeded bytes the creator writes into a section, and the opener expects to read back: each kind's of its own.
    private static byte[] Bytes(SectionOptions options, string kind)
    {
        byte[] bytes = new byte[options.Bytes];
        new Random(options.Seed + (kind == MemoryKind ? 0 : 1)).NextBytes(bytes);
        return bytes;
    }

    private static Process StartChild(string executable, string verb, SectionOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--name", options.Name!,
            "--view-bytes", options.Bytes.ToString(CultureInfo.InvariantCulture),
            "--rounds", options.Rounds.ToString(CultureInfo.InvariantCulture),
            "--seed", options.Seed.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMappingW(nint file, nint attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenFileMappingW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(nint section, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
