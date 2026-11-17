using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The messages window's content (ADR-112): what was kept of each message's bytes said beside it in icat messages's words,
/// and the chosen one's opened in the content viewer, its bytes hidden until asked for there.
/// </summary>
public sealed class MessagesContentWindowTests
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    [AvaloniaFact(DisplayName = "§6.7: Logical messages… says what was kept of each message's bytes in icat messages's words, and Content… opens the chosen one's in the content viewer, saying why not of one with none kept")]
    public async Task ItOpensAMessagesContent()
    {
        using var session = new TemporarySession();
        Build(session, asked: true);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            using var window = new MessagesWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
            Task<MessageChoice?> shown = window.ShowDialog<MessageChoice?>(owner);
            ListBox list = Named<ListBox>(window, "The chosen epoch's messages");
            await Until(() => list.ItemCount > 0);

            // Each message says what was kept of its bytes, as icat messages says it.
            MessageRow[] rows = [.. list.Items.Cast<MessageRow>()];
            Assert.EndsWith(" · content kept: 8 bytes, whole", rows[0].Text, StringComparison.Ordinal);
            Assert.EndsWith(" · content kept: none: published without its bytes", rows[1].Text, StringComparison.Ordinal);

            // Content… waits for a message, and says why a message with none kept has none to show.
            Button content = Labelled(window, "Content…");
            Assert.Equal("Choose a message first.", Offers.WhyNot(content));
            list.SelectedIndex = 1;
            Dispatch();
            Assert.Equal("None: published without its bytes.", Offers.WhyNot(content));

            // The chosen message's kept bytes open in the content viewer, over this window.
            list.SelectedIndex = 0;
            Dispatch();
            Assert.Equal("Open what the capture kept of this message's bytes in the content viewer, its bytes hidden until you ask there",
                Offers.Offered(content));
            content.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SessionContentWindow viewer = await Owned<SessionContentWindow>(window);
            Assert.Equal("InterCat · Content of one record", viewer.Title);
            viewer.Close();
            Dispatch();
            window.Close();
            await shown;
        }
        finally
        {
            owner.Close();
        }

        // A capture that asked for none of the SDK's content says none of it beside its messages, and Content… says why.
        using var unasked = new TemporarySession();
        Build(unasked, asked: false);
        SessionOverviewBundle plain = SessionOverviewProjector.Project(unasked.Store);
        unasked.Store.ReleaseSegmentReaders();
        var second = new Window { Width = 400, Height = 300 };
        second.Show();
        try
        {
            using var window = new MessagesWindow(unasked.Path, plain.SessionId, plain.Generation, EvidencePolicy.IncludeCorrelated);
            Task<MessageChoice?> shown = window.ShowDialog<MessageChoice?>(second);
            ListBox list = Named<ListBox>(window, "The chosen epoch's messages");
            await Until(() => list.ItemCount > 0);
            Assert.All(list.Items.Cast<MessageRow>(), row => Assert.DoesNotContain("content kept", row.Text, StringComparison.Ordinal));
            list.SelectedIndex = 0;
            Dispatch();
            Assert.Equal("The capture kept no message's bytes: it asked for none of the SDK's content.",
                Offers.WhyNot(Labelled(window, "Content…")));
            window.Close();
            await shown;
        }
        finally
        {
            second.Close();
        }
    }

    /// <summary>
    /// A message published with its bytes, kept whole, and one published without them, under a plan that admitted the SDK's
    /// content - or, not <paramref name="asked"/>, one that admitted none of it and kept nothing.
    /// </summary>
    private static void Build(TemporarySession session, bool asked)
    {
        var rows = new List<ObservationRowV1> { Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\publisher.exe" } };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 10;
        ObservationRowV1 Said(long ticks, ushort eventId, long sequence, long? length = null)
        {
            ObservationRowV1 row = SdkRecord(ticks, eventId, 100, ++ordinal, Frames, length);
            rows.Add(row);
            fields.Add(Field(row, SourceField.ChannelEpoch, 1));
            fields.Add(Field(row, SourceField.MessageSequence, sequence));
            return row;
        }

        _ = Said(10, CooperativeSdk.MessagePublished, 1, 8);
        ObservationRowV1 whole = Said(11, CooperativeSdk.MessageContent, 1);
        _ = Said(20, CooperativeSdk.MessagePublished, 2, 8);
        byte[] plan = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "normalizer-plan-v1",
            descriptors = Enumerable.Range(2, asked ? 9 : 8).Select(static id => new { providerGuid = CooperativeSdk.ProviderId, eventId = id, mechanism = (int)Mechanism.ApplicationSdk }),
        });
        (ContentChunkHeaderV1 Header, IReadOnlyList<(ContentFragmentV1 Fragment, ReadOnlyMemory<byte> Bytes)> Fragments)? chunk = asked
            ? (ContentHeader(recordLimit: 4_096), [Content(whole, "12345678"u8.ToArray(), 4_096, ContentEncodingV1.Binary)])
            : null;
        _ = Publish(session.Store, [.. rows], fields: fields, plan: plan, content: chunk);
    }

    /// <summary>A dialog the window shows over itself, once it is shown.</summary>
    private static async Task<T> Owned<T>(Window window)
        where T : Window
    {
        for (int attempt = 0; attempt < 200 && !window.OwnedWindows.OfType<T>().Any(); attempt++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Dispatch();
        return window.OwnedWindows.OfType<T>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static Button Labelled(Window window, string label) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
