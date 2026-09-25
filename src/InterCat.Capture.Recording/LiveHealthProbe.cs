using InterCat.Capture.Windows;

namespace InterCat.Capture.Recording;

/// <summary>What a recording's acquisition counters read while it records, and whether its loss counters could be read.</summary>
public sealed record LiveHealth(CaptureHealthSnapshot Snapshot, bool SourceLossReadable);

/// <summary>
/// A live view of one recording's acquisition counters and live preview for a client that asks while it records. It
/// reads only while the capture is recording; once stopping begins it reads nothing, and the recording's final ledger
/// states what it lost.
/// </summary>
public sealed class LiveHealthProbe
{
    private OwnedCaptureSession? session;
    private LivePreviewTally? preview;

    /// <summary>
    /// The journaled records not yet safely followed, counted by mechanism and time, or null when the capture is not
    /// recording. Counts only: exact records arrive with the chunk that holds them.
    /// </summary>
    public LivePreviewSnapshot? ReadPreview() => Volatile.Read(ref session) is null ? null : Volatile.Read(ref preview)?.Read();

    /// <summary>The counters now, or null when the capture is not recording.</summary>
    public LiveHealth? Read()
    {
        OwnedCaptureSession? current = Volatile.Read(ref session);
        if (current is null)
        {
            return null;
        }

        try
        {
            CaptureHealthSnapshot snapshot = current.ReadHealth();
            return new(snapshot, !current.SourceLossUnreadable);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    internal void Attach(OwnedCaptureSession recording, LivePreviewTally? tally = null)
    {
        Volatile.Write(ref preview, tally);
        Volatile.Write(ref session, recording);
    }

    internal void Detach()
    {
        Volatile.Write(ref session, null);
        Volatile.Write(ref preview, null);
    }
}
