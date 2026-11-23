using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>Whose the protection is: the machine's, which any of its administrators' processes reads, or one user's.</summary>
public enum ProtectionScope
{
    Machine,
    CurrentUser,
}

/// <summary>
/// A key at rest, protected by the system's data protection (DPAPI) for this machine or this user, with InterCat's own
/// entropy, so another program's protected data never reads as InterCat's. It is kept in a folder only its owners can read
/// (<see cref="RemoteFolder"/>): the protection guards the bytes off the machine, the folder on it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static partial class LocalProtection
{
    private const int UiForbidden = 0x1;
    private const int LocalMachine = 0x4;
    private static readonly byte[] Entropy = "InterCat remote identity v1"u8.ToArray();

    /// <summary>The bytes protected for <paramref name="scope"/>.</summary>
    public static byte[] Protect(ReadOnlySpan<byte> data, ProtectionScope scope) =>
        Transform(data, scope, protect: true);

    /// <summary>Protected bytes read back; refused where they were protected for another machine, user or purpose.</summary>
    /// <exception cref="CryptographicException">The bytes are not InterCat's protected for this scope, or were altered.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> data, ProtectionScope scope) =>
        Transform(data, scope, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> data, ProtectionScope scope, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            int flags = UiForbidden | (scope == ProtectionScope.Machine ? LocalMachine : 0);
            bool done = protect
                ? CryptProtectData(in inBlob, 0, in entropyBlob, 0, 0, flags, out DataBlob output)
                : CryptUnprotectData(in inBlob, 0, in entropyBlob, 0, 0, flags, out output);
            if (!done)
            {
                throw new CryptographicException(
                    protect ? "The system could not protect InterCat's key." : "The saved key is not InterCat's for this machine or user, or was altered.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                new Span<byte>((void*)output.Data, output.Size).Clear();
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(in DataBlob dataIn, nint description, in DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(in DataBlob dataIn, nint description, in DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
