using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-WMSG-001 run: the messages a sender sends and posts to a receiver's window.</summary>
internal sealed record WindowMessageOptions
{
    public const string ScenarioId = "FX-WMSG-001";

    public required string TruthDirectory { get; init; }

    /// <summary>The seed of the copied data's sizes and bytes.</summary>
    public int Seed { get; init; } = 20_261_105;

    /// <summary>How many WM_COPYDATA messages the sender sends.</summary>
    public int CopyData { get; init; } = 32;

    /// <summary>How many messages the sender posts.</summary>
    public int Posted { get; init; } = 32;

    /// <summary>The largest WM_COPYDATA payload, in bytes; each is between one byte and this.</summary>
    public int MaximumBytes { get; init; } = 2048;

    /// <summary>The receiver's window class, which the coordinator chooses and gives both processes; null for the coordinator.</summary>
    public string? ClassName { get; init; }
}

/// <summary>
/// FX-WMSG-001: a receiver makes a message-only window of a class of the run's own and pumps its messages; a sender finds it
/// by its class and sends it seeded WM_COPYDATA messages, each a payload the system copies into the receiver, then posts it
/// numbered messages of the application's range, and last posts it a close, which its pump reaches after them. Each process
/// logs every message it sent or posted and every one its window received, by number and with a copy's size, and the window
/// it made or found, by its class. Neither reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowMessageScenario
{
    private const uint Destroy = 0x0002;
    private const uint Close = 0x0010;
    private const uint CopyData = 0x004A;

    /// <summary>The application's first message number, WM_APP; the posted messages are the next.</summary>
    private const uint ApplicationMessage = 0x8001;

    // The parent of a message-only window, HWND_MESSAGE.
    private static readonly nint MessageOnly = -3;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // The receiver's truth log and counts, which its window procedure, called by the system on its pump, reaches.
    private static TruthLog? receiverTruth;
    private static string receiverClass = string.Empty;
    private static int copiesReceived;
    private static int postsReceived;

    /// <summary>Starts the receiver, then the sender once the receiver's window exists, and waits for both.</summary>
    public static async Task<int> RunCoordinatorAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // A class of its own each run, so no other run's window or any other program's shares it.
        string className = $"InterCat.{WindowMessageOptions.ScenarioId}.{Guid.NewGuid():N}";
        using Process receiver = StartChild(executable, "window-message-receiver", options with { ClassName = className });
        if (await receiver.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "ready")
        {
            await receiver.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The receiver exited before it made its window.").ConfigureAwait(false);
            return 1;
        }

        using Process sender = StartChild(executable, "window-message-sender", options with { ClassName = className });
        await Task.WhenAll(receiver.WaitForExitAsync(cancellationToken), sender.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        var summary = new
        {
            scenarioId = WindowMessageOptions.ScenarioId,
            options.Seed,
            options.CopyData,
            options.Posted,
            options.MaximumBytes,
            className,
            copyDataMessage = CopyData,
            postedMessage = ApplicationMessage,
            coordinatorProcessId = Environment.ProcessId,
            receiverProcessId = receiver.Id,
            senderProcessId = sender.Id,
            receiverExitCode = receiver.ExitCode,
            senderExitCode = sender.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return receiver.ExitCode != 0 || sender.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>The receiver: makes the message-only window, says so, and pumps its messages until the sender's close.</summary>
    public static async Task<int> RunReceiverAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        string className = options.ClassName ?? throw new InvalidOperationException("The receiver takes its window's class from --class.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-receiver.jsonl"), WindowMessageOptions.ScenarioId, "receiver");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: className);
        receiverTruth = truth;
        receiverClass = className;
        if (Pump(truth, className, cancellationToken) is { } failure)
        {
            truth.Write(TruthEventKind.Failure, status: failure, resourceName: className);
            return 1;
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return copiesReceived == options.CopyData && postsReceived == options.Posted ? 0 : 1;
    }

    // Makes the message-only window and pumps its messages on this thread, which owns it, until the close's destruction
    // posts the quit; null once it has, or why the window could not be made.
    private static unsafe string? Pump(TruthLog truth, string className, CancellationToken cancellationToken)
    {
        nint instance = GetModuleHandleW(null);
        fixed (char* name = className)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                Procedure = &WindowProcedure,
                Instance = instance,
                ClassName = name,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                return "The window class could not be registered.";
            }

            if (CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, MessageOnly, 0, instance, 0) == 0)
            {
                return "The window could not be made.";
            }

            truth.Write(TruthEventKind.ObjectCreated, status: "window", resourceName: className);
            Console.WriteLine("ready");
            Console.Out.Flush();
            Message message;
            while (GetMessageW(&message, 0, 0, 0) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = TranslateMessage(&message);
                _ = DispatchMessageW(&message);
            }
        }

        return null;
    }

    /// <summary>The sender: finds the receiver's window by its class, sends it the copies, posts it the messages, then its close.</summary>
    public static async Task<int> RunSenderAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        string className = options.ClassName ?? throw new InvalidOperationException("The sender takes the window's class from --class.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-sender.jsonl"), WindowMessageOptions.ScenarioId, "sender");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: className);
        nint window = Find(className);
        if (window == 0)
        {
            truth.Write(TruthEventKind.Failure, status: "The receiver's window was not found.", resourceName: className);
            return 1;
        }

        truth.Write(TruthEventKind.ObjectOpened, status: "window", resourceName: className);
        var random = new Random(options.Seed);
        for (int copy = 1; copy <= options.CopyData; copy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = new byte[random.Next(1, options.MaximumBytes + 1)];
            random.NextBytes(bytes);

            // A sent message returns once the receiver's procedure has answered it, its copy taken.
            nint answered = SendCopy(window, copy, bytes);
            truth.Write(TruthEventKind.MessageSent, callId: copy, declaredBytes: bytes.Length, completedBytes: bytes.Length,
                status: answered == 1 ? "WM_COPYDATA" : "WM_COPYDATA unanswered", resourceName: className);
        }

        for (int post = 1; post <= options.Posted; post++)
        {
            if (!PostMessageW(window, ApplicationMessage, post, 0))
            {
                truth.Write(TruthEventKind.Failure, callId: post, status: "A message could not be posted.", resourceName: className);
                return 1;
            }

            truth.Write(TruthEventKind.MessageSent, callId: post, status: "posted", resourceName: className);
        }

        // Last, a close posted behind them, which the pump reaches after every posted message.
        _ = PostMessageW(window, Close, 0, 0);
        truth.Write(TruthEventKind.HandleClosed, status: "window", resourceName: className);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    // The receiver's message-only window, found by its class among the message-only windows.
    private static unsafe nint Find(string className)
    {
        fixed (char* name = className)
        {
            return FindWindowExW(MessageOnly, 0, name, null);
        }
    }

    // Sends one WM_COPYDATA of the bytes given, numbered by its data field, and returns the receiver's answer.
    private static unsafe nint SendCopy(nint window, int copy, byte[] bytes)
    {
        fixed (byte* data = bytes)
        {
            var copied = new CopyDataStruct { Data = (nuint)copy, Bytes = (uint)bytes.Length, Pointer = data };
            return SendMessageW(window, CopyData, 0, (nint)(&copied));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case CopyData:
            {
                // The system's copy of the sender's payload, valid while this message is answered.
                var copied = (CopyDataStruct*)lParam;
                copiesReceived++;
                receiverTruth?.Write(TruthEventKind.MessageReceived, callId: (long)copied->Data, completedBytes: copied->Bytes,
                    status: "WM_COPYDATA", resourceName: receiverClass);
                return 1;
            }

            case ApplicationMessage:
                postsReceived++;
                receiverTruth?.Write(TruthEventKind.MessageReceived, callId: wParam, status: "posted", resourceName: receiverClass);
                return 0;

            case Close:
                receiverTruth?.Write(TruthEventKind.HandleClosed, status: "window", resourceName: receiverClass);
                _ = DestroyWindow(window);
                return 0;

            case Destroy:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private static Process StartChild(string executable, string verb, WindowMessageOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--class", options.ClassName!,
            "--seed", options.Seed.ToString(CultureInfo.InvariantCulture),
            "--copies", options.CopyData.ToString(CultureInfo.InvariantCulture),
            "--posts", options.Posted.ToString(CultureInfo.InvariantCulture),
            "--bytes", options.MaximumBytes.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    /// <summary>WNDCLASSEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    /// <summary>COPYDATASTRUCT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CopyDataStruct
    {
        public nuint Data;
        public uint Bytes;
        public void* Pointer;
    }

    /// <summary>MSG.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Number;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? module);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial nint CreateWindowExW(uint extendedStyle, char* className, char* windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial nint FindWindowExW(nint parent, nint after, char* className, char* windowName);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetMessageW(Message* message, nint window, uint first, uint last);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool TranslateMessage(Message* message);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint DispatchMessageW(Message* message);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);
}
