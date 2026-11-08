using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Sdk;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one side of a shared-operations exchange: the pipe, the rounds and the client's pause between them.</summary>
internal sealed record SdkExchangeOptions
{
    public const string ScenarioId = "SDK-EXCHANGE";

    public required string TruthDirectory { get; init; }

    /// <summary>The pipe's name, which the server chooses and says, and the client takes from --endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>How many rounds the client runs: its request, the server's serving under it, and its answer under that.</summary>
    public int Rounds { get; init; } = 200;

    /// <summary>The client's pause between rounds, so the rounds spread over the captures.</summary>
    public int PauseMilliseconds { get; init; } = 5;

    /// <summary>Whether the client waits for a line on its standard input before it connects, so captures can name both first.</summary>
    public bool WaitForStart { get; init; }
}

/// <summary>
/// Two processes starting operations under each other's through InterCat's cooperative SDK (ADR-108): each round the client
/// starts a request and writes its identity over a named pipe, the server starts its serving under it and writes that
/// identity back, and the client starts its answer under the serving - so each process starts an operation under one the
/// other started, each start after its parent's by the SDK's own word, since the call that says a start makes its identity.
/// Each side writes the operations it started - identity, parent and name, never a time - to operations-<side>.json. It
/// reads no InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SdkExchangeScenario
{
    private const int IdentityBytes = 16;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The server: makes the pipe under a name of the run's own, says it, and serves one connection's rounds.</summary>
    public static async Task<int> RunServerAsync(SdkExchangeOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-server.jsonl"), SdkExchangeOptions.ScenarioId, "server");
        truth.Write(TruthEventKind.ProcessStarted);
        string name = $"InterCat-{SdkExchangeOptions.ScenarioId}-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await Console.Out.WriteLineAsync($"listening {name}").ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ConnectionAccepted, resourceName: $@"\\.\pipe\{name}");
        byte[] request = new byte[IdentityBytes];
        var started = new List<StartedOperation>();
        while (true)
        {
            int read = await pipe.ReadAtLeastAsync(request, IdentityBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read != IdentityBytes)
            {
                truth.Write(TruthEventKind.Failure, status: "the client's request was cut short");
                return 1;
            }

            // The serving is started under the request whose identity the client wrote, and its own identity goes back.
            var parent = new Guid(request);
            InterCatOperation serving = InterCatOperation.Start($"serve-{started.Count + 1}", parent);
            await pipe.WriteAsync(serving.Id.ToByteArray(), cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            serving.Complete();
            started.Add(new(serving.Id, parent, serving.Name));
        }

        truth.Write(TruthEventKind.ConnectionClosed, resourceName: $@"\\.\pipe\{name}");
        await WriteAsync(options, "server", started, cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>The client: connects once released, and runs the rounds, its answer each started under the server's serving.</summary>
    public static async Task<int> RunClientAsync(SdkExchangeOptions options, CancellationToken cancellationToken)
    {
        string name = options.Endpoint ?? throw new InvalidOperationException("The client takes the pipe's name from --endpoint.");
        Directory.CreateDirectory(options.TruthDirectory);
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-client.jsonl"), SdkExchangeOptions.ScenarioId, "client");
        truth.Write(TruthEventKind.ProcessStarted);
        if (options.WaitForStart)
        {
            _ = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ConnectionEstablished, resourceName: $@"\\.\pipe\{name}");
        byte[] serving = new byte[IdentityBytes];
        var started = new List<StartedOperation>();
        for (int round = 1; round <= options.Rounds; round++)
        {
            InterCatOperation request = InterCatOperation.Start($"request-{round}");
            await pipe.WriteAsync(request.Id.ToByteArray(), cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            await pipe.ReadExactlyAsync(serving, cancellationToken).ConfigureAwait(false);

            // The answer is started under the serving whose identity the server wrote back.
            var parent = new Guid(serving);
            InterCatOperation answer = InterCatOperation.Start($"answer-{round}", parent);
            answer.Complete();
            request.Complete();
            started.Add(new(request.Id, null, request.Name));
            started.Add(new(answer.Id, parent, answer.Name));
            if (options.PauseMilliseconds > 0)
            {
                await Task.Delay(options.PauseMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        truth.Write(TruthEventKind.ConnectionClosed, resourceName: $@"\\.\pipe\{name}");
        await WriteAsync(options, "client", started, cancellationToken).ConfigureAwait(false);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    private static Task WriteAsync(SdkExchangeOptions options, string side, List<StartedOperation> started, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, $"operations-{side}.json"),
            JsonSerializer.Serialize(new { scenarioId = SdkExchangeOptions.ScenarioId, side, operations = started }, SummaryOptions),
            cancellationToken);

    /// <summary>An operation a side started: its identity, the operation it was started under where one, and its name.</summary>
    private sealed record StartedOperation(Guid Id, Guid? Parent, string Name);
}
