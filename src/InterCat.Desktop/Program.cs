using Avalonia;

namespace InterCat.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>

        // A finished capture's checkpoint is published in a process of its own, which shows no window (revision 884).
        args is [CheckpointLaunchTarget.DesktopArgument, string session]
            ? CheckpointProcess.RunOverStandardOutput(session, CancellationToken.None)
            : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
