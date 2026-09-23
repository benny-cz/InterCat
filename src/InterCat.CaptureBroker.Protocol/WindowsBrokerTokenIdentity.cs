using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker;

/// <summary>Reads broker identities from Windows access tokens; request payloads are never consulted.</summary>
public static partial class WindowsBrokerTokenIdentity
{
    private const uint TokenQuery = 0x0008;
    private const int ErrorInsufficientBuffer = 122;

    public static BrokerClientIdentity ReadCurrentProcess()
    {
        RequireWindows();
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out SafeAccessTokenHandle token))
        {
            throw NativeFailure("The broker could not open its process token.");
        }

        using (token)
        {
            return Read(token, requireImpersonationToken: false);
        }
    }

    public static BrokerClientIdentity ReadNamedPipeClient(SafePipeHandle pipeHandle)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            throw new ArgumentException("A live named-pipe handle is required.", nameof(pipeHandle));
        }

        if (!ImpersonateNamedPipeClient(pipeHandle))
        {
            throw NativeFailure("The broker could not impersonate the named-pipe client.");
        }

        BrokerClientIdentity identity;
        try
        {
            if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out SafeAccessTokenHandle token))
            {
                throw NativeFailure("The broker could not open the impersonated client token.");
            }

            using (token)
            {
                identity = Read(token, requireImpersonationToken: true);
            }
        }
        catch
        {
            if (!RevertToSelf())
            {
                Environment.FailFast(
                    "The broker could not end named-pipe client impersonation after an authentication failure.");
            }

            throw;
        }

        if (!RevertToSelf())
        {
            throw NativeFailure("The broker could not end named-pipe client impersonation.");
        }

        return identity;
    }

    public static string CanonicalizeSid(string sid)
    {
        RequireWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        if (!ConvertStringSidToSid(sid, out nint nativeSid))
        {
            throw new ArgumentException("The user SID is not a valid Windows SID.", nameof(sid));
        }

        try
        {
            return SidToString(nativeSid);
        }
        finally
        {
            _ = LocalFree(nativeSid);
        }
    }

    private static BrokerClientIdentity Read(
        SafeAccessTokenHandle token,
        bool requireImpersonationToken)
    {
        TOKEN_STATISTICS statistics = ReadStructure<TOKEN_STATISTICS>(token, TOKEN_INFORMATION_CLASS.TokenStatistics);
        if (requireImpersonationToken
            && (statistics.TokenType != TOKEN_TYPE.TokenImpersonation
                || statistics.ImpersonationLevel < SECURITY_IMPERSONATION_LEVEL.SecurityIdentification))
        {
            throw new UnauthorizedAccessException(
                "The named-pipe client did not provide an identifiable impersonation token.");
        }

        string userSid = ReadSid(token, TOKEN_INFORMATION_CLASS.TokenUser);
        string integritySid = ReadSid(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel);
        int separator = integritySid.LastIndexOf('-');
        if (separator < 0
            || !int.TryParse(
                integritySid.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int integrityLevel)
            || integrityLevel <= 0)
        {
            throw new InvalidDataException("The Windows token has an invalid integrity SID.");
        }

        TOKEN_ELEVATION elevation = ReadStructure<TOKEN_ELEVATION>(token, TOKEN_INFORMATION_CLASS.TokenElevation);
        ulong logonSessionId = unchecked(
            ((ulong)(uint)statistics.AuthenticationId.HighPart << 32)
            | statistics.AuthenticationId.LowPart);
        var identity = new BrokerClientIdentity(
            userSid,
            logonSessionId,
            integrityLevel,
            elevation.TokenIsElevated != 0);
        string? problem = identity.Validate();
        return problem is null
            ? identity
            : throw new InvalidDataException($"The Windows token identity is invalid: {problem}");
    }

    private static T ReadStructure<T>(
        SafeAccessTokenHandle token,
        TOKEN_INFORMATION_CLASS informationClass)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        using var buffer = new NativeBuffer(size);
        if (!GetTokenInformation(token, informationClass, buffer.Pointer, size, out int written)
            || written < size
            || written > size)
        {
            throw NativeFailure($"The broker could not read fixed token information {informationClass}.");
        }

        return Marshal.PtrToStructure<T>(buffer.Pointer);
    }

    private static string ReadSid(
        SafeAccessTokenHandle token,
        TOKEN_INFORMATION_CLASS informationClass)
    {
        using NativeBuffer buffer = Query(token, informationClass);
        if (buffer.Length < IntPtr.Size)
        {
            throw new InvalidDataException($"Token SID information {informationClass} is truncated.");
        }

        nint sid = Marshal.ReadIntPtr(buffer.Pointer);
        if (sid == 0 || !IsValidSid(sid))
        {
            throw new InvalidDataException($"Token SID information {informationClass} is invalid.");
        }

        return SidToString(sid);
    }

    private static NativeBuffer Query(
        SafeAccessTokenHandle token,
        TOKEN_INFORMATION_CLASS informationClass)
    {
        _ = GetTokenInformation(token, informationClass, 0, 0, out int required);
        int error = Marshal.GetLastWin32Error();
        if (required <= 0 || error != ErrorInsufficientBuffer)
        {
            throw NativeFailure($"The broker could not size token information {informationClass}.", error);
        }

        var buffer = new NativeBuffer(required);
        if (!GetTokenInformation(token, informationClass, buffer.Pointer, required, out int written)
            || written <= 0
            || written > required)
        {
            int readError = Marshal.GetLastWin32Error();
            buffer.Dispose();
            throw NativeFailure($"The broker could not read token information {informationClass}.", readError);
        }

        buffer.Length = written;
        return buffer;
    }

    private static string SidToString(nint sid)
    {
        if (!ConvertSidToStringSid(sid, out nint value))
        {
            throw NativeFailure("The broker could not render a Windows SID.");
        }

        try
        {
            return Marshal.PtrToStringUni(value)
                ?? throw new InvalidDataException("Windows returned an empty SID string.");
        }
        finally
        {
            _ = LocalFree(value);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Broker token authentication requires Windows.");
        }
    }

    private static Win32Exception NativeFailure(string message, int? error = null) =>
        new(error ?? Marshal.GetLastWin32Error(), message);

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int length)
        {
            Pointer = Marshal.AllocHGlobal(length);
            Length = length;
        }

        public nint Pointer { get; }
        public int Length { get; set; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_STATISTICS
    {
        public LUID TokenId;
        public LUID AuthenticationId;
        public long ExpirationTime;
        public TOKEN_TYPE TokenType;
        public SECURITY_IMPERSONATION_LEVEL ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public LUID ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation = 2,
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation,
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenStatistics = 10,
        TokenElevation = 20,
        TokenIntegrityLevel = 25,
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(
        nint threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateNamedPipeClient(SafePipeHandle namedPipeHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string stringSid, out nint sid);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsValidSid(nint sid);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
