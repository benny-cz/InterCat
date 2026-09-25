using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using Xunit.Sdk;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// A headless Avalonia fact that measures §6.8's latency windows only when asked: set <c>INTERCAT_LATENCY_OUTPUT</c> to a
/// new directory for its report. A measurement is not a test of this machine's speed, so without the variable it is
/// reported as skipped, never as passed.
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LatencyFactAttribute : FactAttribute
{
    public const string Variable = "INTERCAT_LATENCY_OUTPUT";

    public LatencyFactAttribute()
    {
        if (OutputPath is null)
        {
            Skip = $"Set {Variable} to a new directory to measure §6.8's interaction latency windows.";
        }
    }

    public static string? OutputPath => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;
}

/// <summary>
/// §6.8's windows, measured in the real window over sessions of growing size. Each gesture's canvas cost is the time to
/// build the timeline's, minimap's and graph's drawing - recorded without rasterizing, which the headless platform does
/// in software and a desktop does on the GPU - so it is the UI thread's share of a frame. Level changes, a brush and a
/// search are timed from the gesture to their answer, as the user waits for it.
/// </summary>
public sealed class InteractionLatencyTests
{
    private const double FrameBudgetMs = 16.7;
    private const double FeedbackBudgetMs = 100;
    private const double AnswerBudgetMs = 1_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [LatencyFact(DisplayName = "§6.8: interaction latency windows are measured over sessions of growing size")]
    public async Task MeasureInteractionLatencyWindows()
    {
        string output = Path.GetFullPath(LatencyFactAttribute.OutputPath!);
        Directory.CreateDirectory(output);
        var sessions = new List<Dictionary<string, object?>>();
        foreach (int rows in new[] { 100_000, 1_000_000 })
        {
            using var session = new TemporarySession();
            Publish(session.Store, Synthetic(rows));
            sessions.Add(await MeasureAsync($"synthetic {rows:N0}", session.Path, rows));
        }

        if (RealSessionFactAttribute.SessionPath is { } real)
        {
            sessions.Add(await MeasureAsync("real saved session", real, null));
        }

        var report = new Dictionary<string, object?>
        {
            ["schema"] = "intercat.interaction-latency.v1",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}",
            ["budgetsMs"] = new Dictionary<string, double>
            {
                ["frame"] = FrameBudgetMs, ["feedback"] = FeedbackBudgetMs, ["coarseAnswer"] = AnswerBudgetMs,
            },
            ["notes"] = new[]
            {
                "Canvas cost is TimelineView, MinimapView and GraphView building their drawing for one gesture, recorded "
                    + "into a drawing group without rasterizing; it is the UI thread's share of a frame, not GPU time.",
                "Level change, brush and search are wall time from the gesture to the answer in the window, including "
                    + "off-thread queries and their hand-back to the UI thread.",
                "Synthetic sessions are 50 paired loopback TCP conversations under one executable group; the real session, "
                    + "when measured, is named by the INTERCAT_REAL_SESSION variable and stays local.",
            },
            ["sessions"] = sessions,
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
    }

    private static async Task<Dictionary<string, object?>> MeasureAsync(string name, string path, int? rows)
    {
        var result = new Dictionary<string, object?> { ["session"] = name };
        var clock = Stopwatch.StartNew();

        // As the window opens a saved session: through the store its workspaces then share.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(SharedSessionStores.Open(path));
        result["rows"] = rows ?? overview.ObservationRows;
        result["openOverviewMs"] = clock.Elapsed.TotalMilliseconds;

        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        clock.Restart();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Latency measurement.",
            SessionPath: path, Overview: overview), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        result["openToLaidOutMs"] = clock.Elapsed.TotalMilliseconds;
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        MinimapView minimap = window.GetControl<MinimapView>("MinimapSurface");
        GraphView graph = window.GetControl<GraphView>("GraphSurface");

        // §6.8's first window: a pan, a zoom and a hover each move from cached geometry. A few unmeasured gestures first,
        // so compiling the drawing code once is not reported as a frame.
        timeline.Focus();
        for (int warm = 0; warm < 5; warm++)
        {
            _ = timeline.Navigate(Key.Right, KeyModifiers.Shift);
            _ = timeline.Navigate(warm % 2 == 0 ? Key.OemPlus : Key.OemMinus);
            _ = Canvas(timeline, minimap, graph);
        }

