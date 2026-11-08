using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The logical messages window (`contracts/cooperative-messages-v1.md`, R18): what applications instrumented with InterCat's
/// SDK said of their channels, in `icat messages`'s words - each channel epoch counted by what became of its messages, the
/// chosen epoch's messages with who published and read each - and an epoch's processes made the workspace's selection.
/// </summary>
public sealed class MessagesWindowTests
{
    private const string Epochs = "Channel epochs. Enter selects the processes that published or read the chosen epoch's messages.";
    private const string SelectThem = "Select its processes";
    private const string GoToIt = "Go to its process";
    private const string Reading = "The session's messages are being read.";

    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");
    private static readonly Guid Control = Guid.Parse("1d0c3b2a-9f8e-4d7c-8b6a-5f4e3d2c1b0a");
    private static readonly Guid Batched = Guid.Parse("7e6d5c4b-3a29-4817-a6b5-c4d3e2f1a0b9");

    [AvaloniaFact(DisplayName = "R18: the messages window lists each channel epoch in icat messages's words, the chosen epoch's messages with who published and read each, and returns its processes on Enter")]
    public async Task TheWindowListsEachEpoch()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        CooperativeMessageReading read = CooperativeMessageText.Read(session.Store);
        session.Store.ReleaseSegmentReaders();
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new MessagesWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);

        // Until the session is read, the window's action waits, saying so on itself.
        Assert.Equal(Reading, Offers.WhyNot(Labelled(window, SelectThem)));
        Task<MessageChoice?> chosen = window.ShowDialog<MessageChoice?>(owner);
        ListBox list = Named<ListBox>(window, Epochs);
        await Until(() => list.ItemCount > 0);

        // Each channel epoch by the name its opening gives it, counted by what became of its messages, as icat messages
        // counts them; the lines about the list say how many there are and what the capture covered.
        Assert.Equal("InterCat · Logical messages", window.Title);
        Assert.Equal(
        [
            "control · epoch 2 · 1 message · 1 consumed · 1 reader · 64 bytes published",
            "frames · epoch 1 · 2 messages · 1 consumed · 1 open · 2 readers · 4,608 bytes published",
            "samples · epoch 1 · 50 messages · 50 consumed · 1 reader · 6,400 bytes published",
        ], list.Items.Select(item => item?.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 53 messages in 3 channel epochs"),
            Named<TextBlock>(window, "Messages status").Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Named<TextBlock>(window, "Messages status")));
        Assert.Equal("producer.exe · 100 announced the SDK's schema 1.3, later than the 1.2 this InterCat knows: what it added never "
            + "reached the capture, and is counted nowhere here.", Assert.Single(read.Unreached));
        Assert.Equal(CoverageText.Describe(read.Coverage) + " " + read.Unreached[0], Named<TextBlock>(window, "What the capture covered").Text);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == CooperativeMessageText.Note);
        Assert.False(Named<TextBlock>(window, "What no channel holds").IsVisible);

        // The first epoch has the keyboard, and a screen reader hears each row whole.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // The frames epoch says its identity, what it carries messages through and how often it was opened and closed, and
        // lists its messages in sequence order, each in the words icat messages --channel says it in.
        list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Frames);
        Assert.Equal("frames · epoch 1: 2 messages · 1 consumed · 1 open · 2 readers · 4,608 bytes published.",
            AutomationProperties.GetName(list.ContainerFromIndex(list.SelectedIndex)!));
        Assert.Equal($"Channel {Frames} · carried through shared memory, as its first opening says · opened 1 and closed 0 times",
            Starting(window, "Channel ").Text);
        Assert.Equal("2 messages · 1 consumed · 1 open · 2 readers · 4,608 bytes published",
            Named<TextBlock>(window, "What the chosen epoch holds").Text);
        Assert.Equal("Its consumptions were said 11.0 µs after their publications at the median, from 10.0 µs after to 11.0 µs after.",
            Named<TextBlock>(window, "How long after their publications the chosen epoch's consumptions were said").Text);
        Assert.Equal("Its messages in sequence order:", Starting(window, "Its messages").Text);
        Assert.Equal(
        [
            "Sequence 1 · consumed · 4,096 bytes · published by producer.exe · 100 · read by reader 1 in reader.exe · 200, 10.0 µs "
                + "after its publication was said, reader 2 in reader.exe · 201, 11.0 µs after its publication was said",
            "Sequence 2 · open: no reader consumed it · 512 bytes · published by producer.exe · 100 · no reader",
        ], Named<ListBox>(window, "The chosen epoch's messages").Items.Cast<string>());
        Assert.Equal("Close this list and select the processes that published or read the epoch's messages together in "
            + "InterCat's window, as Ctrl+click does; Enter there lists their records.", Offers.Offered(Labelled(window, SelectThem)));

        // A run said in batches is one row, its batch's length said together.
        list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Batched);
        Assert.Equal("Its rows, a run said in batches one row, in sequence order:", Starting(window, "Its rows").Text);
        Assert.Equal(["Sequences 1 to 50 · consumed · said in a batch of 50 messages, 6,400 bytes together · published by producer.exe · 100 · "
            + "read by reader 1 in reader.exe · 200, 5.0 µs after its publication was said"], Named<ListBox>(window, "The chosen epoch's messages").Items.Cast<string>());

        // An epoch one process published and read goes to that process.
        list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Control);
        Assert.Equal("Close this list and select the process that published and read the epoch's messages in InterCat's window.",
            Offers.Offered(Labelled(window, GoToIt)));

        // With none chosen, the window says to choose one; Enter on the frames epoch closes it with its three processes.
        list.SelectedIndex = -1;
        Assert.Equal("Choose a channel epoch first.", Offers.WhyNot(Labelled(window, SelectThem)));
        list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Frames);
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(list.ContainerFromIndex(list.SelectedIndex)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await Until(() => chosen.IsCompleted);
        MessageChoice choice = Assert.IsType<MessageChoice>(await chosen);
        Assert.Equal((Frames, 1L), (choice.Channel.Channel, choice.Channel.Epoch));
        Assert.Equal([100, 200, 201],
            choice.Processes.Select(id => snapshot.Processes.Single(process => process.Id == id).ProcessId).Order());

        // Escape closes the window with nothing chosen, as it cancels InterCat's prompts.
        using var again = new MessagesWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        Task<MessageChoice?> cancelled = again.ShowDialog<MessageChoice?>(owner);
        ListBox listed = Named<ListBox>(again, Epochs);
        await Until(() => listed.ItemCount > 0);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the messages window says why it lists none - a session holding no channel or message of the SDK's, the coverage saying whether one could have been seen")]
    public async Task TheWindowSaysWhyItListsNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 }]);
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new MessagesWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        _ = window.ShowDialog<MessageChoice?>(owner);
        await Until(() => Named<TextBlock>(window, "Messages status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 0 messages in 0 channel epochs"),
            Named<TextBlock>(window, "Messages status").Text);
        Assert.Equal("This session holds no channel or message of the SDK's: no application said one through it while the capture "
            + "ran, or the capture did not record the SDK; the coverage above says which.", Starting(window, "This session").Text);
        Assert.Equal("This session holds no channel or message of the SDK's.", Offers.WhyNot(Labelled(window, SelectThem)));
        Dispatch();
        Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
        window.Close();
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Logical messages… selects the processes that published or read the chosen epoch's messages together, or goes to its one process, and the graph has the keyboard")]
    public async Task AnEpochsProcessesBecomeTheSelection()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Button button = window.GetControl<Button>("MessagesButton");
            Assert.True(button.IsEnabled);
            Assert.Equal("List the logical messages applications said of their channels", AutomationProperties.GetHelpText(button));
            Assert.Equal("List what applications instrumented with InterCat's SDK said of their channels - each channel epoch's "
                + "messages, what became of each, who published and who read it - and select an epoch's processes.", ToolTip.GetTip(button));

            // The frames epoch's three processes become the selection together, and the graph has the keyboard.
            MessagesWindow messages = Opened(window, button);
            ListBox list = Named<ListBox>(messages, Epochs);
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Frames);
            Labelled(messages, SelectThem).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.HasMultiSelection);
            Assert.Equal([100, 200, 201], workspace.ChosenProcesses.Select(process => process.ProcessId).Order());
            Assert.True(window.GetControl<GraphView>("GraphSurface").IsFocused);

            // An epoch of one process goes to it, in place of the set.
            messages = Opened(window, button);
            list = Named<ListBox>(messages, Epochs);
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = list.Items.Cast<MessageChannelRow>().ToList().FindIndex(row => row.Channel.Channel == Control);
            Labelled(messages, GoToIt).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.SelectedProcess is not null);
            Assert.Equal((100, false), (workspace.SelectedProcess!.ProcessId, workspace.HasMultiSelection));

            // Among the source records, the list waits, saying how to leave them.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Equal("Esc leaves the source records; logical messages can be listed from any other level.", Offers.WhyNot(button));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the messages window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(out SourceFieldRowV1[] fields), fields: fields);
        session.Store.ReleaseSegmentReaders();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new MessagesWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        _ = window.ShowDialog<MessageChoice?>(owner);
        ListBox list = Named<ListBox>(window, Epochs);
        await Until(() => list.ItemCount > 0);
        var problems = new List<string>();
        foreach (int index in new[] { 0, 1, 2 })
        {
            list.SelectedIndex = index;
            problems.AddRange(LegibleTextTests.CutOff(window, $"messages, epoch {index + 1} chosen"));
            problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"messages, epoch {index + 1}: {problem}"));
        }

        window.Close();
        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A producer opens the frames ring at epoch 1 and publishes two frames, which two readers consume the first of and none
    /// the second; at the control channel's second epoch it publishes one command and reads it back itself.
    /// </summary>
    private static ObservationRowV1[] Rows(out SourceFieldRowV1[] carried)
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\producer.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\reader.exe" },
            Lifecycle(3, ObservationKind.Create, 201, 3) with { ResourceName = @"C:\Tools\reader.exe" },
        };
        var fields = new List<SourceFieldRowV1>();
        void Add(ObservationRowV1 row, params (SourceField Code, long Value)[] values)
        {
            rows.Add(row);
            fields.AddRange(values.Select(field => Field(row, field.Code, field.Value)));
        }

        // The producer announces a later minor than this InterCat knows (ADR-111).
        Add(SdkRecord(5, CooperativeSdk.SdkStarted, 100, 9, null) with { Kind = ObservationKind.Inventory, ResourceName = "dotnet-1.3" },
            (SourceField.SdkSchemaMinor, 3));
        Add(SdkRecord(10, CooperativeSdk.ChannelOpened, 100, 10, Frames) with { ResourceName = "frames" },
            (SourceField.ChannelEpoch, 1), (SourceField.ChannelKind, 3));
        Add(SdkRecord(20, CooperativeSdk.MessagePublished, 100, 11, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.CopiedLength, -1));
        Add(SdkRecord(30, CooperativeSdk.MessageConsumed, 200, 12, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 1));
        Add(SdkRecord(31, CooperativeSdk.MessageConsumed, 201, 13, Frames, 4_096),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 2));
        Add(SdkRecord(40, CooperativeSdk.MessagePublished, 100, 14, Frames, 512),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 2), (SourceField.CopiedLength, 512));
        Add(SdkRecord(50, CooperativeSdk.ChannelOpened, 100, 15, Control) with { ResourceName = "control" },
            (SourceField.ChannelEpoch, 2), (SourceField.ChannelKind, 1));
        Add(SdkRecord(60, CooperativeSdk.MessagePublished, 100, 16, Control, 64),
            (SourceField.ChannelEpoch, 2), (SourceField.MessageSequence, 1), (SourceField.CopiedLength, 64));
        Add(SdkRecord(70, CooperativeSdk.MessageConsumed, 100, 17, Control, 64),
            (SourceField.ChannelEpoch, 2), (SourceField.MessageSequence, 1), (SourceField.ConsumerNumber, 1));

        // Fifty samples published in one batch, which reader 1 consumes in one batch.
        Add(SdkRecord(80, CooperativeSdk.ChannelOpened, 100, 18, Batched) with { ResourceName = "samples" },
            (SourceField.ChannelEpoch, 1), (SourceField.ChannelKind, 3));
        Add(SdkRecord(90, CooperativeSdk.MessagesPublished, 100, 19, Batched, 6_400),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.MessageCount, 50));
        Add(SdkRecord(95, CooperativeSdk.MessagesConsumed, 200, 20, Batched, 6_400),
            (SourceField.ChannelEpoch, 1), (SourceField.MessageSequence, 1), (SourceField.MessageCount, 50), (SourceField.ConsumerNumber, 1));
        carried = [.. fields];
        return [.. rows];
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static MessagesWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<MessagesWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<MessagesWindow>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static TextBlock Starting(Window window, string start) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.IsVisible
            && block.Text?.StartsWith(start, StringComparison.Ordinal) == true);

    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
