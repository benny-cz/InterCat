using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-PROC-001 run: where its scenario is written.</summary>
internal sealed record ProcessPackageOptions
{
    public const string ScenarioId = "FX-PROC-001";

    public required string TruthDirectory { get; init; }
}

/// <summary>
/// FX-PROC-001: a process starts two children, each created suspended so its identity is read before it runs - one in a
/// package, App Installer's winget through its execution alias, asked only its version, and one in none, a child of the
/// workload's own that exits at once. For each it reads, by the application model and the child's token rather than by any
/// trace, its package full name and application user model ID - its package's family and the application's ID in it - its
/// token's elevation type, whether it is elevated, its integrity level and its session; then resumes it and waits for it,
/// and writes what it read, by each child's PID and creation time, into the scenario. It reads no InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessPackageScenario
{
    private const uint CreateSuspended = 0x0000_0004;
    private const uint CreateNoWindow = 0x0800_0000;
    private const uint TokenQuery = 0x0008;
    private const int TokenSessionId = 12;
    private const int TokenElevationType = 18;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const uint Infinite = 0xFFFF_FFFF;

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Starts both children suspended, reads each one's identity, runs it to its end, and writes the scenario.</summary>
    public static async Task<int> RunAsync(ProcessPackageOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // App Installer's execution alias: a reparse point the process creation resolves to the package's own winget.
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(winget))
        {
            await Console.Error.WriteLineAsync("App Installer's winget is not installed for this user, so no packaged child can be started.")
                .ConfigureAwait(false);
            return 2;
        }

        Child packaged = Run("packaged", winget, $"\"{winget}\" --version");
        Child unpackaged = Run("unpackaged", executable, $"\"{executable}\" process-package-child --truth \"{options.TruthDirectory}\"");
        var summary = new
        {
            scenarioId = ProcessPackageOptions.ScenarioId,
            workloadProcessId = Environment.ProcessId,
            children = new[] { packaged, unpackaged },
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return packaged is { ExitCode: 0, PackageFullName: not null } && unpackaged is { ExitCode: 0, PackageFullName: null } ? 0 : 1;
    }

    // One child created suspended, its identity read before it runs, then resumed and waited for.
    private static unsafe Child Run(string role, string image, string commandLine)
    {
        var startup = new StartupInfo { Size = sizeof(StartupInfo) };
        ProcessInformation created;
        char[] line = [.. commandLine, '\0'];
        fixed (char* writable = line)
        {
            if (!CreateProcessW(null, writable, 0, 0, false, CreateSuspended | CreateNoWindow, 0, null, &startup, &created))
            {
                throw new InvalidOperationException($"The {role} child could not be created ({Marshal.GetLastPInvokeError()}).");
            }
        }

        try
        {
            (string? package, int packageStatus) = Text(created.Process, static (process, length, buffer) => GetPackageFullName(process, length, buffer));
            (string? model, _) = Text(created.Process, static (process, length, buffer) => GetApplicationUserModelId(process, length, buffer));
            (int elevationType, bool elevated, string? integrity, int session) = Token(created.Process);
            _ = GetProcessTimes(created.Process, out long creation, out _, out _, out _);
            _ = ResumeThread(created.Thread);
            _ = WaitForSingleObject(created.Process, Infinite);
            _ = GetExitCodeProcess(created.Process, out uint exitCode);
            return new(role, created.ProcessId, creation, Path.GetFileName(image), package, packageStatus, model,
                model is not null && model.IndexOf('!', StringComparison.Ordinal) is var bang and >= 0 ? model[(bang + 1)..] : null,
                elevationType, elevated, integrity, session, (int)exitCode);
        }
        finally
        {
            _ = CloseHandle(created.Thread);
            _ = CloseHandle(created.Process);
        }
    }

    // A string the application model gives of a process, asked once for its length and once for itself; null where the
    // process has none, with the status that said so.
    private static unsafe (string? Text, int Status) Text(nint process, Ask ask)
    {
        uint length = 0;
        int status = ask(process, &length, null);
        if (status != ErrorInsufficientBuffer)
        {
            return (null, status);
        }

        char[] buffer = new char[length];
        fixed (char* text = buffer)
        {
            status = ask(process, &length, text);
        }

        return status == ErrorSuccess ? (new string(buffer, 0, (int)Math.Max(0, length - 1)), status) : (null, status);
    }

    // The child's token: its elevation type, whether it is elevated, its integrity level's SID and its session.
    private static unsafe (int ElevationType, bool Elevated, string? Integrity, int Session) Token(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            throw new InvalidOperationException($"The child's token could not be opened ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            int elevationType = 0, elevated = 0, session = 0;
            uint returned = 0;
            _ = GetTokenInformation(token, TokenElevationType, &elevationType, sizeof(int), &returned);
            _ = GetTokenInformation(token, TokenElevation, &elevated, sizeof(int), &returned);
            _ = GetTokenInformation(token, TokenSessionId, &session, sizeof(int), &returned);
            byte* label = stackalloc byte[256];
            string? integrity = null;
            if (GetTokenInformation(token, TokenIntegrityLevel, label, 256, &returned) && ConvertSidToStringSidW(*(nint*)label, out nint text))
            {
                integrity = Marshal.PtrToStringUni(text);
                _ = LocalFree(text);
            }

            return (elevationType, elevated != 0, integrity, session);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private unsafe delegate int Ask(nint process, uint* length, char* buffer);

    /// <summary>One child as the workload read it before it ran - its image by its file name alone - and how it ended.</summary>
    private sealed record Child(
        string Role,
        int ProcessId,
        long CreationFileTime,
        string Image,
        string? PackageFullName,
        int PackageStatus,
        string? ApplicationUserModelId,
        string? PackageRelativeApplicationId,
        int TokenElevationType,
        bool TokenIsElevated,
        string? IntegrityLevelSid,
        int SessionId,
        int ExitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, char* currentDirectory,
        StartupInfo* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFullName(nint process, uint* length, char* packageFullName);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetApplicationUserModelId(nint process, uint* length, char* applicationUserModelId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, uint* returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSidW(nint sid, out nint text);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
