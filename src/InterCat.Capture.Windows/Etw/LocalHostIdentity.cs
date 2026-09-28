using System.Security;
using InterCat.Domain;
using Microsoft.Win32;

namespace InterCat.Capture.Windows;

/// <summary>
/// The host identity a live capture on this machine records: its Windows installation's machine GUID, which setup mints
/// and a generalized image mints anew, together with the machine's name and build (<see cref="HostId.ForLocalMachine"/>).
/// It is read once per process; a process that cannot read it records a host of its own that matches no other.
/// </summary>
public static class LocalHostIdentity
{
    private static readonly Lazy<HostId> Identity = new(() => HostId.ForLocalMachine(InstallationId()));

    public static HostId Current => Identity.Value;

    /// <summary>The installation's machine GUID, from the 64-bit registry view any user may read; null when it cannot be.</summary>
    public static string? InstallationId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? cryptography = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return cryptography?.GetValue("MachineGuid") is string id && Guid.TryParse(id, out _) ? id : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
