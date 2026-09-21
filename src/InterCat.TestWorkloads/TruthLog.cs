using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>
/// Writes an independent truth log as JSON lines. The workload never reads InterCat state and never feeds
/// the correlator: the log exists so a test can contradict the capture (section 13.1).
/// </summary>
internal sealed class TruthLog : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private readonly Lock gate = new();
    private readonly StreamWriter writer;
    private readonly string scenarioId;
    private readonly string role;
    private readonly int processId;
    private readonly long processStartFileTime;
    private readonly long startTimestamp;
    private long sequence;

    public TruthLog(string path, string scenarioId, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.scenarioId = scenarioId;
        this.role = role;

        using Process current = Process.GetCurrentProcess();
        processId = current.Id;
        processStartFileTime = current.StartTime.ToUniversalTime().ToFileTimeUtc();
        startTimestamp = Stopwatch.GetTimestamp();

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        writer = new(path, append: false) { AutoFlush = false };
    }

    public int ProcessId => processId;

    public long ProcessStartFileTime => processStartFileTime;

    /// <summary>
    /// Appends one truth record. A concurrent workload writes from several connection tasks at once, so
    /// the sequence number and the line are taken under one lock: a truth log with interleaved halves of
    /// two records could not contradict anything (section 13.1).
    /// </summary>
    public void Write(
        TruthEventKind kind,
        long? callId = null,
        int? localPort = null,
        int? remotePort = null,
        long? declaredBytes = null,
        long? completedBytes = null,
        string? status = null,
        string? resourceName = null)
    {
        lock (gate)
        {
            WriteLocked(kind, callId, localPort, remotePort, declaredBytes, completedBytes, status, resourceName);
        }
    }

    private void WriteLocked(
        TruthEventKind kind,
        long? callId,
        int? localPort,
        int? remotePort,
        long? declaredBytes,
        long? completedBytes,
        string? status,
        string? resourceName)
    {
        var record = new TruthRecord
        {
            ScenarioId = scenarioId,
            Role = role,
            Sequence = ++sequence,
            Kind = kind,
            ProcessId = processId,
            ProcessStartFileTime = processStartFileTime,
            MonotonicTicks = Stopwatch.GetTimestamp() - startTimestamp,
            RecordedUtc = DateTimeOffset.UtcNow,
            CallId = callId,
            LocalPort = localPort,
            RemotePort = remotePort,
            ResourceName = resourceName,
            DeclaredBytes = declaredBytes,
            CompletedBytes = completedBytes,
            Status = status,
        };

        writer.WriteLine(JsonSerializer.Serialize(record, Options));
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            writer.Flush();
        }

        await writer.FlushAsync().ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
