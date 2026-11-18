using System.Runtime.InteropServices;
using InterCat.CaptureBroker;
using InterCat.Domain;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The InterCat capture broker requires Windows.");
    return (int)InterCatExitCode.PermissionOrCapabilityFailure;
}

using var shutdown = new CancellationTokenSource();
using var termination = new BrokerTermination(shutdown, BrokerTermination.WindowsGrace);
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Stop serving, then stop and finalize what is still recording before the process ends.
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

// Console close, logoff and system shutdown end the process when this handler returns, and Windows allows only a few
// seconds; holding the handler until the host has stopped its captures gives finalization that window.
using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    _ = termination.Terminate();
});

try
{
    return await BrokerProcess.RunAsync(args, Console.Out, Console.Error, shutdown.Token);
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    // A failure nothing mapped still ends with a sentence and a code, never as an unexplained crash. The next broker's
    // recovery stops anything this one left recording.
    Console.Error.WriteLine($"The InterCat capture broker stopped on an unexpected error: {exception}");
    return BrokerProcess.ExitUnexpected;
}
finally
{
    termination.Finished();
}
