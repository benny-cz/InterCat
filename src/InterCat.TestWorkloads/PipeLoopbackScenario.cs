using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one seeded named-pipe run. The same seed reproduces the same declared sizes (I14).</summary>
internal sealed record PipeLoopbackOptions
{
    public const string ScenarioId = "FX-PIPE-001";

    public required string TruthDirectory { get; init; }
    public int Seed { get; init; } = 20_260_921;
    public int Messages { get; init; } = 8;
    public int MaximumMessageBytes { get; init; } = 4_096;
    public int MinimumMessageBytes { get; init; } = 64;
    public int InterMessageDelayMilliseconds { get; init; } = 15;

    /// <summary>
    /// The message whose server read uses a deliberately small buffer, so a validated completion reports
    /// fewer bytes than were requested (section 21.1, row 2).
    /// </summary>
    public int PartialReadMessageIndex { get; init; } = 2;

    public int PartialReadBufferBytes { get; init; } = 1_024;

    public string PipeName { get; init; } = string.Empty;
}

/// <summary>
/// A two-process named-pipe workload in message mode with an independent truth log per process
/// (IC-005, section 13.1 scenario 3). It uses ordinary pipe APIs only: nothing here observes ETW.
/// Message mode is a Windows pipe feature, so the scenario is Windows-only by contract.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeLoopbackScenario
{
    private const int AckBytes = 8;

    private static readonly JsonSerializerOptions SummaryOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunCoordinatorAsync(PipeLoopbackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        string pipeName = string.Create(
            CultureInfo.InvariantCulture,
            $"intercat-fx-pipe-{System.Environment.ProcessId}-{Random.Shared.Next(100_000, 999_999)}");
        PipeLoopbackOptions resolved = options with { PipeName = pipeName };

        using Process server = StartChild(executable, "pipe-loopback-server", resolved, redirectStdout: true);
        string? handshake = await server.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (handshake is null)
        {
            await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The server process exited before it reported readiness.")
                .ConfigureAwait(false);
            return 1;
        }

        using Process client = StartChild(executable, "pipe-loopback-client", resolved, redirectStdout: false);
        await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await server.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            scenarioId = PipeLoopbackOptions.ScenarioId,
            resolved.Seed,
            resolved.Messages,
            resolved.MinimumMessageBytes,
            resolved.MaximumMessageBytes,
            resolved.PartialReadMessageIndex,
            resolved.PartialReadBufferBytes,
            pipeName,
            pipePath = @"\\.\pipe\" + pipeName,
            serverProcessId = server.Id,
            clientProcessId = client.Id,
            serverExitCode = server.ExitCode,
            clientExitCode = client.ExitCode,
        };
        await File.WriteAllTextAsync(
            Path.Combine(resolved.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions),
            cancellationToken).ConfigureAwait(false);

        return server.ExitCode != 0 || client.ExitCode != 0 ? 1 : 0;
    }

    public static async Task<int> RunServerAsync(PipeLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-server.jsonl"),
            PipeLoopbackOptions.ScenarioId,
            "server");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: PipePath(options.PipeName));

        using var server = new NamedPipeServerStream(
            options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 2,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous);
        truth.Write(TruthEventKind.ListenerBound, resourceName: PipePath(options.PipeName));
        await Console.Out.WriteLineAsync("{\"ready\":true}").ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);

        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ConnectionAccepted, callId: 0, resourceName: PipePath(options.PipeName));

        byte[] full = new byte[options.MaximumMessageBytes + 4];
        byte[] ack = new byte[AckBytes];
        for (int message = 0; message < options.Messages; message++)
        {
            bool partial = message == options.PartialReadMessageIndex;
            int bufferSize = partial ? options.PartialReadBufferBytes : full.Length;
            int read = await server.ReadAsync(full.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                truth.Write(
                    TruthEventKind.Failure,
                    callId: message,
                    resourceName: PipePath(options.PipeName),
                    status: "the peer closed before the message arrived");
                break;
            }

            // In message mode a short buffer returns a partial message; the rest of it is drained so the
            // stream stays aligned. The truth log keeps requested and completed sizes distinct.
            int drained = 0;
            while (!server.IsMessageComplete)
            {
                int extra = await server.ReadAsync(full.AsMemory(0, full.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (extra == 0)
                {
                    break;
                }

                drained += extra;
            }

            truth.Write(
                TruthEventKind.MessageReceived,
                callId: message,
                resourceName: PipePath(options.PipeName),
                declaredBytes: bufferSize,
                completedBytes: read,
                status: partial ? "partial read with a short buffer" : "complete");

            if (drained > 0)
            {
                truth.Write(
                    TruthEventKind.MessageReceived,
                    callId: message,
                    resourceName: PipePath(options.PipeName),
                    declaredBytes: full.Length,
                    completedBytes: drained,
                    status: "remainder of a partially read message");
            }

            BinaryPrimitives.WriteInt64LittleEndian(ack, message);
            await server.WriteAsync(ack, cancellationToken).ConfigureAwait(false);
            truth.Write(
                TruthEventKind.MessageSent,
                callId: message,
                resourceName: PipePath(options.PipeName),
                declaredBytes: AckBytes,
                completedBytes: AckBytes);
        }

        server.Disconnect();
        truth.Write(TruthEventKind.ConnectionClosed, callId: 0, resourceName: PipePath(options.PipeName));
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    public static async Task<int> RunClientAsync(PipeLoopbackOptions options, CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(
            Path.Combine(options.TruthDirectory, "truth-client.jsonl"),
            PipeLoopbackOptions.ScenarioId,
            "client");
        truth.Write(TruthEventKind.ProcessStarted, resourceName: PipePath(options.PipeName));

        using var client = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        client.ReadMode = PipeTransmissionMode.Message;
        truth.Write(TruthEventKind.ConnectionEstablished, callId: 0, resourceName: PipePath(options.PipeName));

        var sizes = new Random(options.Seed);
        byte[] ack = new byte[AckBytes];
        for (int message = 0; message < options.Messages; message++)
        {
            // The designated message is deliberately larger than the server's short buffer, so the server
            // reads it partially and requested and completed sizes must stay distinct (section 21.1).
            int payload = message == options.PartialReadMessageIndex
                ? options.MaximumMessageBytes
                : sizes.Next(options.MinimumMessageBytes, options.MaximumMessageBytes + 1);
            byte[] buffer = new byte[payload];
            for (int index = 0; index < buffer.Length; index++)
            {
                buffer[index] = (byte)(index % 251);
            }

            await client.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            truth.Write(
                TruthEventKind.MessageSent,
                callId: message,
                resourceName: PipePath(options.PipeName),
                declaredBytes: payload,
                completedBytes: payload,
                status: "complete");

            int read = await client.ReadAsync(ack.AsMemory(), cancellationToken).ConfigureAwait(false);
            truth.Write(
                TruthEventKind.MessageReceived,
                callId: message,
                resourceName: PipePath(options.PipeName),
                declaredBytes: AckBytes,
                completedBytes: read);

            if (options.InterMessageDelayMilliseconds > 0)
            {
                await Task.Delay(options.InterMessageDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        truth.Write(TruthEventKind.ConnectionClosed, callId: 0, resourceName: PipePath(options.PipeName));
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    private static string PipePath(string pipeName) => @"\\.\pipe\" + pipeName;

    private static Process StartChild(
        string executable,
        string verb,
        PipeLoopbackOptions options,
        bool redirectStdout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
        };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(options.TruthDirectory);
        start.ArgumentList.Add("--seed");
        start.ArgumentList.Add(options.Seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--messages");
        start.ArgumentList.Add(options.Messages.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--bytes");
        start.ArgumentList.Add(options.MaximumMessageBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(options.PipeName);

        return Process.Start(start)
            ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }
}
