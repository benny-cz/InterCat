using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>
/// Parameters of one FX-WMSG-001 run - the messages a sender sends and posts to a receiver's window - or of an FX-WMSG-002
/// run, the same messages to each of the receiver's windows made one after another, or of an FX-WMSG-003 run, the same
/// messages each taking the receiver a while.
/// </summary>
internal sealed record WindowMessageOptions
{
    /// <summary>
    /// The fixture a run is: FX-WMSG-002 where the receiver makes its windows in generations, FX-WMSG-003 where it takes its
    /// time over each message.
    /// </summary>
    public string ScenarioId => HandleMilliseconds > 0 ? "FX-WMSG-003" : Generations > 1 ? "FX-WMSG-002" : "FX-WMSG-001";

    public required string TruthDirectory { get; init; }

    /// <summary>How many windows the receiver makes one after another, each closed by the sender before the next is made.</summary>
    public int Generations { get; init; } = 1;

    /// <summary>
    /// How long the receiver's window procedure takes over each copy and each posted message, in milliseconds, so a sent
    /// message is in flight that long and posted ones wait their turn in its queue: long enough for a capture's start and
    /// stop to cut them.
    /// </summary>
    public int HandleMilliseconds { get; init; }

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
/// it made or found, by its class. FX-WMSG-002 is the same over windows made one after another: the receiver makes each
/// generation's window under a name of its own once the last is destroyed, and the sender finds each by its class and name and
/// sends and posts it the messages, numbered on from the last window's, before its close. FX-WMSG-003 is FX-WMSG-001 with a
/// receiver that takes its time over each copy and each posted message, so each sent message is in flight that long and the
/// posted ones wait in its queue: a capture begun or ended meanwhile cuts them. Neither reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowMessageScenario
{
    private const uint Destroy = 0x0002;
    private const uint Close = 0x0010;
    private const uint CopyData = 0x004A;

    /// <summary>The application's first message number, WM_APP; the posted messages are the next.</summary>
    private const uint ApplicationMessage = 0x8001;

    /// <summary>ERROR_NOT_ENOUGH_QUOTA: a post to a thread whose queue already holds as many posted messages as it may.</summary>
    private const int NotEnoughQuota = 1816;

    // The parent of a message-only window, HWND_MESSAGE.
    private static readonly nint MessageOnly = -3;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // The receiver's truth log and counts, the name of the window it pumps and how long it takes over a message, which its
    // window procedure, called by the system on its pump, reaches.
    private static TruthLog? receiverTruth;
    private static string receiverWindow = string.Empty;
    private static int receiverHandling;
    private static int copiesReceived;
    private static int postsReceived;

    /// <summary>Starts the receiver, then the sender once the receiver's first window exists, and waits for both.</summary>
    public static async Task<int> RunCoordinatorAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // A class of its own each run, so no other run's window or any other program's shares it.
        string className = $"InterCat.{options.ScenarioId}.{Guid.NewGuid():N}";
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
            scenarioId = options.ScenarioId,
            options.Seed,
            options.Generations,
            handleMilliseconds = options.HandleMilliseconds,
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

    /// <summary>
    /// The receiver: makes each generation's message-only window in turn, says so once the first exists, and pumps each one's
    /// messages until the sender's close destroys it.
    /// </summary>
    public static async Task<int> RunReceiverAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        string className = options.ClassName ?? throw new InvalidOperationException("The receiver takes its window's class from --class.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-receiver.jsonl"), options.ScenarioId, "receiver");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: className);
        receiverTruth = truth;
        receiverHandling = options.HandleMilliseconds;
        if (Pump(truth, className, options.Generations, cancellationToken) is { } failure)
        {
            truth.Write(TruthEventKind.Failure, status: failure, resourceName: className);
            return 1;
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return copiesReceived == options.CopyData * options.Generations && postsReceived == options.Posted * options.Generations ? 0 : 1;
    }

    // Registers the class, then makes each generation's message-only window once the last is destroyed and pumps its messages
    // on this thread, which owns it, until the close's destruction posts the quit; null once the last window's has, or why a
    // window could not be made.
    private static unsafe string? Pump(TruthLog truth, string className, int generations, CancellationToken cancellationToken)
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

            for (int generation = 1; generation <= generations; generation++)
            {
                string windowName = WindowName(className, generation, generations);
                receiverWindow = windowName;
                fixed (char* title = windowName)
                {
                    if (CreateWindowExW(0, name, title, 0, 0, 0, 0, 0, MessageOnly, 0, instance, 0) == 0)
                    {
                        return "The window could not be made.";
                    }
                }

                truth.Write(TruthEventKind.ObjectCreated, status: "window", resourceName: windowName);
                if (generation == 1)
                {
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                }

                Message message;
                while (GetMessageW(&message, 0, 0, 0) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = TranslateMessage(&message);
                    _ = DispatchMessageW(&message);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The sender: finds each of the receiver's windows by its class and name once it is made, sends it the copies, posts it
    /// the messages, then its close.
    /// </summary>
    public static async Task<int> RunSenderAsync(WindowMessageOptions options, CancellationToken cancellationToken)
    {
        string className = options.ClassName ?? throw new InvalidOperationException("The sender takes the window's class from --class.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-sender.jsonl"), options.ScenarioId, "sender");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: className);
        var random = new Random(options.Seed);
        for (int generation = 1; generation <= options.Generations; generation++)
        {
            string windowName = WindowName(className, generation, options.Generations);
            nint window = Find(className, windowName, cancellationToken);
            if (window == 0)
            {
                truth.Write(TruthEventKind.Failure, status: "The receiver's window was not found.", resourceName: windowName);
                return 1;
            }

            truth.Write(TruthEventKind.ObjectOpened, status: "window", resourceName: windowName);

            // Each window's messages numbered on from the last window's, so no two of the run's messages share a number.
            for (int copy = (generation - 1) * options.CopyData + 1; copy <= generation * options.CopyData; copy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes = new byte[random.Next(1, options.MaximumBytes + 1)];
                random.NextBytes(bytes);

                // A sent message returns once the receiver's procedure has answered it, its copy taken.
                nint answered = SendCopy(window, copy, bytes);
                truth.Write(TruthEventKind.MessageSent, callId: copy, declaredBytes: bytes.Length, completedBytes: bytes.Length,
                    status: answered == 1 ? "WM_COPYDATA" : "WM_COPYDATA unanswered", resourceName: windowName);
            }

            for (int post = (generation - 1) * options.Posted + 1; post <= generation * options.Posted; post++)
            {
                if (!Post(window, ApplicationMessage, post))
                {
                    truth.Write(TruthEventKind.Failure, callId: post, status: "A message could not be posted.", resourceName: windowName);

                    // The receiver pumps until its window is closed: a run that failed closes it, so it ends rather than hangs.
                    _ = Post(window, Close, 0);
                    return 1;
                }

                truth.Write(TruthEventKind.MessageSent, callId: post, status: "posted", resourceName: windowName);
            }

            // Last, a close posted behind them, which the pump reaches after every posted message; the receiver makes the
            // next generation's window once this one is destroyed.
            if (!Post(window, Close, 0))
            {
                truth.Write(TruthEventKind.Failure, status: "The window's close could not be posted.", resourceName: windowName);
                return 1;
            }

            truth.Write(TruthEventKind.HandleClosed, status: "window", resourceName: windowName);
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    // Posts one message, waiting while the receiver's queue is full - a thread's queue holds 10,000 posted messages at most,
    // and a sender posting unpaced outruns a receiver the machine's other load slows - so the message waits for room rather
    // than being lost; false if it could not be posted within a minute, or for any other reason.
    private static bool Post(nint window, uint message, nint wParam)
    {
        var waited = Stopwatch.StartNew();
        while (!PostMessageW(window, message, wParam, 0))
        {
            if (Marshal.GetLastPInvokeError() != NotEnoughQuota || waited.Elapsed > TimeSpan.FromMinutes(1))
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    // A window's name: its class's for a run of one window, as FX-WMSG-001's was, and the class's numbered by its generation
    // for a run of several, so a generation's window is found only once the receiver has made it.
    private static string WindowName(string className, int generation, int generations) =>
        generations == 1 ? className : string.Create(CultureInfo.InvariantCulture, $"{className}/{generation:D2}");

    // The receiver's message-only window of the name given, found among the message-only windows of its class once the
    // receiver has made it, which it does once the last generation's is destroyed; zero if it is not made within a minute.
    private static unsafe nint Find(string className, string windowName, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        fixed (char* name = className)
        fixed (char* title = windowName)
        {
            while (true)
            {
                nint window = FindWindowExW(MessageOnly, 0, name, title);
                if (window != 0 || waited.Elapsed > TimeSpan.FromMinutes(1))
                {
                    return window;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }
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
                    status: "WM_COPYDATA", resourceName: receiverWindow);
                Handle();
                return 1;
            }

            case ApplicationMessage:
                postsReceived++;
                receiverTruth?.Write(TruthEventKind.MessageReceived, callId: wParam, status: "posted", resourceName: receiverWindow);
                Handle();
                return 0;

            case Close:
                receiverTruth?.Write(TruthEventKind.HandleClosed, status: "window", resourceName: receiverWindow);
                _ = DestroyWindow(window);
                return 0;

            // The window's destruction ends its pump, after which the receiver makes the next generation's window, if any.
            case Destroy:
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    // The receiver's time over a message it has logged: a copy's sender waits that long for its answer, and the next posted
    // message that long in the queue.
    private static void Handle()
    {
        if (receiverHandling > 0)
        {
            Thread.Sleep(receiverHandling);
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
            "--generations", options.Generations.ToString(CultureInfo.InvariantCulture),
            "--handle-ms", options.HandleMilliseconds.ToString(CultureInfo.InvariantCulture),
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
