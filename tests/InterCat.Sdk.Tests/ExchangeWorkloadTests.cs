using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace InterCat.Sdk.Tests;

/// <summary>
/// The exchange workload the alignment by shared operations is qualified with (ADR-108): two processes starting operations
/// under each other's through the SDK, each logging the operations it started with the one each was started under.
/// </summary>
public sealed class ExchangeWorkloadTests : IDisposable
{
    private readonly string truth = Path.Combine(Path.GetTempPath(), "InterCat.Sdk.Tests.Exchange", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(truth, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "§15: the exchange's server serves each of the client's requests under it, and the client answers each serving under it, each side logging the operations it started")]
    public async Task EachSideStartsUnderTheOthers()
    {
        using Process server = Start("sdk-exchange-server", "--truth", truth);
        string? listening = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(listening);
        Assert.StartsWith("listening ", listening, StringComparison.Ordinal);
        using Process client = Start("sdk-exchange-client", "--truth", truth, "--endpoint", listening["listening ".Length..],
            "--rounds", "5", "--pause-ms", "0");
        await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal((0, 0), (client.ExitCode, server.ExitCode));

        // The client started a request and an answer each round, the request under nothing; the server a serving each round,
        // under that round's request; and the client's answer under that round's serving.
        (Guid Id, Guid? Parent, string Name)[] clientStarted = Operations("client");
        (Guid Id, Guid? Parent, string Name)[] serverStarted = Operations("server");
        (Guid Id, Guid? Parent, string Name)[] requests = [.. clientStarted.Where(started => started.Name.StartsWith("request-", StringComparison.Ordinal))];
        (Guid Id, Guid? Parent, string Name)[] answers = [.. clientStarted.Where(started => started.Name.StartsWith("answer-", StringComparison.Ordinal))];
        Assert.Equal((10, 5, 5, 5), (clientStarted.Length, requests.Length, answers.Length, serverStarted.Length));
        Assert.All(requests, request => Assert.Null(request.Parent));
        Assert.Equal(requests.Select(request => (Guid?)request.Id), serverStarted.Select(serving => serving.Parent));
        Assert.Equal(serverStarted.Select(serving => (Guid?)serving.Id), answers.Select(answer => answer.Parent));
    }

    /// <summary>The operations a side of the exchange logged it started, in order.</summary>
    private (Guid Id, Guid? Parent, string Name)[] Operations(string side)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, $"operations-{side}.json")));
        return [.. document.RootElement.GetProperty("operations").EnumerateArray().Select(started => (
            started.GetProperty("id").GetGuid(),
            started.GetProperty("parent").ValueKind == JsonValueKind.Null ? (Guid?)null : started.GetProperty("parent").GetGuid(),
            started.GetProperty("name").GetString()!))];
    }

    /// <summary>A workload verb in a process of its own, its output read by the test.</summary>
    private static Process Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "InterCat.TestWorkloads.dll"));
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
    }
}
