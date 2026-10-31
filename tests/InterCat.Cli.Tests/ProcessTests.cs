using System.Diagnostics;
using System.Text;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat as a person or a script starts it, a process of its own (§20.4): what it writes reaches a pipe or a file whole, as
/// UTF-8 with no byte-order mark, whatever code page a console would have; the code a command returns is the process's
/// exit code; and Ctrl+C stops a command as a cancellation, never as a crash.
/// </summary>
public sealed class ProcessTests : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Fact(DisplayName = "§20.4: icat writes a pipe UTF-8 with no byte-order mark, its ellipses and middle dots whole, and exits with the code its command returned")]
    public async Task ItsTextAndItsExitCodeReachWhatStartedIt()
    {
        // The demo says what it does with an ellipsis, on stderr, and lists what it made on stdout.
        (int code, byte[] output, byte[] error) = await Icat("demo", Path.Combine(root.Path, "demo"));
        Assert.Equal((int)InterCatExitCode.Success, code);
        foreach (byte[] written in new[] { output, error })
        {
            Assert.False(written.AsSpan().StartsWith(Encoding.UTF8.Preamble), "A byte-order mark leads what icat wrote.");
        }

        Assert.True(error.AsSpan().IndexOf("… "u8) >= 0, Encoding.UTF8.GetString(error));
        Assert.Contains("DEMO INVESTIGATION", Encoding.UTF8.GetString(output), StringComparison.Ordinal);

        // A refused invocation exits with the code its refusal returned, saying why on stderr and nothing on stdout.
        (code, output, error) = await Icat("frobnicate");
        Assert.Equal((int)InterCatExitCode.InvalidInvocation, code);
        Assert.Empty(output);
        Assert.StartsWith("x Unknown command 'frobnicate'.", Encoding.UTF8.GetString(error), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§20.4: Ctrl+C stops a follow waiting for a recording's first publication as a cancellation, saying so")]
    public async Task CtrlCStopsACommandAsACancellation()
    {
        // Windows gives Ctrl+C to every process of a console at once, which no test can aim at one child; elsewhere SIGINT is
        // Ctrl+C, aimed at the follow alone.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string evidence = Directory.CreateDirectory(Path.Combine(root.Path, "evidence")).FullName;
        using Process follow = Start("follow", evidence, Path.Combine(root.Path, "session"));
        var said = new StringBuilder();
        var waiting = new TaskCompletionSource();
        follow.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not { } text) return;
            lock (said) said.AppendLine(text);
            if (text.Contains("Waiting for the recording's first publication", StringComparison.Ordinal)) waiting.TrySetResult();
        };
        follow.BeginErrorReadLine();
        Task<string> answered = follow.StandardOutput.ReadToEndAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));

        using (Process signal = Process.Start("kill", ["-INT", follow.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
        {
            await signal.WaitForExitAsync();
        }

        await follow.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        string error;
        lock (said) error = said.ToString();
        Assert.True(follow.ExitCode == (int)InterCatExitCode.Cancelled, $"icat follow exited {follow.ExitCode}: {error}");
        Assert.Equal(string.Empty, await answered);
        Assert.Contains("! Cancelling. Any owned capture session is stopped before exit.", error, StringComparison.Ordinal);
        Assert.Contains("! Stopped before the recording published anything.", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "session")));
    }

    /// <summary>Runs icat as a process of its own, as built beside these tests, and returns its exit code and every byte it wrote.</summary>
    private static async Task<(int Code, byte[] Output, byte[] Error)> Icat(params string[] args)
    {
        using Process icat = Start(args);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        await Task.WhenAll(icat.StandardOutput.BaseStream.CopyToAsync(output), icat.StandardError.BaseStream.CopyToAsync(error));
        await icat.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return (icat.ExitCode, output.ToArray(), error.ToArray());
    }

    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "InterCat.Cli.dll"));
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment.Remove("NO_COLOR");
        return Process.Start(start) ?? throw new InvalidOperationException("icat did not start.");
    }
}
