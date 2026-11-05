using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;
using Microsoft.Win32.SafeHandles;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-MAIL-001 run: the seeded messages a client writes to a server's mailslot.</summary>
internal sealed record MailslotLoopbackOptions
{
    public const string ScenarioId = "FX-MAIL-001";

    public required string TruthDirectory { get; init; }

    /// <summary>The seed of the messages' sizes and bytes.</summary>
    public int Seed { get; init; } = 20_261_105;

    /// <summary>How many messages the client writes.</summary>
    public int Messages { get; init; } = 32;

    /// <summary>The largest message the client writes, in bytes; each is between one byte and this.</summary>
    public int MaximumBytes { get; init; } = 2048;

    /// <summary>How long the client waits between two messages.</summary>
    public int DelayMilliseconds { get; init; }

    /// <summary>The mailslot's name, which the coordinator chooses and gives both processes; null for the coordinator.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// FX-MAIL-001: a server makes a mailslot under a name of the run's own, and a client opens it by its name and writes seeded
/// messages to it, which the server reads one at a time. Each read asks for more than any message holds, so the size a read
/// asked for and the size it read differ, as a mailslot read returns one whole message. Each process logs the mailslot it
/// made or opened, every message it wrote or read with the bytes asked for and moved, and the handle it closed, by the
/// mailslot's name. Neither reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class MailslotLoopbackScenario
{
    // What a read asks for: more than any message the client writes.
    private const int ReadBytes = 65_536;

    // How long a read waits for a message before the server gives up, so a client that failed leaves no server waiting.
    private const uint ReadTimeoutMilliseconds = 30_000;

    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareRead = 0x0000_0001;
    private const uint OpenExisting = 3;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Starts the server, then the client once the server's mailslot exists, and waits for both.</summary>
    public static async Task<int> RunCoordinatorAsync(MailslotLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // A name of its own each run, so no other run's mailslot or any other program's shares it.
        string name = $@"\\.\mailslot\InterCat\{MailslotLoopbackOptions.ScenarioId}-{Guid.NewGuid():N}";
        using Process server = StartChild(executable, "mailslot-loopback-server", options with { Name = name });
        if (await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "created")
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server exited before it made its mailslot.").ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "mailslot-loopback-client", options with { Name = name });
        await Task.WhenAll(server.WaitForExitAsync(cancellationToken), client.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        var summary = new
        {
            scenarioId = MailslotLoopbackOptions.ScenarioId,
            options.Seed,
            options.Messages,
            options.MaximumBytes,
            options.DelayMilliseconds,
            readBytes = ReadBytes,
            name,
            coordinatorProcessId = Environment.ProcessId,
            serverProcessId = server.Id,
            clientProcessId = client.Id,
            serverExitCode = server.ExitCode,
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return server.ExitCode != 0 || client.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>The server: makes the mailslot, says so, and reads each message the client writes.</summary>
    public static async Task<int> RunServerAsync(MailslotLoopbackOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The server takes the mailslot's name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-server.jsonl"), MailslotLoopbackOptions.ScenarioId, "server");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: name);

        using SafeFileHandle mailslot = CreateMailslot(name, 0, ReadTimeoutMilliseconds, 0);
        if (mailslot.IsInvalid)
        {
            truth.Write(TruthEventKind.Failure, status: new Win32Exception(Marshal.GetLastPInvokeError()).Message, resourceName: name);
            return 1;
        }

        truth.Write(TruthEventKind.ObjectCreated, status: "mailslot", resourceName: name);
        Console.WriteLine("created");
        Console.Out.Flush();

        byte[] buffer = new byte[ReadBytes];
        for (int message = 1; message <= options.Messages; message++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadFile(mailslot, buffer, ReadBytes, out uint read, 0))
            {
                truth.Write(TruthEventKind.Failure, callId: message, status: new Win32Exception(Marshal.GetLastPInvokeError()).Message, resourceName: name);
                return 1;
            }

            truth.Write(TruthEventKind.MessageReceived, callId: message, declaredBytes: ReadBytes, completedBytes: read, resourceName: name);
        }

        mailslot.Dispose();
        truth.Write(TruthEventKind.HandleClosed, status: "mailslot", resourceName: name);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>The client: opens the server's mailslot by its name and writes the seeded messages to it.</summary>
    public static async Task<int> RunClientAsync(MailslotLoopbackOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The client takes the mailslot's name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-client.jsonl"), MailslotLoopbackOptions.ScenarioId, "client");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: name);

        using SafeFileHandle mailslot = CreateFile(name, GenericWrite, FileShareRead, 0, OpenExisting, 0, 0);
        if (mailslot.IsInvalid)
        {
            truth.Write(TruthEventKind.Failure, status: new Win32Exception(Marshal.GetLastPInvokeError()).Message, resourceName: name);
            return 1;
        }

        truth.Write(TruthEventKind.ObjectOpened, status: "mailslot", resourceName: name);
        var random = new Random(options.Seed);
        for (int message = 1; message <= options.Messages; message++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = new byte[random.Next(1, options.MaximumBytes + 1)];
            random.NextBytes(bytes);
            if (!WriteFile(mailslot, bytes, (uint)bytes.Length, out uint written, 0))
            {
                truth.Write(TruthEventKind.Failure, callId: message, status: new Win32Exception(Marshal.GetLastPInvokeError()).Message, resourceName: name);
                return 1;
            }

            truth.Write(TruthEventKind.MessageSent, callId: message, declaredBytes: bytes.Length, completedBytes: written, resourceName: name);
            if (options.DelayMilliseconds > 0)
            {
                await Task.Delay(options.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        mailslot.Dispose();
        truth.Write(TruthEventKind.HandleClosed, status: "mailslot", resourceName: name);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    private static Process StartChild(string executable, string verb, MailslotLoopbackOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--name", options.Name!,
            "--seed", options.Seed.ToString(CultureInfo.InvariantCulture),
            "--messages", options.Messages.ToString(CultureInfo.InvariantCulture),
            "--bytes", options.MaximumBytes.ToString(CultureInfo.InvariantCulture),
            "--delay", options.DelayMilliseconds.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMailslotW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateMailslot(string name, uint maximumMessageSize, uint readTimeout, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint securityAttributes, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle file, [Out] byte[] buffer, uint toRead, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, byte[] buffer, uint toWrite, out uint written, nint overlapped);
}
