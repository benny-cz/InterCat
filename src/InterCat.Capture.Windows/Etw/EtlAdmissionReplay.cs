using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// <summary>
    /// Opens <paramref name="path"/> as an event trace, the one way InterCat opens an ETL file (§18.4). Windows' trace reader
    /// refuses a file that is not an event trace, or is damaged, with an error the trace library raises as an exception of
    /// its own - a file of other bytes as one that is corrupted and unreadable - which is the file's fault and never
    /// InterCat's: it is refused as unreadable input, in a sentence naming the file and what Windows said, rather than ended
    /// on as a defect (§20.4, §20.6).
    /// </summary>
    public static ETWTraceEventSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return new ETWTraceEventSource(path, TraceEventSourceType.FileOnly);
        }
        catch (Exception exception) when (exception is BadImageFormatException or ExternalException)
        {
            throw new InvalidDataException(
                $"'{path}' is not an event trace Windows can open, so nothing in it was read: {exception.Message}", exception);
        }
    }

    /// <summary>Refuses <paramref name="path"/>, as <see cref="Open"/> does, unless it opens as an event trace.</summary>
    public static void EnsureOpens(string path)
    {
        using ETWTraceEventSource source = Open(path);
    }

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
        using ETWTraceEventSource source = Open(path);
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
