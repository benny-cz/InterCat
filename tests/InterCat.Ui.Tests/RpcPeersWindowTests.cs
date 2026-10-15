using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The Explore card's RPC peers choice (ADR-035): a Keep choice of its own, never the default, since its private system
/// logger takes one of the eight a machine can run, so the card grows no taller at the smallest window. Chosen, it says
/// what the capture then records and takes, and the capture it starts links RPC calls to the processes serving them,
/// which the graph draws.
/// </summary>
public sealed class RpcPeersWindowTests
{
    [AvaloniaFact(DisplayName = "§9.4: the Explore card records RPC peers only when chosen, says what that records and takes, and tells the capture it starts")]
    public async Task TheExploreCardRecordsRpcPeersWhenChosen()
    {
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            ComboBox keep = window.GetControl<ComboBox>("CaptureKeepSelector");
            TextBlock note = window.GetControl<TextBlock>("CaptureKeepNote");
            Button start = window.GetControl<Button>("StartExploringButton");
            Dispatch();

            // Explore by default: RPC peers is offered among the Keep choices, and the capture links no RPC call.
            CaptureKeepChoice peers = Assert.Single(CaptureKeepChoice.All, choice => choice.LinksRpcCalls);
            Assert.Contains(peers, keep.Items.Cast<CaptureKeepChoice>());
            Assert.False(window.LinksRpcCalls);
            Assert.False(window.KeepChoice.Options(null).LinksRpcCalls);
            Assert.Equal("Start the default Explore capture", AutomationProperties.GetHelpText(start));
            Assert.Contains("every record of a ten-minute capture with the processes serving its RPC calls",
                AutomationProperties.GetHelpText(keep), StringComparison.Ordinal);

            // Chosen, it says beneath the choice what the capture records and what it takes, on hover too, and the start
            // action says what it starts. It records every record of ten minutes, as the default does.
            keep.SelectedItem = peers;
            Dispatch();
            Assert.True(window.LinksRpcCalls);
            Assert.True(note.IsVisible);
            Assert.Equal("This capture records for up to 10 minutes and keeps every record, with the kernel's ALPC messages, "
                + "which link each RPC call to the process that served it, so the graph draws RPC calls between processes. It "
                + "is not the default: it takes one of the eight system loggers a machine can run, and its overhead is "
                + "moderate.", note.Text);
            Assert.Equal("RPC peers, 10 minutes: " + peers.Explanation, ToolTip.GetTip(keep));
            Assert.Equal("Start the RPC peers capture, which draws each RPC call to the process that served it",
                AutomationProperties.GetHelpText(start));
            Assert.Equal("Start exploring (Ctrl+R)", start.Content);
            CaptureRunOptions options = peers.Options(null);
            Assert.Equal((600, (RollingRetentionPolicy?)null, true, (ContentCaptureChoice?)null),
                (options.MaximumDurationSeconds, options.Rolling, options.LinksRpcCalls, options.Content));

            // Another choice links none.
            keep.SelectedIndex = 0;
            Dispatch();
            Assert.False(window.LinksRpcCalls);
            Assert.False(note.IsVisible);
            keep.SelectedItem = peers;
            Dispatch();

            // While a capture runs the choice is not offered: it was told to the capture when it started.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(note.IsVisible);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Live capture needs Windows", "No capture was started."));
            Dispatch();
            Assert.True(note.IsVisible);

            // Starting names what it prepares and tells the capture to link RPC calls; where no capture can run, the card
            // keeps the choice.
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            var said = new List<string?>();
            status.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBlock.TextProperty)
                {
                    said.Add(status.Text);
                }
            };
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.IsAssignableFrom<Task>(window.CaptureRun).WaitAsync(TimeSpan.FromSeconds(60));
            Dispatch();
            CaptureRunOptions started = Assert.IsType<CaptureRunOptions>(window.StartedWith);
            Assert.True(started.LinksRpcCalls);
            Assert.Equal((600, (RollingRetentionPolicy?)null), (started.MaximumDurationSeconds, started.Rolling));
            Assert.Same(peers, window.KeepChoice);
            Assert.Contains("Preparing the RPC peers capture", said);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
