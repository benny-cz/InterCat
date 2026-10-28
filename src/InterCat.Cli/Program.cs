using InterCat.Cli;

ConsoleOutput.Install();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    ConsoleUi.Warn("Cancelling. Any owned capture session is stopped before exit.");
    cancellation.Cancel();
};

// An error nothing in icat mapped ends with a sentence and exit code 70, never an unexplained crash (§20.6 Internal).
return (int)await Icat.RunContainedAsync(() => Icat.RunAsync(args, cancellation.Token)).ConfigureAwait(false);
