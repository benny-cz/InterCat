using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using InterCat.Desktop.Theme;

namespace InterCat.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>
    /// A mode the application keeps whatever the platform reports. Tests pin one, so a rendering does not depend on the
    /// machine running them; the product itself follows the operating system (§26.2).
    /// </summary>
    internal static ThemeMode? PinnedMode { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Tokens come from the verified palette, so no view carries a colour literal (section 6.6). The mode follows the
        // operating system's light or dark setting (§26.2); there is no high-contrast token set yet, so a high-contrast
        // setting keeps the light or dark one it is paired with.
        ThemeResources.Apply(this, PinnedMode ?? PlatformMode());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (PinnedMode is null && PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => ThemeResources.Apply(this, PinnedMode ?? PlatformMode()));
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ThemeMode PlatformMode() =>
        PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? ThemeMode.Light : ThemeMode.Dark;
}
