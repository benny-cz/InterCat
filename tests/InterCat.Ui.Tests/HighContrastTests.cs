using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using Xunit;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.1 and §26.2: the operating system's high-contrast setting gets a high-contrast set, dark or light as its scheme
/// is, and in it every structural and interactive edge can be seen. Leaving it hands the control theme its look back.
/// </summary>
public sealed class HighContrastTests
{
    [Fact(DisplayName = "§26.2: the platform's high-contrast setting picks a high-contrast set, dark or light as its scheme is")]
    public void ThePlatformSettingPicksTheMode()
    {
        Assert.Equal(ThemeMode.Dark, App.ModeFor(null));
        Assert.Equal(ThemeMode.Dark, App.ModeFor(new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark }));
        Assert.Equal(ThemeMode.Light, App.ModeFor(new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light }));
        Assert.Equal(ThemeMode.HighContrastDark, App.ModeFor(new PlatformColorValues
        {
            ThemeVariant = PlatformThemeVariant.Dark,
            ContrastPreference = ColorContrastPreference.High,
        }));
        Assert.Equal(ThemeMode.HighContrastLight, App.ModeFor(new PlatformColorValues
        {
            ThemeVariant = PlatformThemeVariant.Light,
            ContrastPreference = ColorContrastPreference.High,
        }));
    }

    [AvaloniaFact(DisplayName = "§6.1: in high contrast a pane's, a button's and a text box's edges are drawn in the visible divider, and an ordinary mode hands the control theme its look back")]
    public void HighContrastEdgesAreSeen()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        Border rail = window.GetControl<Border>("Rail");
        Button open = window.GetControl<Button>("OpenSavedSessionButton");
        TextBox search = window.GetControl<TextBox>("SearchBox");
        Border card = Assert.IsType<Border>(window.GetControl<StackPanel>("EvidenceQualityKey").Parent);

        try
        {
            foreach (ThemeMode mode in new[] { ThemeMode.HighContrastDark, ThemeMode.HighContrastLight })
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                WriteableBitmap frame = Settle(window);
                SurfaceTokens surfaces = ThemePalette.Surfaces(mode);
                Color divider = ThemeResources.ToColor(surfaces.Divider);

                // The rail's right edge, a card's, a button's and the search box's top edges: each a line to be seen.
                Assert.Equal(divider, At(frame, rail.TranslatePoint(new(rail.Bounds.Width - 1, rail.Bounds.Height / 2), window)!.Value));
                Assert.Equal(divider, At(frame, card.TranslatePoint(new(card.Bounds.Width / 2, 0), window)!.Value));
                Assert.Equal(divider, At(frame, open.TranslatePoint(new(open.Bounds.Width / 2, 0), window)!.Value));
                Assert.Equal(divider, At(frame, search.TranslatePoint(new(search.Bounds.Width / 2, 0), window)!.Value));

                // Inside its edge the button is the elevated face the report measures its label on.
                Assert.Equal(ThemeResources.ToColor(surfaces.Elevated),
                    At(frame, open.TranslatePoint(new(6, open.Bounds.Height / 2), window)!.Value));
                Assert.Equal(ThemeResources.ToColor(surfaces.Ink), ColorOf(open.Foreground));
            }

            // An ordinary mode keeps the control theme's own chrome: none of the high-contrast keys is left behind.
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            WriteableBitmap ordinary = Settle(window);
            Assert.All(ThemeResources.ControlChromeKeys,
                key => Assert.False(Avalonia.Application.Current!.Resources.ContainsKey(key), key));
            Assert.NotEqual(ThemeResources.ToColor(ThemePalette.Surfaces(ThemeMode.HighContrastDark).Divider),
                At(ordinary, open.TranslatePoint(new(open.Bounds.Width / 2, 0), window)!.Value));

            // There a card's edge is its own tone, so the edge that high contrast shows is no seam in the ordinary modes.
            Assert.Equal(ThemeResources.ToColor(ThemePalette.Surfaces(ThemeMode.Dark).Elevated),
                At(ordinary, card.TranslatePoint(new(card.Bounds.Width / 2, 0), window)!.Value));
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
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
