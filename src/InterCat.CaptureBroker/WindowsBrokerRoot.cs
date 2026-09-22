using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace InterCat.CaptureBroker;

/// <summary>Where a broker root is asked for and under whose security it is provisioned.</summary>
public sealed record BrokerRootRequest
{
    /// <summary>An existing directory the broker trusts to hold its root.</summary>
    public required string TrustedParentDirectory { get; init; }

    /// <summary>The single fixed component created beneath the parent.</summary>
    public required string RootDirectoryName { get; init; }

    /// <summary>The capturing user, who keeps read access to their own evidence and no write access.</summary>
    public required string CapturingUserSid { get; init; }

    public required BrokerRootSecurityPolicy Policy { get; init; }
}

/// <summary>Everything the broker measured about the root it is now holding open.</summary>
public sealed record BrokerRootReport(
    string Path,
    string ConfiguredParentPath,
    string ResolvedParentPath,
    bool CreatedByThisBroker,
    bool SecurityReapplied,
    uint VolumeSerialNumber,
    string? ParentOwnerSid,
    string? OwnerSid,
    int MandatoryIntegrityLevel,
    string AppliedSecurityDescriptorSddl,
    string ObservedSecurityDescriptorSddl);

/// <summary>The fixed production location of the broker root.</summary>
public static class BrokerRootLocation
{
    public const string ProductionRootName = "InterCat";

    public static string ProductionParentDirectory =>
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

    public static BrokerRootRequest Production(string capturingUserSid) => new()
    {
        TrustedParentDirectory = ProductionParentDirectory,
        RootDirectoryName = ProductionRootName,
        CapturingUserSid = capturingUserSid,
        Policy = BrokerRootSecurityPolicy.Production,
    };
}

