using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using InterCat.Desktop.Settings;
using InterCat.Desktop.Theme;

namespace InterCat.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>
    /// A mode the application keeps whatever the platform reports. Tests pin one, so a rendering does not depend on the
    /// machine running them; the product itself follows the operating system (§26.2).
    /// </summary>
    internal static ThemeMode? PinnedMode { get; set; }

    /// <summary>The theme the user chose in the application's settings, or null to follow the operating system.</summary>
    internal static ThemeMode? ChosenMode { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Tokens come from the verified palette, so no view carries a colour literal (section 6.6). The mode follows the
        // operating system's light or dark setting and its high-contrast setting (§26.2), unless the user chose one.
        ApplyTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (PinnedMode is null && PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(ApplyTheme);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Only the application reads the user's own settings and session folder, never a test (§26.3).
            ApplicationSettingsStore? store = null;
            ApplicationSettings loaded = ApplicationSettings.Default;
            try
            {
                store = new ApplicationSettingsStore(ApplicationSettingsStore.DefaultPath());
                loaded = store.Load();
                ChooseTheme(loaded.Theme);
            }
            catch (InvalidOperationException)
            {
                // Windows gave no per-user settings folder: the defaults stand, and a choice lasts until InterCat closes.
            }

            var window = new MainWindow();
            window.UseSettings(store, loaded);
            desktop.MainWindow = window;

            // A viewer that crashed while recording loses no evidence; this launch offers to finish its session (§3.1
            // step 6), and lists the sessions saved before.
            try
            {
                _ = window.UseSessionRootAsync(DesktopCaptureRunner.DefaultSessionRoot());
            }
            catch (InvalidOperationException)
            {
                // Windows gave no local user-data folder, so no capture could have saved a session there either.
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies a theme the user chose, or the operating system's when null (§26.2, §26.3).</summary>
    internal void ChooseTheme(ThemeMode? choice)
    {
        ChosenMode = choice;
        ApplyTheme();
    }

    /// <summary>
    /// The mode for what the platform reports: its high-contrast setting picks a high-contrast set, and its light or dark
    /// scheme picks which. A platform that reports nothing is drawn dark.
    /// </summary>
    internal static ThemeMode ModeFor(PlatformColorValues? values) =>
        (values?.ContrastPreference == ColorContrastPreference.High, values?.ThemeVariant == PlatformThemeVariant.Light) switch
        {
            (true, true) => ThemeMode.HighContrastLight,
            (true, false) => ThemeMode.HighContrastDark,
            (false, true) => ThemeMode.Light,
            _ => ThemeMode.Dark,
        };

    private void ApplyTheme() =>
        ThemeResources.Apply(this, PinnedMode ?? ChosenMode ?? ModeFor(PlatformSettings?.GetColorValues()));
}
