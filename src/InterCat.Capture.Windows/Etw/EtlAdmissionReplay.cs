using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;

namespace InterCat.Capture.Windows;

public sealed record EtlAdmissionReplayResult(
    string InputPath,
    long InputLengthBytes,
    long SourceEventsLost,
    long AssignedRecordCount,
    TimeSpan Elapsed);

/// <summary>
/// Replays an immutable ETL through the same admission adapter used by live capture. The caller owns the
/// sink and therefore controls whether replay writes a comparison journal, computes coverage, or only counts.
/// </summary>
public static class EtlAdmissionReplay
{
    public static EtlAdmissionReplayResult Replay(
        string inputPath,
        OwnedSessionPlan admissionPlan,
        IAdmittedEventSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(admissionPlan);
        ArgumentNullException.ThrowIfNull(sink);

        string path = Path.GetFullPath(inputPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The ETL evidence file does not exist.", path);
        }

        var table = new EventAdmissionTable(admissionPlan.Sources);
        var admitter = new TraceEventRecordAdmitter(
            table,
            admissionPlan.Providers,
            sink,
            admissionPlan.PreserveExtendedData);
        using var source = new ETWTraceEventSource(path, TraceEventSourceType.FileOnly);
        void OnEvent(TraceEvent data) => admitter.Admit(data);

        source.AllEvents += OnEvent;
        using CancellationTokenRegistration registration = cancellationToken.Register(source.StopProcessing);
        long started = Stopwatch.GetTimestamp();
        try
        {
            source.Process();
        }
        finally
        {
            source.AllEvents -= OnEvent;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new(
            path,
            new FileInfo(path).Length,
            source.EventsLost,
            admitter.AssignedRecordCount,
            Stopwatch.GetElapsedTime(started));
    }
}
