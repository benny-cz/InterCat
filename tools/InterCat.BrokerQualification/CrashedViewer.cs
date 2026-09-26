using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;

// A viewer that crashes while recording loses no evidence, and the next launch offers to finish its session (plan §3.1
// step 6). This scenario kills a real Desktop capture runner mid-capture, lets the broker stop and finalize the capture
// when the owner lease lapses, and then finishes the session from the ticket the runner left, as the next launch does.
[SupportedOSPlatform("windows")]
internal static partial class Qualification
{
    /// <summary>How long after the viewer's death the broker may take to stop and finalize before the run fails.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(150);

    public static async Task<int> CrashedViewerAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        int seconds = int.TryParse(Option(args, "--seconds") ?? "12", NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 5 and <= 60 ? parsed : -1;
        if (output is null || seconds < 0)
        {
            return Usage();
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("The crashed-viewer qualification starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; qualification writes only to a new directory.");
            return 2;
        }

        Directory.CreateDirectory(output);
        string sessions = Path.Combine(output, "sessions");
        var report = new CrashedViewerReport
        {
            Schema = "intercat.crashed-viewer.v1",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            RecordingSeconds = seconds,
            Notes =
            [
                "The viewer is a child process running the Desktop's capture runner unchanged, with this tool as its broker "
                    + "over a qualification root. After recording for the stated time with paced loopback traffic, it "
                    + "terminates itself: no stop request, no disposal, no closed pipe beforehand.",
                "The broker is a separate process and outlives the viewer. It stops the capture when the owner lease the "
                    + "viewer renewed lapses, and finalizes it.",
                "This process then does what the next launch does: it finds the ticket the runner left beside the session, "
                    + "assesses it until it can be finished, finishes it, and checks the session re-derives from its journal.",
            ],
        };

        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        try
        {
            var viewer = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                ArgumentList = { "crashed-viewer-child", "--session-root", sessions, "--seconds", seconds.ToString(CultureInfo.InvariantCulture) },
            };
            using (Process child = Process.Start(viewer) ?? throw new InvalidOperationException("The viewer did not start."))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 120));
                Task<string> log = child.StandardOutput.ReadToEndAsync(timeout.Token);
                await child.WaitForExitAsync(timeout.Token);
                report.ViewerExitCode = child.ExitCode;
                report.ViewerLog = [.. (await log).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }

            var sinceCrash = Stopwatch.StartNew();
            report.ViewerTerminated = report.ViewerLog.Any(line => line.StartsWith("terminating", StringComparison.Ordinal));
            LiveFollowTicket ticket = LiveFollowTicket.FindInterrupted(sessions) switch
            {
                [var only] => only,
                var found => throw new InvalidOperationException(
                    $"Expected one interrupted follow beside the viewer's session and found {found.Count}."),
            };
            report.TicketStartedUtc = ticket.StartedUtc;
            report.TicketOwnerLeaseExpiresUtc = ticket.OwnerLeaseExpiresUtc;

            InterruptedFollow assessed = InterruptedFollow.Assess(ticket);
            report.StateAfterCrash = assessed.State.ToString();
            report.ChunksFollowedBeforeCrash = assessed.SessionChunks;
            report.EvidenceChunksAfterCrash = assessed.EvidenceChunks;
            while (assessed.State == InterruptedFollowState.StillRecording && sinceCrash.Elapsed < SettleTimeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                assessed = InterruptedFollow.Assess(ticket);
            }

            report.SettledState = assessed.State.ToString();
            report.SecondsUntilSettled = Math.Round(sinceCrash.Elapsed.TotalSeconds, 1);
            report.EvidenceChunks = assessed.EvidenceChunks;
            if (assessed.State != InterruptedFollowState.Finishable)
            {
                throw new InvalidOperationException(
                    $"The interrupted capture settled as {assessed.State}, not as a finishable one. {assessed.Problem}");
            }

            var finishing = Stopwatch.StartNew();
            int progressReports = 0;
            InterruptedFollowResult finished = InterruptedFollow.Finish(
                ticket, new InlineProgress(_ => progressReports++), cancellationToken: cancellationToken);
            report.FinishMs = finishing.ElapsedMilliseconds;
            report.FinishProgressReports = progressReports;
            report.FinishCompleted = finished.Completed;
            report.SessionFinished = finished.Step.Finished;
            report.SessionChunks = finished.Step.DerivedChunks;
            report.SessionRecords = finished.Step.DerivedRecords;
            report.RederivedRows = JournalRederivation.Verify(finished.Session, cancellationToken).ObservationRows;
            report.TicketRemoved = !File.Exists(LiveFollowTicket.PathFor(ticket.SessionDirectory));
            report.NothingLeftToOffer = LiveFollowTicket.FindInterrupted(sessions).Count == 0;
            if (!await WaitForCliBrokerExitAsync(cancellationToken))
            {
                report.Failure = "The broker did not exit within 90 s of the finish.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Failure = exception.ToString();
        }
        finally
        {
            report.LeakedSessions = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Passed = report.Failure is null && report.LeakedSessions.Count == 0 && report.ViewerTerminated
                && report.FinishCompleted && report.SessionFinished && report.TicketRemoved && report.NothingLeftToOffer
                && report.SessionChunks == report.EvidenceChunks && report.SessionChunks > report.ChunksFollowedBeforeCrash
                && report.RederivedRows == report.SessionRecords
                && report.TicketOwnerLeaseExpiresUtc > report.TicketStartedUtc + TimeSpan.FromSeconds(30);
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json),
                CancellationToken.None);
            TryDelete(CliRootParent);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"viewer followed {report.ChunksFollowedBeforeCrash} chunks before it was terminated; the capture settled as "
            + $"{report.SettledState} after {report.SecondsUntilSettled} s; finishing derived {report.SessionChunks} chunks "
            + $"({report.SessionRecords:N0} records) in {report.FinishMs} ms"));
        Console.WriteLine(report.Passed ? "Crashed-viewer qualification passed." : "Crashed-viewer qualification FAILED; see report.json.");
        Console.WriteLine(Path.Combine(output, "report.json"));
        return report.Passed ? 0 : 1;
    }

    /// <summary>
    /// The viewer: the Desktop's capture runner, recording with paced loopback traffic until it has derived part of the
    /// capture, and then terminated as a crash leaves it.
    /// </summary>
    public static async Task<int> CrashedViewerChildAsync(string[] args, CancellationToken cancellationToken)
    {
        string root = Option(args, "--session-root") ?? throw new ArgumentException("--session-root is required.");
        int seconds = int.Parse(Option(args, "--seconds") ?? "12", CultureInfo.InvariantCulture);
        var recording = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int overviews = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task capture = DesktopCaptureRunner.RunAsync(
            update =>
            {
                if (update.Phase == CaptureUiPhase.Recording)
                {
                    _ = recording.TrySetResult();
                }

                if (update.Overview is not null)
                {
                    _ = Interlocked.Increment(ref overviews);
                }
            },
            stop.Token,
            new CaptureRunOptions
            {
                Broker = new(Environment.ProcessPath!, ["serve"]),
                SessionRoot = root,
                MaximumDurationSeconds = 120,
            });
        await Task.WhenAny(recording.Task, capture);
        if (capture.IsCompleted)
        {
            Console.WriteLine("the capture ended before it recorded");
            return 4;
        }

        Console.WriteLine("recording");
        await PacedLoopbackAsync(TimeSpan.FromSeconds(seconds), capture, cancellationToken);
        var derived = Stopwatch.StartNew();
        while (Volatile.Read(ref overviews) == 0 && !capture.IsCompleted && derived.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"terminating after {Volatile.Read(ref overviews)} overviews"));
        Console.Out.Flush();

        // No stop, no disposal: the process vanishes as a crash leaves it, and only the owner lease ends the capture.
        Process.GetCurrentProcess().Kill();
        return 0;
    }

    private sealed class InlineProgress(Action<FollowStep> report) : IProgress<FollowStep>
    {
        public void Report(FollowStep value) => report(value);
    }
}

