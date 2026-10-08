using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.2 and ADR-037: at a process's HTTP exchanges, the timeline draws each exchange as a bar from its first buffer to its
/// response's end, as an RPC channel's calls are drawn from their start to their stop - in the HTTP hue when it was recorded
/// whole, faint where it was not - and past the lane's budget as density. A bar hovers and selects its row.
/// </summary>
public sealed class HttpExchangeLaneTests
{
    [AvaloniaFact(DisplayName = "§6.2: a process's HTTP exchanges are a lane of bars, faint where not recorded whole, that hover and select their row")]
    public async Task ExchangesAreALaneOfBars()
    {
        using var session = new TemporarySession();
        Publish(session.Store, FiveExchanges(out SourceFieldRowV1[] fields), fields: fields);
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenExchanges(session);
        try
        {
            // Every exchange in view is a bar: three recorded whole, one whose response's end was not recorded, and one
            // whose request head was lost.
            Assert.True(workspace.ShowsHttpExchangeLane, workspace.TimelineCaption);
            IReadOnlyList<HttpExchangeSpanView> exchanges = workspace.HttpExchangeSpans!;
            Assert.Equal([true, true, false, true, false], exchanges.Select(exchange => exchange.Complete));
            Assert.Equal("5 in view", workspace.HttpExchangeLaneNote);
            Assert.StartsWith("This process's HTTP exchanges, each from its first buffer to its response's end",
                workspace.TimelineCaption, StringComparison.Ordinal);

            // A whole exchange is drawn in the HTTP hue; one not recorded whole is the same hue, faint over the plot.
            WriteableBitmap frame = Settle(window);
            Color hue = ThemeResources.FillOf(Mechanism.Http, ThemeMode.Dark);
            Point On(HttpExchangeSpanView exchange) => timeline.TranslatePoint(timeline.PointOf(exchange)!.Value, window)!.Value;
            Assert.Equal(hue, At(frame, On(exchanges[0])));
            Color plot = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeMode.Dark).Plot);
            Color faint = At(frame, On(exchanges[2]));
            Assert.True(Near(faint, Color.FromRgb((byte)((hue.R + plot.R) / 2), (byte)((hue.G + plot.G) / 2), (byte)((hue.B + plot.B) / 2))),
                $"An exchange not recorded whole drew {faint}, not the HTTP hue {hue} faint over the plot {plot}.");

            // A resting pointer describes the exchange under it, and a click selects its row, which the lane outlines.
            window.MouseMove(On(exchanges[2]));
            Dispatch();
            Assert.Same(exchanges[2], timeline.HoveredHttpExchange);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.Equal("HTTP exchange · its response's end was not recorded", card.Title);
            Assert.Contains("Not recorded whole; its row says which part", card.Lines);
            Assert.Contains("Click selects its row", card.Lines);
            window.MouseDown(On(exchanges[2]), MouseButton.Left);
            window.MouseUp(On(exchanges[2]), MouseButton.Left);
            Dispatch();
            Assert.Equal(exchanges[2].Key, workspace.SelectedRung?.Key);
            Assert.Equal(exchanges[2].Key, workspace.SelectedHttpExchangeKey);
            Assert.Equal("response end not recorded", workspace.SelectedRung!.Label);
            Assert.Null(workspace.SelectedInterval);

            // A whole exchange's card says how long it took and what its messages held.
            window.MouseMove(On(exchanges[0]));
            Dispatch();
            HoverCard whole = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.Equal("HTTP exchange · " + OperationText.Duration(2_000, System.Globalization.CultureInfo.CurrentCulture), whole.Title);
            Assert.Contains("281 B sent, 1,115 B received", whole.Lines.Select(line => line.Replace(' ', ' ')));
            Assert.Contains("Recorded whole", whole.Lines);
            Save(Settle(window), "l3-http-exchange-lane-1080x700.png");

            // Above the rung there is no exchange lane.
            Assert.True(workspace.Ascend());
            timeline.RequestDetailNow();
            Dispatch();
            Assert.False(workspace.ShowsHttpExchangeLane);
            Assert.Null(workspace.HttpExchangeSpans);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.2: an exchange lane with more exchanges in view than it draws one by one draws them all as density")]
    public async Task ADenseExchangeLaneIsDrawnAsDensity()
    {
        int count = SessionHttpExchanges.MaximumSpans + 100;
        using var session = new TemporarySession();
        Publish(session.Store, ManyExchanges(count, out SourceFieldRowV1[] fields), fields: fields);
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenExchanges(session);
        try
        {
            // No exchange is dropped: each counts into the columns, and the lane and the caption say it is density.
            HttpExchangeDensity density = Assert.IsType<HttpExchangeDensity>(workspace.HttpExchangeDensity);
            Assert.Empty(workspace.HttpExchangeSpans!);
            Assert.Equal(string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{count:N0} · density"),
                workspace.HttpExchangeLaneNote);
            Assert.Contains("HTTP exchanges in view, as density", workspace.TimelineCaption, StringComparison.Ordinal);
            Assert.Contains(density.Incomplete, share => share > 0);

            // A column of exchanges recorded whole is the HTTP hue; one whose every exchange was not recorded whole is the
            // same hue, faint over the plot, never hidden beneath the solid fill.
            WriteableBitmap frame = Settle(window);
            Color hue = ThemeResources.FillOf(Mechanism.Http, ThemeMode.Dark);
            Color plot = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeMode.Dark).Plot);
            Point Column(int index) => timeline.TranslatePoint(timeline.PointOfDensityColumn(index)!.Value, window)!.Value;
            int whole = Enumerable.Range(0, density.Columns).First(index => density.Running[index] > 0 && density.Incomplete[index] == 0);
            int cut = Enumerable.Range(0, density.Columns).First(index => density.Running[index] > 0
                && density.Incomplete[index] == density.Running[index]);
            Assert.Equal(hue, At(frame, Column(whole)));
            Color faint = At(frame, Column(cut));
            Assert.True(Near(faint, Color.FromRgb((byte)((hue.R + plot.R) / 2), (byte)((hue.G + plot.G) / 2), (byte)((hue.B + plot.B) / 2))),
                $"A column not recorded whole drew {faint}, not the HTTP hue {hue} faint over the plot {plot}.");

