using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using InterCat.Desktop;
using InterCat.Desktop.Settings;
using InterCat.Desktop.Theme;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>§26.3 in the window: the theme the user chooses is applied at once and kept in the settings file.</summary>
public sealed class ThemeSettingsWindowTests
{
    [AvaloniaFact(DisplayName = "§26.3: a theme chosen from the header's menu is applied at once and kept in the settings file")]
    public void AChosenThemeIsAppliedAndKept()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Settings", Guid.NewGuid().ToString("N"));
        var store = new ApplicationSettingsStore(Path.Combine(directory, "settings.json"));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        ThemeMode? pinned = App.PinnedMode;
        App app = Assert.IsType<App>(Avalonia.Application.Current);

        // The product's path: nothing pinned, so the user's choice is what applies.
        App.PinnedMode = null;
        try
        {
            window.UseSettings(store, store.Load());
            Button theme = window.GetControl<Button>("ThemeButton");
            MenuFlyout menu = Assert.IsType<MenuFlyout>(theme.Flyout);
            Assert.Equal(
                ["Follow the system", "Dark", "Light", "High contrast, dark", "High contrast, light"],
                Choices(menu).Select(item => (string)item.Header!));
            Assert.True(Choices(menu)[0].IsChecked);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => !item.IsEnabled && (string)item.Header! == "Kept in " + store.FilePath);

            // A click on "Light", as the menu delivers it.
            Choices(menu)[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatch();
            Assert.Equal(ThemeMode.Light, ThemeResources.CurrentMode);
            Assert.Equal(ThemeMode.Light, new ApplicationSettingsStore(store.FilePath).Load().Theme);
            Assert.True(Choices(menu)[2].IsChecked);
            Assert.False(Choices(menu)[0].IsChecked);

            // The header keeps its title legible at the minimum window beside the new button.
            _ = window.CaptureRenderedFrame();
            Assert.True(window.GetControl<TextBlock>("HeaderTitle").Bounds.Width >= 100,
                $"the header title has {window.GetControl<TextBlock>("HeaderTitle").Bounds.Width:F0} px");

            // Following the system again gives the platform's own mode, and the file says so.
            window.ChooseTheme(null);
            Dispatch();
            Assert.Equal(App.ModeFor(app.PlatformSettings?.GetColorValues()), ThemeResources.CurrentMode);
            Assert.Null(new ApplicationSettingsStore(store.FilePath).Load().Theme);
            Assert.True(Choices(menu)[0].IsChecked);
        }
        finally
        {
            App.PinnedMode = pinned;
            app.ChooseTheme(null);
            ThemeResources.Apply(app, ThemeMode.Dark);
            window.Close();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MenuItem[] Choices(MenuFlyout menu) =>
        [.. menu.Items.OfType<MenuItem>().Where(item => item.ToggleType == MenuItemToggleType.Radio)];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
