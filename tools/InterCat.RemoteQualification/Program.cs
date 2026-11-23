using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterCat.Remote;
using InterCat.Storage;

// M11's overhead, measured (the M11 commitment review's "Expected overhead"): what an idle agent holds, what a control
// lease's renewal costs, and how fast a capture's evidence is fetched. `overhead` runs icat itself as an agent on loopback
// over a scratch folder - never the agent folder in the machine's program data - and as its controller, and fetches a
// capture's evidence that exists already, read-only, checking afterwards that nothing of it changed: nothing is recorded.
// `renewals` drives renewals through an agent in this process whose host's capture is a stand-in. Run elevated: an
// agent's folder is its administrators'. Each writes its figures, and no path, address or process, to a results folder.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The remote qualification runs on Windows.");
    return 3;
}

return args.FirstOrDefault() switch
{
    "overhead" => await RemoteQualification.OverheadAsync(args),
    "renewals" => await RemoteQualification.RenewalsAsync(args),
    _ => RemoteQualification.Usage(),
};

[SupportedOSPlatform("windows")]
internal static class RemoteQualification
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Usage()
    {
        Console.Error.WriteLine("InterCat.RemoteQualification overhead --cli <InterCat.Cli.exe> --evidence <capture folder> --output <folder> [--idle-seconds <n>] [--fetches <n>]");
        Console.Error.WriteLine("InterCat.RemoteQualification renewals --output <folder> [--count <n>]");
        return 2;
    }

    /// <summary>
    /// An idle agent's footprint over <c>--idle-seconds</c>, sampled every second, and <c>--fetches</c> fetches of an existing
    /// capture's evidence through it, each timed with both processes' processor time; the evidence is checked unchanged.
    /// </summary>
    public static async Task<int> OverheadAsync(string[] args)
    {
        string? cliOption = Option(args, "--cli"), evidenceOption = Option(args, "--evidence"), outputOption = Option(args, "--output");
        if (cliOption is null || evidenceOption is null || outputOption is null)
        {
            return Usage();
        }

        string cli = Path.GetFullPath(cliOption), evidence = Path.GetFullPath(evidenceOption), output = Path.GetFullPath(outputOption);
        int idleSeconds = int.Parse(Option(args, "--idle-seconds") ?? "300", CultureInfo.InvariantCulture);
        int fetches = int.Parse(Option(args, "--fetches") ?? "3", CultureInfo.InvariantCulture);
        string scratch = Path.Combine(Path.GetTempPath(), "intercat-remote-qualification-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        string agentRoot = Path.Combine(scratch, "agent");
        string controllerRoot = Path.Combine(scratch, "controller");
        FileState[] before = FileState.Of(evidence);
        Guid capture;
        SessionStore viewed = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(evidence));
        try
        {
            capture = viewed.Current?.SessionId ?? throw new InvalidOperationException("The evidence published nothing.");
        }
        finally
        {
            viewed.ReleaseSegmentReaders();
        }

        string token = JsonNode.Parse((await IcatAsync(cli, "agent", "token", "--root", agentRoot, "--json")).Answered())!["token"]!.GetValue<string>();
        int port = FreePort();
        string endpoint = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}");
        using Process agent = Start(cli, "agent", "serve", "--listen", endpoint, "--root", agentRoot);
        var samples = new List<Sample>();
        var runs = new List<FetchRun>();
        try
        {
            await ListeningAsync(port, agent);
            _ = (await IcatAsync(cli, "remote", "enroll", endpoint, token, "--state", controllerRoot, "--json")).Answered();

            // Idle: enrolled, listening, asked nothing.
            samples.Add(Sample.Of(agent));
            for (int second = 1; second <= idleSeconds; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                samples.Add(Sample.Of(agent));
            }

            // Fetches: the agent's record names the existing evidence as a capture this controller started.
            RemotePin controller;
            using (RemoteIdentity key = ControllerState.Open(controllerRoot).Identity())
            {
                controller = key.Pin;
            }

            AgentState.Open(agentRoot).Captures.Add(new RemoteCaptureRecord(capture, controller, evidence, "explore", DateTimeOffset.UtcNow));
            for (int at = 1; at <= fetches; at++)
            {
                string into = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"fetch-{at}"));
                Sample agentBefore = Sample.Of(agent);
                Icat run = await IcatAsync(cli, "remote", "fetch", endpoint, capture.ToString("N"), into, "--state", controllerRoot, "--json");
                Sample agentAfter = Sample.Of(agent);
                JsonNode fetched = JsonNode.Parse(run.Answered())!;
                runs.Add(new FetchRun(
                    at,
                    fetched["files"]!.GetValue<int>(),
                    fetched["bytes"]!.GetValue<long>(),
                    fetched["fetchedBytes"]!.GetValue<long>(),
                    fetched["elapsedMilliseconds"]!.GetValue<double>(),
                    run.Wall.TotalMilliseconds,
                    run.Processor.TotalMilliseconds,
                    (agentAfter.Processor - agentBefore.Processor).TotalMilliseconds,
                    agentAfter.WorkingSetBytes));
                Directory.Delete(into, recursive: true);
            }
        }
        finally
        {
            agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync();
        }

        bool unchanged = before.SequenceEqual(FileState.Of(evidence));
        Directory.Delete(scratch, recursive: true);
        TimeSpan idle = samples[^1].Processor - samples[0].Processor;
        var result = new
        {
            Contract = "remote-overhead-v1",
            MeasuredUtc = DateTimeOffset.UtcNow,
            Machine = Machine(),
            Idle = new
            {
                Seconds = idleSeconds,
                ProcessorMilliseconds = idle.TotalMilliseconds,
                ProcessorPercentOfOneCore = idle.TotalMilliseconds / (idleSeconds * 10.0),
                WorkingSetBytes = new { Start = samples[0].WorkingSetBytes, End = samples[^1].WorkingSetBytes, Most = samples.Max(sample => sample.WorkingSetBytes) },
                PrivateBytes = new { Start = samples[0].PrivateBytes, End = samples[^1].PrivateBytes },
                Handles = new { Start = samples[0].Handles, End = samples[^1].Handles },
                Threads = new { Start = samples[0].Threads, End = samples[^1].Threads },
            },
            Fetches = runs,
            FetchMedian = runs.Count == 0 ? null : new
            {
                MebibytesPerSecond = Median(runs.Select(run => run.Bytes / 1_048_576.0 / (run.ElapsedMilliseconds / 1_000))),
                ControllerProcessorMillisecondsPerMebibyte = Median(runs.Select(run => run.ControllerProcessorMilliseconds / (run.Bytes / 1_048_576.0))),
                AgentProcessorMillisecondsPerMebibyte = Median(runs.Select(run => run.AgentProcessorMilliseconds / (run.Bytes / 1_048_576.0))),
            },
            EvidenceUnchanged = unchanged,
        };
        Directory.CreateDirectory(output);
        string json = JsonSerializer.Serialize(result, Indented);
        await File.WriteAllTextAsync(Path.Combine(output, "overhead.json"), json);
        Console.WriteLine(json);
        return unchanged ? 0 : 1;
    }

    /// <summary>
    /// <c>--count</c> renewals of a control lease through an agent in this process over loopback TLS, its host's capture a
    /// stand-in: each renewal's round trip, and the processor time of both ends together per renewal.
    /// </summary>
    public static async Task<int> RenewalsAsync(string[] args)
    {
        if (Option(args, "--output") is not { } outputOption)
        {
            return Usage();
        }

        string output = Path.GetFullPath(outputOption);
        int count = int.Parse(Option(args, "--count") ?? "2000", CultureInfo.InvariantCulture);
        CancellationToken none = CancellationToken.None;
        var enrollments = new AgentEnrollments();
        using var key = RemoteIdentity.Create("agent");
        await using var captures = new RemoteCaptures(new StandInCapture());
        await using var server = new RemoteAgentServer(key, enrollments, new RemoteAgentHost(Guid.NewGuid(), "qualification-host", "Windows", "qualification"),
            captures: captures);
        IPEndPoint endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0));
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await RemoteAgentConnection.ConnectAsync(endpoint, controller, key.Pin, TimeSpan.FromSeconds(10), none);
        _ = await connection.EnrollAsync(enrollments.Issue(key.Pin, RemoteGrants.MetadataCapture), none);
        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 3_600, 64), none);
        RemoteCaptureStarted started = await connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromMinutes(5), none);
        for (int warm = 0; warm < 100; warm++)
        {
            _ = await connection.RenewCaptureAsync(started.CaptureId, none);
        }

        var roundTrips = new double[count];
        using Process self = Process.GetCurrentProcess();
        TimeSpan processorBefore = self.TotalProcessorTime;
        var all = Stopwatch.StartNew();
        for (int at = 0; at < count; at++)
        {
            long began = Stopwatch.GetTimestamp();
            _ = await connection.RenewCaptureAsync(started.CaptureId, none);
            roundTrips[at] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
        }

        all.Stop();
        self.Refresh();
        TimeSpan processor = self.TotalProcessorTime - processorBefore;
        _ = await connection.StopCaptureAsync(started.CaptureId, none);
        Array.Sort(roundTrips);
        var result = new
        {
            Contract = "remote-renewals-v1",
            MeasuredUtc = DateTimeOffset.UtcNow,
            Machine = Machine(),
            Renewals = count,
            RoundTripMicroseconds = new
            {
                Median = Percentile(roundTrips, 0.50),
                P95 = Percentile(roundTrips, 0.95),
                P99 = Percentile(roundTrips, 0.99),
                Most = roundTrips[^1],
            },
            BothEndsProcessorMicrosecondsPerRenewal = processor.TotalMicroseconds / count,
            WallMilliseconds = all.Elapsed.TotalMilliseconds,
        };
        Directory.CreateDirectory(output);
        string json = JsonSerializer.Serialize(result, Indented);
        await File.WriteAllTextAsync(Path.Combine(output, "renewals.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    private static object Machine() => new
    {
        LogicalProcessors = Environment.ProcessorCount,
        OperatingSystem = Environment.OSVersion.VersionString,
        Runtime = Environment.Version.ToString(),
    };

    private static double Median(IEnumerable<double> values) => Percentile([.. values.Order()], 0.50);

    private static double Percentile(double[] sorted, double rank) => sorted[Math.Clamp((int)Math.Ceiling(rank * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Waits until the agent accepts a connection, or says why it stopped.</summary>
    private static async Task ListeningAsync(int port, Process agent)
    {
        for (DateTime giveUp = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < giveUp; await Task.Delay(100))
        {
            if (agent.HasExited)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The agent stopped with exit code {agent.ExitCode}."));
            }

            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
            }
        }

        throw new TimeoutException("The agent did not listen within 60 seconds.");
    }

    private static Process Start(string cli, params string[] arguments)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("icat did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<Icat> IcatAsync(string cli, params string[] arguments)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("icat did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        clock.Stop();
        return new Icat(arguments[0] + " " + arguments[1], process.ExitCode, await output, await error, clock.Elapsed, process.TotalProcessorTime);
    }

    private sealed record Icat(string Verb, int ExitCode, string Output, string Error, TimeSpan Wall, TimeSpan Processor)
    {
        /// <summary>What it printed, where it succeeded; otherwise why not.</summary>
        public string Answered() => ExitCode == 0
            ? Output
            : throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"icat {Verb} exited {ExitCode}: {Error.Trim()}"));
    }

    private sealed record Sample(TimeSpan Processor, long WorkingSetBytes, long PrivateBytes, int Handles, int Threads)
    {
        public static Sample Of(Process process)
        {
            process.Refresh();
            return new(process.TotalProcessorTime, process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, process.Threads.Count);
        }
    }

    private sealed record FetchRun(int Run, int Files, long Bytes, long FetchedBytes, double ElapsedMilliseconds, double WallMilliseconds,
        double ControllerProcessorMilliseconds, double AgentProcessorMilliseconds, long AgentWorkingSetBytes);

    /// <summary>A file of the evidence as it stood: its name, length and when it was last written.</summary>
    private sealed record FileState(string Name, long Length, DateTime WrittenUtc)
    {
        public static FileState[] Of(string folder) =>
            [.. new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(file => new FileState(Path.GetRelativePath(folder, file.FullName), file.Length, file.LastWriteTimeUtc))
                .OrderBy(file => file.Name, StringComparer.Ordinal)];
    }

    /// <summary>A host's capture that records nothing: prepared, started and renewed at once, recording until stopped.</summary>
    private sealed class StandInCapture : IRemoteCaptureDriver
    {
        public Task<DriverPreparation> PrepareAsync(RemoteCaptureRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DriverPreparation(Guid.NewGuid(), request.Profile, request.Seconds, request.JournalMebibytes, "Nothing: a stand-in."));

        public Task<DriverStart> StartAsync(Guid prepared, CancellationToken cancellationToken) =>
            Task.FromResult(new DriverStart(Guid.NewGuid(), "stand-in"));

        public Task ReleaseAsync(Guid prepared, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DriverCaptureState> StatusAsync(Guid capture, CancellationToken cancellationToken) =>
            Task.FromResult(new DriverCaptureState(RemoteCaptureLifecycle.Recording, 0, 0, false, null));

        public Task RenewAsync(Guid capture, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DriverCaptureState> StopAsync(Guid capture, CancellationToken cancellationToken) =>
            Task.FromResult(new DriverCaptureState(RemoteCaptureLifecycle.Closed, 0, 0, true, null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