            // A column among those not recorded whole describes itself, and a click selects its interval.
            int column = cut;
            Point point = timeline.TranslatePoint(timeline.PointOfDensityColumn(column)!.Value, window)!.Value;
            window.MouseMove(point);
            Dispatch();
            Assert.Equal(column, timeline.HoveredHttpDensityColumn);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.EndsWith(" running", card.Title, StringComparison.Ordinal);
            Assert.Contains(card.Lines, line => line.EndsWith(" not recorded whole", StringComparison.Ordinal));
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatch();
            Assert.Equal(density.ColumnInterval(column), workspace.SelectedInterval);
            Save(Settle(window), "l3-http-exchange-density-1080x700.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Opens the session in a window and descends to process 4242's HTTP exchanges, with the lane read for the view.</summary>
    internal static async Task<(MainWindow Window, WorkspaceViewModel Workspace, TimelineView Timeline)> OpenExchanges(TemporarySession session)
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(
            CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ProcessNode node = workspace.Snapshot.Processes.Single(process => process.ProcessId == 4_242);
        foreach (string key in new[] { node.GroupKey, node.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.HttpReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "HTTP exchanges");
        Assert.True(workspace.Descend());
        Assert.True(workspace.IsHttpChannelRung);
        await workspace.HttpReady;

        // Laid out at the rung first, so the view asks for the columns it draws.
        _ = window.CaptureRenderedFrame();
        Dispatch();
        timeline.RequestDetailNow();
        await workspace.HttpSpansReady;
        Dispatch();
        return (window, workspace, timeline);
    }

    /// <summary>
    /// Five exchanges of process 4242, as the application tests read them: exchange 1 in six buffers, its request body's
    /// last raised after its response ended, interleaved with exchange 2's three; exchange 3, whose response body's last
    /// buffer was not recorded; exchange 1's number again; and exchange 3's again, its request head lost.
    /// </summary>
    internal static ObservationRowV1[] FiveExchanges(out SourceFieldRowV1[] fields)
    {
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes)[] buffers =
        [
            (10, 2001, 1, 0, 3, 181), (11, 2002, 1, 0, 1, 64),
            (15, 2001, 2, 0, 3, 181),
            (20, 2003, 1, 0, 3, 115),
            (25, 2003, 2, 0, 3, 115), (26, 2004, 2, 0, 3, 0),
            (29, 2004, 1, 0, 1, 900), (30, 2004, 1, 1, 2, 100), (31, 2002, 1, 1, 2, 36),
            (40, 2001, 3, 0, 3, 181), (41, 2003, 3, 0, 3, 115), (42, 2004, 3, 0, 1, 50),
            (100, 2001, 1, 0, 3, 181), (101, 2003, 1, 0, 3, 115), (102, 2004, 1, 0, 3, 7),
            (200, 2003, 3, 0, 3, 115), (201, 2004, 3, 0, 3, 9),
        ];
        return Exchanges(buffers, out fields);
    }

    /// <summary>
    /// <paramref name="count"/> exchanges of process 4242, one after another, each a request head, a response head and a
    /// response body; the four hundred from the 2,000th have a response body whose last buffer was not recorded, enough to
    /// fill whole density columns.
    /// </summary>
    internal static ObservationRowV1[] ManyExchanges(int count, out SourceFieldRowV1[] fields) => Exchanges(
        [
            .. Enumerable.Range(0, count).SelectMany(index =>
            {
                long at = 10 + (3L * index);
                long number = index + 1;
                long last = index is >= 2_000 and < 2_400 ? 1 : 3;
                return new (long, ushort, long, long, long, long)[]
                {
                    (at, 2001, number, 0, 3, 181), (at + 1, 2003, number, 0, 3, 115), (at + 2, 2004, number, 0, last, 9),
                };
            }),
        ],
        out fields);

    private static ObservationRowV1[] Exchanges(
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes)[] buffers, out SourceFieldRowV1[] fields)
    {
        ObservationRowV1[] rows = [.. buffers.Select((buffer, index) => Http(buffer.Ticks, buffer.Event, (ulong)(index + 1), buffer.Bytes))];
        fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, buffer.Number),
                Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        return [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), .. rows];
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242, with a session time.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
            SessionRelativeTicks = ticks * 100,
        };

    private static bool Near(Color seen, Color expected) =>
        Math.Abs(seen.R - expected.R) <= 3 && Math.Abs(seen.G - expected.G) <= 3 && Math.Abs(seen.B - expected.B) <= 3;

    private static void Save(WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }
}
