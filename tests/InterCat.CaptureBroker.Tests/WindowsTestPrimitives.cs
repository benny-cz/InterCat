using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// Writes a real directory junction so the reparse-point refusals are measured against the same
/// substitution an attacker would perform, not against a simulated attribute.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class DirectoryJunction
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint ReparseTagMountPoint = 0xA000_0003;

    public static void Create(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);
        string substitute = $@"\??\{Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath))}";
        byte[] substituteBytes = System.Text.Encoding.Unicode.GetBytes(substitute);
        int pathBufferLength = substituteBytes.Length + 2 + 2;
        byte[] buffer = new byte[8 + 8 + pathBufferLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), ReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)(8 + pathBufferLength));
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)substituteBytes.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), (ushort)(substituteBytes.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), (ushort)0);
        substituteBytes.CopyTo(buffer, 16);

        using SafeFileHandle handle = CreateFile(
            junctionPath,
            GenericWrite,
            0,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open '{junctionPath}'.");
        }

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not write a junction at '{junctionPath}'.");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In] byte[] inBuffer,
        int inBufferSize,
        nint outBuffer,
        int outBufferSize,
        out int bytesReturned,
        nint overlapped);
}

/// <summary>
/// The current user's token lowered to a chosen mandatory level. Lowering is always permitted, so the
/// broker's write refusal can be proved by an ordinary-integrity caller without a second account.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class LoweredIntegrityToken : IDisposable
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenImpersonate = 0x0004;
    private const uint SeGroupIntegrity = 0x0000_0020;
    private const int TokenIntegrityLevel = 25;
    private const int SecurityImpersonationLevel = 2;
    private const int TokenTypeImpersonation = 2;

    private readonly SafeAccessTokenHandle token;
    private bool disposed;

    private LoweredIntegrityToken(SafeAccessTokenHandle token) => this.token = token;

    public static LoweredIntegrityToken Create(int integrityLevel)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out SafeAccessTokenHandle process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the test process token.");
        }

        using (process)
        {
            if (!DuplicateTokenEx(
                process,
                TokenQuery | TokenDuplicate | TokenImpersonate | TokenAdjustDefault,
                0,
                SecurityImpersonationLevel,
                TokenTypeImpersonation,
                out SafeAccessTokenHandle duplicate))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the test process token.");
            }

            try
            {
                Apply(duplicate, integrityLevel);
                return new(duplicate);
            }
            catch
            {
                duplicate.Dispose();
                throw;
            }
        }
    }

    /// <summary>Runs one action while impersonating the lowered token and always reverts.</summary>
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ImpersonateLoggedOnUser(token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not impersonate the lowered token.");
        }

        try
        {
            action();
        }
        finally
        {
            if (!RevertToSelf())
            {
                Environment.FailFast("A test could not revert from a lowered-integrity token.");
            }
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            token.Dispose();
        }
    }

    private static void Apply(SafeAccessTokenHandle target, int integrityLevel)
    {
        if (!ConvertStringSidToSid(BrokerIntegrityLevel.ToSid(integrityLevel), out nint sid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not build the integrity SID.");
        }

        try
        {
            var label = new TOKEN_MANDATORY_LABEL
            {
                Sid = sid,
                Attributes = SeGroupIntegrity,
            };
            int size = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>();
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(label, buffer, fDeleteOld: false);
                if (!SetTokenInformation(target, TokenIntegrityLevel, buffer, size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not lower the duplicated token's integrity.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = LocalFree(sid);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string stringSid, out nint sid);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