        timeline.SetViewport(null);
        Dispatch();
        for (int warm = 0; warm < 5; warm++)
        {
            window.MouseMove(timeline.TranslatePoint(timeline.PointOf(workspace.Snapshot.Timeline[warm])!.Value, window)!.Value);
            _ = timeline.HoverCard;
            _ = Canvas(timeline, minimap, graph);
        }
        result["panCanvasMs"] = Summary(Enumerable.Range(0, 30).Select(step =>
        {
            _ = timeline.Navigate(Key.Right, KeyModifiers.Shift);
            return Canvas(timeline, minimap, graph);
        }));
        result["zoomCanvasMs"] = Summary(Enumerable.Range(0, 30).Select(index =>
        {
            _ = timeline.Navigate(index % 2 == 0 ? Key.OemPlus : Key.OemMinus);
            return Canvas(timeline, minimap, graph);
        }));
        timeline.SetViewport(null);
        Dispatch();
        result["hoverFeedbackMs"] = Summary(workspace.Snapshot.Timeline.Take(30).Select(bucket =>
        {
            var hover = Stopwatch.StartNew();
            window.MouseMove(timeline.TranslatePoint(timeline.PointOf(bucket)!.Value, window)!.Value);
            _ = timeline.HoverCard;
            return hover.Elapsed.TotalMilliseconds + Canvas(timeline, minimap, graph);
        }));
        window.MouseMove(new Point(1, 1));

        // §6.8's third window: a level change, a brush and a search each give an answer within a second, or say so.
        var steps = new List<Dictionary<string, object?>>();
        foreach (string level in new[] { "group", "process", "channel" })
        {
            if (workspace.RungRows is not [var row, ..])
            {
                break;
            }

            workspace.SelectedRung = row;
            clock.Restart();
            if (!workspace.Descend())
            {
                break;
            }

            await workspace.LayoutReady;
            Dispatch();
            double laidOut = clock.Elapsed.TotalMilliseconds;
            await workspace.TimelineDetailReady;
            Dispatch();
            steps.Add(new()
            {
                ["level"] = level,
                ["graphLaidOutMs"] = laidOut,
                ["timelineCountedMs"] = clock.Elapsed.TotalMilliseconds,
                ["withinCoarseAnswer"] = clock.Elapsed.TotalMilliseconds <= AnswerBudgetMs,
            });
        }

        result["levelChanges"] = steps;
        workspace.ReturnTo(0);
        await workspace.LayoutReady;
        Dispatch();

        TimeRange extent = workspace.Snapshot.Extent;
        clock.Restart();
        workspace.SelectInterval(new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.EndTicks - (extent.SpanTicks / 4)));
        await workspace.IntervalReady;
        Dispatch();
        result["brushRankedMs"] = clock.Elapsed.TotalMilliseconds;
        workspace.ClearSelection();

        clock.Restart();
        workspace.SearchText = "PID 1";
        Dispatch();
        result["searchMs"] = clock.Elapsed.TotalMilliseconds;
        result["searchHits"] = workspace.SearchResults.Count;
        window.Close();
        return result;
    }

    /// <summary>The UI thread's share of one frame: each canvas building its drawing, recorded, not rasterized.</summary>
    private static double Canvas(params Control[] controls)
    {
        var clock = Stopwatch.StartNew();
        foreach (Control control in controls)
        {
            var group = new DrawingGroup();
            using DrawingContext context = group.Open();
            control.Render(context);
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    private static Dictionary<string, double> Summary(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        double At(double quantile) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)];
        return new()
        {
            ["p50"] = Math.Round(At(0.5), 2),
            ["p95"] = Math.Round(At(0.95), 2),
            ["max"] = Math.Round(sorted[^1], 2),
        };
    }

    /// <summary>50 paired loopback conversations, each record a send or a receive a microsecond apart.</summary>
    private static ObservationRowV1[] Synthetic(int rows) =>
    [
        .. Enumerable.Range(0, rows).Select(index =>
        {
            int pair = index % 50;
            bool send = (index / 50) % 2 == 0;
            string clientEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{40_000 + pair}");
            string serverEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + pair}");
            ObservationRowV1 row = send
                ? Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 64, 1_000 + pair, (ulong)(index + 1))
                    .Between(clientEnd, serverEnd)
                : Transfer(10 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 5_000 + pair, (ulong)(index + 1))
                    .Between(serverEnd, clientEnd);
            return row with { SessionRelativeTicks = (10L + index) * 100 };
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
