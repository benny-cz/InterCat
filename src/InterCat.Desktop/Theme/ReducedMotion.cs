using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InterCat.Desktop.Theme;

/// <summary>
/// Whether motion is reduced (§6.8, §26.2): the platform's setting, read as each animation would start, so turning it on
/// stills the next change. Under it a change is drawn at once where it ends, so no value, final position or selection
/// depends on whether it animated. On Windows the setting is Animation effects (Show animations in Windows), which turns
/// off the animations inside windows; a platform whose setting InterCat cannot read animates, as Windows does by default.
/// </summary>
internal static partial class ReducedMotion
{
    /// <summary>
    /// An answer the application keeps whatever the platform reports. Tests pin motion off, so a drawing they read does not
    /// depend on the machine running them or on the moment it is read; one that measures motion turns it back on.
    /// </summary>
    internal static bool? Pinned { get; set; }

    /// <summary>Whether a change starting now is drawn at once where it ends.</summary>
    internal static bool IsOn => Pinned ?? (OperatingSystem.IsWindows() && !WindowsAnimates());

    // SPI_GETCLIENTAREAANIMATION. A read that fails animates, as Windows does unless the setting is turned off.
    private const uint GetClientAreaAnimation = 0x1042;

    [SupportedOSPlatform("windows")]
    private static bool WindowsAnimates() =>
        !SystemParametersInfo(GetClientAreaAnimation, 0, out int animates, 0) || animates != 0;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);
}