internal sealed class CrashedViewerReport
{
    public required string Schema { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset FinishedUtc { get; set; }

    public required ProbeEnvironment Environment { get; init; }

    public required int RecordingSeconds { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public int? ViewerExitCode { get; set; }

    public IReadOnlyList<string> ViewerLog { get; set; } = [];

    public bool ViewerTerminated { get; set; }

    public DateTimeOffset TicketStartedUtc { get; set; }

    /// <summary>The owner lease's expiry at the viewer's last renewal, as its ticket recorded it.</summary>
    public DateTimeOffset TicketOwnerLeaseExpiresUtc { get; set; }

    public string? StateAfterCrash { get; set; }

    public int ChunksFollowedBeforeCrash { get; set; }

    public int? EvidenceChunksAfterCrash { get; set; }

    public string? SettledState { get; set; }

    public double SecondsUntilSettled { get; set; }

    public int? EvidenceChunks { get; set; }

    public long FinishMs { get; set; }

    public int FinishProgressReports { get; set; }

    public bool FinishCompleted { get; set; }

    public bool SessionFinished { get; set; }

    public int SessionChunks { get; set; }

    public long SessionRecords { get; set; }

    public long RederivedRows { get; set; }

    public bool TicketRemoved { get; set; }

    public bool NothingLeftToOffer { get; set; }

    public IReadOnlyList<string> LeakedSessions { get; set; } = [];

    public bool Passed { get; set; }

    public string? Failure { get; set; }
}
