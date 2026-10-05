using InterCat.Cli;

ConsoleOutput.Install();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    ConsoleUi.Warn("Cancelling. Any owned capture session is stopped before exit.");
    cancellation.Cancel();
};

return (int)await Icat.RunAsync(args, cancellation.Token).ConfigureAwait(false);