/// <summary>
/// The broker's own filesystem boundary. The root is created with its protected DACL and mandatory
/// label already applied, is validated from the open handle rather than from the path that was asked
/// for, and stays open for the broker's lifetime without sharing delete access, so the directory that
/// passed validation is the same directory every later write lands in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsBrokerRoot : IBrokerOwnedDirectory, IDisposable
{
    private const uint ReadControl = 0x0002_0000;
    private const uint WriteDac = 0x0004_0000;
    private const uint WriteOwner = 0x0008_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint FileGenericRead = 0x0012_0089;
    private const uint FileGenericWrite = 0x0012_0116;
    private const uint FileTraverse = 0x0000_0020;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint FileShareDelete = 0x0000_0004;
    private const uint CreateNew = 1;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint TruncateExisting = 5;
    private const uint FileFlagWriteThrough = 0x8000_0000;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint FileFlagRandomAccess = 0x1000_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;
    private const uint FileFlagDeleteOnClose = 0x0400_0000;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileAttributeDirectory = 0x0000_0010;
    private const uint FileAttributeReparsePoint = 0x0000_0400;
    private const uint OwnerSecurityInformation = 0x0000_0001;
    private const uint DaclSecurityInformation = 0x0000_0004;
    private const uint LabelSecurityInformation = 0x0000_0010;
    private const uint ProtectedDaclSecurityInformation = 0x8000_0000;
    private const int SeFileObject = 1;
    private const int SddlRevision1 = 1;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    private readonly SafeFileHandle handle;
    private bool disposed;

    private WindowsBrokerRoot(SafeFileHandle handle, BrokerRootReport report)
    {
        this.handle = handle;
        Report = report;
    }

    public BrokerRootReport Report { get; }

    public string Path => Report.Path;

    public uint VolumeSerialNumber => Report.VolumeSerialNumber;

    /// <summary>
    /// Creates or adopts the configured root and returns it held open. Every refusal names what was
    /// observed; nothing here downgrades a boundary to make provisioning succeed.
    /// </summary>
    public static WindowsBrokerRoot Provision(BrokerRootRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The InterCat broker root requires Windows.");
        }

        BrokerRootSecurityPolicy policy = request.Policy
            ?? throw new ArgumentException("A broker root security policy is required.", nameof(request));
        string? policyProblem = policy.Validate();
        if (policyProblem is not null)
        {
            throw new ArgumentException(policyProblem, nameof(request));
        }

        BrokerOwnedFileName.Require(request.RootDirectoryName, nameof(request));
        string userSid = WindowsBrokerTokenIdentity.CanonicalizeSid(request.CapturingUserSid);
        RequireBrokerTokenCanLabel(policy);

        string configuredParent = NormalizeDirectory(request.TrustedParentDirectory, nameof(request));
        (string resolvedParent, uint parentVolume, string? parentOwner) = InspectParent(configuredParent, policy);
        string rootPath = System.IO.Path.Combine(resolvedParent, request.RootDirectoryName);
        string sddl = policy.BuildSecurityDescriptorSddl(userSid);
        bool created = CreateRootDirectory(rootPath, sddl);

        SafeFileHandle rootHandle = OpenDirectory(
            rootPath,
            FileGenericRead | FileTraverse | ReadControl,
            FileShareRead | FileShareWrite);
        try
        {
            BY_HANDLE_FILE_INFORMATION information = ReadFileInformation(rootHandle, rootPath);
            RequireDirectoryIdentity(rootHandle, information, rootPath, parentVolume);
            (string observed, string? problem, bool reapplied) = ApproveOrRepair(
                rootHandle,
                rootPath,
                policy,
                userSid,
                sddl,
                created);
            if (problem is not null)
            {
                throw new UnauthorizedAccessException(
                    $"The broker root '{rootPath}' does not carry its required security: {problem}");
            }

            BrokerSecurityDescriptorFacts facts = BrokerSecurityDescriptorFacts.Parse(observed);
            return new(
                rootHandle,
                new(
                    rootPath,
                    configuredParent,
                    resolvedParent,
                    created,
                    reapplied,
                    information.VolumeSerialNumber,
                    parentOwner,
                    facts.OwnerSid,
                    policy.MandatoryIntegrityLevel,
                    sddl,
                    observed));
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public FileStream OpenOwnedFile(
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BrokerOwnedFileName.Require(name, nameof(name));
        if (mode == FileMode.Append)
        {
            throw new ArgumentException(
                "An owned file is opened at an explicit position; append mode hides where a write landed.",
                nameof(mode));
        }

        string path = System.IO.Path.Combine(Path, name);
        SafeFileHandle file = CreateFile(
            path,
            DesiredAccess(access),
            ShareMode(share),
            CreationDisposition(mode, nameof(mode)),
            FileFlags(options) | FileFlagOpenReparsePoint);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw OpenFailure(path, error);
        }

        try
        {
            BY_HANDLE_FILE_INFORMATION information = ReadFileInformation(file, path);
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException($"Broker file '{path}' is a reparse point and is never written through.");
            }

            if ((information.FileAttributes & FileAttributeDirectory) != 0)
            {
                throw new IOException($"Broker file '{path}' is a directory, not an owned file.");
            }

            if (information.VolumeSerialNumber != VolumeSerialNumber)
            {
                throw new IOException(
                    $"Broker file '{path}' is on volume 0x{information.VolumeSerialNumber:x8}, "
                    + $"not the validated root volume 0x{VolumeSerialNumber:x8}.");
            }

            RequireFinalPath(file, path);
            return new(file, access, bufferSize: 4096, isAsync: (options & FileOptions.Asynchronous) != 0);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            handle.Dispose();
        }
    }

    private static void RequireBrokerTokenCanLabel(BrokerRootSecurityPolicy policy)
    {
        BrokerClientIdentity broker = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        if (policy.RequireElevatedBroker && !broker.IsElevated)
        {
            throw new UnauthorizedAccessException(
                "The broker root is provisioned by an elevated broker; this process is not elevated.");
        }

        if (broker.IntegrityLevel < policy.MandatoryIntegrityLevel)
        {
            throw new UnauthorizedAccessException(
                $"The broker runs at integrity 0x{broker.IntegrityLevel:x4} and cannot label its root "
                + $"0x{policy.MandatoryIntegrityLevel:x4}; Windows never lets a process label above itself.");
        }
    }

    private static string NormalizeDirectory(string directory, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory, parameterName);
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                $"The broker root's parent directory '{full}' must already exist.");
        }

        return LongPathName(full);
    }

    private static (string ResolvedPath, uint Volume, string? OwnerSid) InspectParent(
        string configuredParent,
        BrokerRootSecurityPolicy policy)
    {
        using SafeFileHandle parent = OpenDirectory(
            configuredParent,
            FileGenericRead | FileTraverse | ReadControl,
            FileShareRead | FileShareWrite | FileShareDelete);
        BY_HANDLE_FILE_INFORMATION information = ReadFileInformation(parent, configuredParent);
        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new IOException($"The broker root's parent '{configuredParent}' is not a directory.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException(
                $"The broker root's parent '{configuredParent}' is a reparse point; "
                + "a redirected parent is refused rather than followed.");
        }

        string resolved = FinalPath(parent);
        if (!resolved.Equals(configuredParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The broker root's parent '{configuredParent}' resolves to '{resolved}'; "
                + "the configured path and the opened directory must be the same directory.");
        }

        string? owner = BrokerSecurityDescriptorFacts
            .Parse(ReadSecurityDescriptor(parent, configuredParent))
            .OwnerSid;
        if (policy.RequireTrustedParentOwner && (owner is null || !policy.IsTrustedOwner(owner)))
        {
            throw new UnauthorizedAccessException(
                $"The broker root's parent '{configuredParent}' is owned by {owner ?? "an unreadable principal"}, "
                + "which could rename the directory holding the broker root.");
        }

        return (resolved, information.VolumeSerialNumber, owner);
    }

    private static bool CreateRootDirectory(string rootPath, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out nint descriptor, out _))
        {
            throw NativeFailure($"The broker root security descriptor '{sddl}' is invalid.");
        }

        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            if (CreateDirectory(rootPath, in attributes))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            return error == ErrorAlreadyExists
                ? false
                : throw new IOException(
                    $"The broker could not create its root directory '{rootPath}'.",
                    NativeFailure("CreateDirectoryW failed.", error));
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static (string Observed, string? Problem, bool Reapplied) ApproveOrRepair(
        SafeFileHandle root,
        string rootPath,
        BrokerRootSecurityPolicy policy,
        string userSid,
        string sddl,
        bool created)
    {
        string observed = ReadSecurityDescriptor(root, rootPath);
        string? problem = policy.Approve(BrokerSecurityDescriptorFacts.Parse(observed), userSid);
        if (problem is null)
        {
            return (observed, null, false);
        }

        if (created)
        {
            return (
                observed,
                $"{problem} The broker applied '{sddl}' when it created the directory, so the result was "
                + "changed between creation and validation.",
                false);
        }

        string? owner = BrokerSecurityDescriptorFacts.Parse(observed).OwnerSid;
        if (owner is null || !policy.IsTrustedOwner(owner))
        {
            return (
                observed,
                $"{problem} It is owned by {owner ?? "an unreadable principal"}, so the broker will not "
                + "adopt or repair it; remove or reprovision the directory.",
                false);
        }

        ApplySecurityDescriptor(rootPath, sddl);
        string reread = ReadSecurityDescriptor(root, rootPath);
        string? remaining = policy.Approve(BrokerSecurityDescriptorFacts.Parse(reread), userSid);
        return (reread, remaining, true);
    }

    private static void ApplySecurityDescriptor(string rootPath, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out nint descriptor, out _))
        {
            throw NativeFailure($"The broker root security descriptor '{sddl}' is invalid.");
        }

        try
        {
            if (!GetSecurityDescriptorDacl(descriptor, out bool daclPresent, out nint dacl, out _) || !daclPresent)
            {
                throw NativeFailure("The broker root descriptor carries no discretionary ACL to apply.");
            }

            if (!GetSecurityDescriptorSacl(descriptor, out bool saclPresent, out nint sacl, out _) || !saclPresent)
            {
                throw NativeFailure("The broker root descriptor carries no mandatory label to apply.");
            }

            using SafeFileHandle control = OpenDirectory(
                rootPath,
                ReadControl | WriteDac | WriteOwner,
                FileShareRead | FileShareWrite);
            uint status = SetSecurityInfo(
                control,
                SeFileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation | LabelSecurityInformation,
                0,
                0,
                dacl,
                sacl);
            if (status != 0)
            {
                throw new Win32Exception(
                    (int)status,
                    $"The broker could not re-apply its root security to '{rootPath}'.");
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static void RequireDirectoryIdentity(
        SafeFileHandle root,
        BY_HANDLE_FILE_INFORMATION information,
        string rootPath,
        uint parentVolume)
    {
        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new IOException($"The broker root '{rootPath}' is not a directory.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException(
                $"The broker root '{rootPath}' is a reparse point; a substituted root is refused, not followed.");
        }

        if (information.VolumeSerialNumber != parentVolume)
        {
            throw new IOException(
                $"The broker root '{rootPath}' is on volume 0x{information.VolumeSerialNumber:x8} "
                + $"but its parent is on 0x{parentVolume:x8}; a mounted volume is not the configured root.");
        }

        RequireFinalPath(root, rootPath);
    }

    private static void RequireFinalPath(SafeFileHandle openHandle, string expected)
    {
        string resolved = FinalPath(openHandle);
        if (!resolved.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"'{expected}' resolved to '{resolved}'; the broker writes only to the path it validated.");
        }
    }

    private static SafeFileHandle OpenDirectory(string path, uint desiredAccess, uint shareMode)
    {
        SafeFileHandle directory = CreateFile(
            path,
            desiredAccess,
            shareMode,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (!directory.IsInvalid)
        {
            return directory;
        }

        int error = Marshal.GetLastWin32Error();
        directory.Dispose();
        throw OpenFailure(path, error);
    }

    private static Exception OpenFailure(string path, int error) => error switch
    {
        ErrorAccessDenied => new UnauthorizedAccessException(
            $"The broker was refused access to '{path}'.",
            NativeFailure("CreateFileW was denied.", error)),
        ErrorFileNotFound or ErrorPathNotFound => new FileNotFoundException(
            $"The broker could not open '{path}'.",
            path,
            NativeFailure("CreateFileW could not find the path.", error)),
        _ => new IOException(
            $"The broker could not open '{path}'.",
            NativeFailure("CreateFileW failed.", error)),
    };

    private static BY_HANDLE_FILE_INFORMATION ReadFileInformation(SafeFileHandle openHandle, string path) =>
        GetFileInformationByHandle(openHandle, out BY_HANDLE_FILE_INFORMATION information)
            ? information
            : throw new IOException(
                $"The broker could not read the identity of '{path}'.",
                NativeFailure("GetFileInformationByHandle failed."));

    private static string FinalPath(SafeFileHandle openHandle)
    {
        uint length = GetFinalPathNameByHandle(openHandle, null, 0, 0);
        if (length == 0)
        {
            throw new IOException(
                "The broker could not resolve the final path of an open handle.",
                NativeFailure("GetFinalPathNameByHandleW failed."));
        }

        char[] buffer = new char[length + 1];
        uint written = GetFinalPathNameByHandle(openHandle, buffer, (uint)buffer.Length, 0);
        if (written == 0 || written >= buffer.Length)
        {
            throw new IOException(
                "The broker could not read the final path of an open handle.",
                NativeFailure("GetFinalPathNameByHandleW failed."));
        }

        string resolved = new(buffer, 0, (int)written);
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return string.Concat(@"\\", resolved.AsSpan(8));
        }

        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    private static string LongPathName(string path)
    {
        uint length = GetLongPathName(path, null, 0);
        if (length == 0)
        {
            throw new IOException(
                $"The broker could not resolve the long form of '{path}'.",
                NativeFailure("GetLongPathNameW failed."));
        }

        char[] buffer = new char[length + 1];
        uint written = GetLongPathName(path, buffer, (uint)buffer.Length);
        return written == 0 || written >= buffer.Length
            ? throw new IOException(
                $"The broker could not read the long form of '{path}'.",
                NativeFailure("GetLongPathNameW failed."))
            : new(buffer, 0, (int)written);
    }

    private static string ReadSecurityDescriptor(SafeFileHandle openHandle, string path)
    {
        uint status = GetSecurityInfo(
            openHandle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation | LabelSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out nint descriptor);
        if (status != 0)
        {
            throw new Win32Exception((int)status, $"The broker could not read the security of '{path}'.");
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                descriptor,
                SddlRevision1,
                OwnerSecurityInformation | DaclSecurityInformation | LabelSecurityInformation,
                out nint text,
                out _))
            {
                throw NativeFailure($"The broker could not render the security of '{path}'.");
            }

            try
            {
                return Marshal.PtrToStringUni(text)
                    ?? throw new InvalidDataException($"Windows returned an empty descriptor for '{path}'.");
            }
            finally
            {
                _ = LocalFree(text);
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static uint DesiredAccess(FileAccess access) => access switch
    {
        FileAccess.Read => FileGenericRead | Synchronize,
        FileAccess.Write => FileGenericWrite | Synchronize,
        FileAccess.ReadWrite => FileGenericRead | FileGenericWrite | Synchronize,
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unsupported owned-file access."),
    };

    private static uint ShareMode(FileShare share)
    {
        uint mode = 0;
        mode |= (share & FileShare.Read) != 0 ? FileShareRead : 0;
        mode |= (share & FileShare.Write) != 0 ? FileShareWrite : 0;
        mode |= (share & FileShare.Delete) != 0 ? FileShareDelete : 0;
        return mode;
    }

    private static uint CreationDisposition(FileMode mode, string parameterName) => mode switch
    {
        FileMode.CreateNew => CreateNew,
        FileMode.Create => CreateAlways,
        FileMode.Open => OpenExisting,
        FileMode.OpenOrCreate => OpenAlways,
        FileMode.Truncate => TruncateExisting,
        _ => throw new ArgumentOutOfRangeException(parameterName, mode, "Unsupported owned-file mode."),
    };

    private static uint FileFlags(FileOptions options)
    {
        uint flags = 0;
        flags |= (options & FileOptions.WriteThrough) != 0 ? FileFlagWriteThrough : 0;
        flags |= (options & FileOptions.Asynchronous) != 0 ? FileFlagOverlapped : 0;
        flags |= (options & FileOptions.RandomAccess) != 0 ? FileFlagRandomAccess : 0;
        flags |= (options & FileOptions.SequentialScan) != 0 ? FileFlagSequentialScan : 0;
        flags |= (options & FileOptions.DeleteOnClose) != 0 ? FileFlagDeleteOnClose : 0;
        return flags;
    }

    private static Win32Exception NativeFailure(string message, int? error = null) =>
        new(error ?? Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, in SECURITY_ATTRIBUTES securityAttributes);

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
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out BY_HANDLE_FILE_INFORMATION information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[]? longPath, uint bufferLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        int stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        nint securityDescriptor,
        int requestedStringSdRevision,
        uint securityInformation,
        out nint stringSecurityDescriptor,
        out uint stringSecurityDescriptorLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorDacl(
        nint securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out nint dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(
        nint securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out nint sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    private static SafeFileHandle CreateFile(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flagsAndAttributes) =>
        CreateFile(path, desiredAccess, shareMode, 0, creationDisposition, flagsAndAttributes, 0);
}
