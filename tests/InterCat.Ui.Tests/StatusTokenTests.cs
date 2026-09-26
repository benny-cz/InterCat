using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using Xunit;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.6: a condition or an action is drawn in tokens of its own, never in a mechanism's hue. The coverage hatch and a
/// warning's words take the caution ink, the followed live view the accent, the primary action its measured fill and
/// ink, and the evidence-quality key the graph's own dash patterns in body ink - in either theme mode.
/// </summary>
public sealed class StatusTokenTests
{
    [AvaloniaFact(DisplayName = "P24: the coverage hatch and a warning's words draw in the caution ink, which no mechanism uses, in either mode")]
    public async Task TheHatchAndWarningsTakeTheCautionInk()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        CoverageKeySample swatch = window.GetControl<CoverageKeySample>("CoverageKeySwatch");
        TextBlock summary = window.GetControl<TextBlock>("CoverageSummaryText");
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        TextBlock dot = window.GetControl<TextBlock>("HealthDot");
        Button follow = window.GetControl<Button>("FollowButton");

        try
        {
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                WriteableBitmap frame = Settle(window);
                Color caution = ThemeResources.ToColor(ThemePalette.Status(mode).Caution);

                // The legend's swatch is drawn by the timeline's own hatch routine, which the timeline and the minimap
                // call for every gap: its crisp top edge is the ink every hatch is stroked in.
                Assert.Equal(caution, At(frame, swatch.TranslatePoint(new(swatch.Bounds.Width / 2, 0), window)!.Value));

                // The synthetic session has partial gaps, so its coverage summary is a warning in words.
                Assert.True(viewModel.CoverageLimited);
                Assert.Equal(caution, ColorOf(summary.Foreground));
            }

            // A capture's states come second: a recording replaces the synthetic session with its own live one.
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                Dispatch();
                Color caution = ThemeResources.ToColor(ThemePalette.Status(mode).Caution);

                // An unavailable capture says so in caution ink beside the action that failed, and the health dot agrees.
                window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Capture unavailable", "The broker is not installed."));
                Dispatch();
                Assert.Equal(caution, ColorOf(status.Foreground));
                Assert.Equal(caution, ColorOf(dot.Foreground));

                // A recording the view follows is the accent, not a warning; paused, the view is behind it, which is.
                window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Nothing published yet."));
                Dispatch();
                Assert.NotEqual(caution, ColorOf(status.Foreground));
                Assert.Equal(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Accent), ColorOf(dot.Foreground));
                follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatch();
                Assert.Equal(caution, ColorOf(dot.Foreground));
                follow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatch();
                Assert.Equal(ThemeResources.ToColor(ThemePalette.Surfaces(mode).Accent), ColorOf(dot.Foreground));
            }

            // Complete coverage is a plain statement, never dressed as a warning.
            WorkspaceSnapshot source = SyntheticWorkspace.Create();
            using var covered = new WorkspaceViewModel(source with
            {
                Timeline = [.. source.Timeline.Select(bucket => bucket with { Coverage = CoverageState.Covered })],
            }, "generation-2");
            window.DataContext = covered;
            Dispatch();
            Assert.False(covered.CoverageLimited);
            Assert.Equal(ThemeResources.ToColor(ThemePalette.Surfaces(ThemeMode.Dark).MutedInk), ColorOf(summary.Foreground));
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R14: the primary action draws in its own measured fill and ink at rest, under the pointer and pressed, in either mode")]
    public void ThePrimaryActionKeepsItsTokens()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        Button start = window.GetControl<Button>("StartExploringButton");
        Point away = new(700, 300);

        try
        {
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                window.MouseMove(away);
                WriteableBitmap frame = Settle(window);
                StatusTokens tokens = ThemePalette.Status(mode);
                ContentPresenter face = start.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(presenter => presenter.Name == "PART_ContentPresenter");
                Point fill = start.TranslatePoint(new(6, start.Bounds.Height / 2), window)!.Value;
                Point middle = start.TranslatePoint(new(start.Bounds.Width / 2, start.Bounds.Height / 2), window)!.Value;

                // At rest: the action fill, with the ink measured on it. It once wore the TCP fill under white text.
                Assert.Equal(ThemeResources.ToColor(tokens.ActionFill), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(tokens.ActionInk), ColorOf(face.Foreground));

                // The theme's own pointer-over look would make it an ordinary grey button; it keeps its hover fill.
                window.MouseMove(middle);
                frame = Settle(window);
                Assert.Equal(ThemeResources.ToColor(tokens.ActionFillHover), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(tokens.ActionInk), ColorOf(face.Foreground));

                // Pressed, and released away from the button, so nothing starts.
                window.MouseDown(middle, MouseButton.Left);
                frame = Settle(window);
                Assert.Equal(ThemeResources.ToColor(tokens.ActionFillPressed), At(frame, fill));
                Assert.Equal(ThemeResources.ToColor(tokens.ActionInk), ColorOf(face.Foreground));
                window.MouseMove(away);
                window.MouseUp(away, MouseButton.Left);
                Dispatch();
                Assert.Equal("Ready to explore", window.GetControl<TextBlock>("CaptureStatus").Text);
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.6: the evidence-quality key draws the graph's own solid, dashed and dotted edges in body ink, in either mode")]
    public async Task TheEvidenceKeyDrawsTheGraphsPatterns()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        EvidenceKeySample[] samples =
            [.. window.GetControl<StackPanel>("EvidenceQualityKey").GetVisualDescendants().OfType<EvidenceKeySample>()];
        Assert.Equal([RelationStrength.Direct, RelationStrength.Correlated, RelationStrength.Candidate],
            samples.Select(sample => sample.Strength));

        // Along each 2 px stroke a direct edge is drawn throughout, a correlated dash runs 12 px and a candidate's dot
        // 4 px, both from the stroke's start a pixel in. Only a weak relation carries the open ring above its middle.
        (int X, int Y, bool[] Drawn)[] probes =
        [
            (3, 6, [true, true, true]),
            (7, 6, [true, true, false]),
            (15, 6, [true, false, false]),
            (18, 2, [false, false, true]),
        ];

        try
        {
            foreach (ThemeMode mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                WriteableBitmap frame = Settle(window);
                Color ink = ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink);
                Color ground = ThemeResources.ToColor(ThemePalette.Surfaces(mode).Elevated);
                for (int index = 0; index < samples.Length; index++)
                {
                    foreach ((int x, int y, bool[] drawn) in probes)
                    {
                        Color seen = At(frame, samples[index].TranslatePoint(new(x, y), window)!.Value);
                        string where = $"{mode}: the {samples[index].Strength} sample at ({x}, {y}) drew {seen}";
                        if (!drawn[index])
                        {
                            Assert.True(seen == ground, where + $", where its pattern leaves the ground {ground}");
                        }
                        else if (y == 2)
                        {
                            // The ring's curve is antialiased; it is the ink within a step of rounding, never a hue.
                            Assert.True(Near(seen, ink), where + $", where the ring is the ink {ink}");
                        }
                        else
                        {
                            Assert.True(seen == ink, where + $", where the stroke is the ink {ink}");
                        }
                    }
                }
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    private static bool Near(Color seen, Color expected) =>
        Math.Abs(seen.R - expected.R) <= 12 && Math.Abs(seen.G - expected.G) <= 12 && Math.Abs(seen.B - expected.B) <= 12;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the bindings and a layout pass and returns the frame, so what the test reads is what is drawn.</summary>
    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }
}
