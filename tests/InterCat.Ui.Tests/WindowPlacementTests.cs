using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using InterCat.Desktop;
using InterCat.Desktop.Settings;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// InterCat's window opens where and as large as it last closed, maximized if it was, wherever a screen still shows its
/// title bar, and keeps where it closes in the application's settings (§26.3).
/// </summary>
public sealed class WindowPlacementTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("intercat-placement-").FullName;

    private ApplicationSettingsStore Store => new(Path.Combine(directory, "settings.json"));

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [AvaloniaFact(DisplayName = "§26.3: the window opens where and as large as it last closed, and keeps where it closes")]
    public void TheWindowOpensWhereItClosed()
    {
        // Its first close keeps where the system placed it.
        var first = new MainWindow();
        first.UseSettings(Store, Store.Load());
        first.Show();
        Dispatch();
        PixelPoint placed = first.Position;
        first.Close();
        Assert.Equal(new WindowPlacement(placed.X, placed.Y, 1_440, 900, false), Store.Load().Placement);

        Assert.True(Store.SavePlacement(new WindowPlacement(200, 150, 1_300, 800, Maximized: false)));
        var window = new MainWindow();
        window.UseSettings(Store, Store.Load());
        window.Show();
        Dispatch();
        Assert.Equal((new PixelPoint(200, 150), new Size(1_300, 800)), (window.Position, window.ClientSize));

        // Moved and made larger, it keeps where it closed.
        window.Position = new PixelPoint(260, 180);
        window.Width = 1_400;
        Dispatch();
        window.Close();
        Assert.Equal(new WindowPlacement(260, 180, 1_400, 800, false), Store.Load().Placement);
    }

    [AvaloniaFact(DisplayName = "§26.3: a window that closed maximized opens maximized, keeping where it stood before for when it no longer is")]
    public void AMaximizedWindowOpensMaximized()
    {
        Assert.True(Store.SavePlacement(new WindowPlacement(120, 90, 1_200, 760, Maximized: true)));
        var window = new MainWindow();
        window.UseSettings(Store, Store.Load());
        window.Show();
        Dispatch();
        Assert.Equal(WindowState.Maximized, window.WindowState);

        // Where the platform puts a maximized window is not where it returns to.
        window.Position = new PixelPoint(0, 0);
        window.Width = 1_920;
        Dispatch();
        window.Close();
        Assert.Equal(new WindowPlacement(120, 90, 1_200, 760, true), Store.Load().Placement);

        // One maximized after it opened keeps that, with where it stood before.
        window = new MainWindow();
        window.UseSettings(Store, Store.Load() with { Placement = null });
        window.Show();
        Dispatch();
        window.Position = new PixelPoint(300, 200);
        Dispatch();
        window.WindowState = WindowState.Maximized;
        window.Position = new PixelPoint(0, 0);
        Dispatch();
        window.Close();
        Assert.Equal(new WindowPlacement(300, 200, 1_440, 900, true), Store.Load().Placement);
    }

    [AvaloniaFact(DisplayName = "§26.3: a window kept where no screen shows its title bar opens as InterCat's window first opens")]
    public void AWindowNoScreenShowsOpensWhereTheSystemPlacesIt()
    {
        Assert.True(Store.SavePlacement(new WindowPlacement(5_000, 100, 1_300, 800, Maximized: false)));
        var window = new MainWindow();
        window.UseSettings(Store, Store.Load());
        window.Show();
        Dispatch();
        Assert.NotEqual(new PixelPoint(5_000, 100), window.Position);
        Assert.Equal(new Size(1_440, 900), window.ClientSize);
        window.Close();
    }

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();
}
