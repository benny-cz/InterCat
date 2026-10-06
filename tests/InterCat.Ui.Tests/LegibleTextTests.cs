using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Every word the window shows can be read whole (§6.8): a text too long for its place wraps, or ends in an ellipsis whose
/// tooltip holds it whole. One cut off silently - by its own box, or by a card or panel it sits in - loses what it said,
/// as the empty rung's step once read "Show source records (E".
/// </summary>
public sealed class LegibleTextTests
{
    [AvaloniaFact(DisplayName = "§6.8: at the minimum window, with the rail at its narrowest, no text is cut off: it wraps, or ends in an ellipsis its tooltip completes")]
    public async Task NoTextIsCutOff()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 40).SelectMany(index => new[]
            {
                Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
                Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (2 * index)))
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
            }),
        ]);

        foreach (bool narrowRail in new[] { false, true })
        {
            var window = new MainWindow { Width = 1080, Height = 700 };
            window.Show();
            if (narrowRail)
            {
                ColumnDefinition column = window.GetControl<Grid>("WindowGrid").ColumnDefinitions[0];
                column.Width = new GridLength(column.MinWidth);
            }

            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            var cut = new List<string>();
            string rail = narrowRail ? "narrowest rail" : "rail as it opens";
            cut.AddRange(CutOff(window, $"{rail}, machine"));

            // Down every rung to a channel, whose rung has no rows and offers its records' step beside the reason, then to
            // the records themselves.
            Channel channel = workspace.Snapshot.Channels.Single();
            ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
            foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
            {
                workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
                Assert.True(workspace.Descend());
                cut.AddRange(CutOff(window, $"{rail}, {workspace.LevelBadge}"));
            }

            Assert.True(workspace.OffersEvidenceStep);
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            workspace.SelectedRung = workspace.RungRows[3];
            cut.AddRange(CutOff(window, $"{rail}, evidence"));
            window.Close();
            Assert.True(cut.Count == 0, string.Join(Environment.NewLine, cut));
        }
    }

    /// <summary>
    /// Each visible text the window cuts off where it stands: one wider than its own box that neither wraps nor ends in an
    /// ellipsis, one a clipping card or panel cuts, and one that ends in an ellipsis with no tooltip to complete it.
    /// </summary>
    private static IEnumerable<string> CutOff(Window window, string where)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = window.CaptureRenderedFrame();
        }

        foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text) && text.Bounds.Width > 0))
        {
            var whole = new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                FontFamily = text.FontFamily,
                FontWeight = text.FontWeight,
                FontStyle = text.FontStyle,
                LetterSpacing = text.LetterSpacing,
            };
            whole.Measure(Size.Infinity);
            bool wider = text.TextWrapping == TextWrapping.NoWrap && whole.DesiredSize.Width > text.Bounds.Width + 1;
            bool completed = text.GetSelfAndVisualAncestors().OfType<Control>().Any(control => ToolTip.GetTip(control) is not null);
            if (wider && text.TextTrimming == TextTrimming.None)
            {
                yield return $"{where}: '{text.Text}' is cut off by its own box.";
            }
            else if (wider && !completed)
            {
                yield return $"{where}: '{text.Text}' ends in an ellipsis that no tooltip completes.";
            }
            else if (!wider && ClippedBy(text, window) is { } clip)
            {
                yield return $"{where}: '{text.Text}' is cut off by {clip}.";
            }
        }
    }

    /// <summary>The ancestor that clips the text's right edge, when one does; a scrolled view's own viewport aside.</summary>
    private static string? ClippedBy(TextBlock text, Window window)
    {
        if (text.TranslatePoint(new Point(0, 0), window) is not { } origin)
        {
            return null;
        }

        double right = origin.X + text.Bounds.Width;
        foreach (Control ancestor in text.GetVisualAncestors().OfType<Control>())
        {
            if (!ancestor.ClipToBounds || ancestor.GetType().Name == "ScrollContentPresenter"
                || ancestor.TranslatePoint(new Point(0, 0), window) is not { } corner)
            {
                continue;
            }

            if (right > corner.X + ancestor.Bounds.Width + 1 && origin.X < corner.X + ancestor.Bounds.Width)
            {
                return $"{ancestor.GetType().Name} {ancestor.Name}".TrimEnd();
            }
        }

        return null;
    }
}
