using System.Text;
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
/// M8's conversation inspector (`contracts/http-exchanges-v1.md` §5d): C on an HTTP exchange opens its request and its
/// response side by side, what was recorded of each read from the records alone; its heads are decoded whole only when
/// asked, never one that is not whole; and Show its buffers opens the exchange's buffers in InterCat's window.
/// </summary>
public sealed class ConversationWindowTests
{
    [AvaloniaFact(DisplayName = "P2: C on an HTTP exchange opens its conversation, which says what each head said only once asked, and Show its buffers opens them")]
    public async Task AConversationSaysWhatItsHeadsSaidWhenAsked()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        try
        {
            // The first exchange chosen, the inspector says what C does, and offers it as a button.
            workspace.SelectedRung = workspace.RungRows[0];
            Dispatch();
            Assert.True(workspace.HasSelectedHttpExchange);
            Assert.Equal("Enter opens its buffers · C reads its conversation", workspace.SelectionActions);
            Button offered = Labelled(window, "Read its conversation (C)");
            Assert.True(offered.IsEffectivelyVisible);

            // C opens it: read from the records alone, each side says what was recorded of its head and body, and nothing
            // of what they said.
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            SessionConversationWindow conversation = await Owned(window);
            await Until(() => Status(conversation).StartsWith("Read from generation", StringComparison.Ordinal));
            Assert.Equal("Read from generation 1; its heads are decoded only when you ask.", Status(conversation));
            string[] shown = Texts(conversation);
            Assert.Contains(shown, text => text.StartsWith("HTTP exchange 7 of ", StringComparison.Ordinal) && text.Contains("4242", StringComparison.Ordinal));
            Assert.Contains("Head: 28 B in 2 buffers, recorded whole", shown);
            Assert.Contains("Body: none recorded, as for a request without one", shown);
            Assert.Contains("Head: 38 B in 1 buffer, recorded whole", shown);
            Assert.Contains("Body: 5 B in 1 buffer, recorded whole", shown);
            Assert.Contains("whole: every part from its first buffer to its last", shown);
            Assert.DoesNotContain(shown, text => text.Contains("GET", StringComparison.Ordinal));
            Button decode = Labelled(conversation, "Decode its heads");
            Assert.Equal(SessionConversationWindow.DecodeDoes, Offers.Offered(decode));
            await Until(() => conversation.FocusManager?.GetFocusedElement() == decode);

            // Asked, each head is read whole and decoded: what the request asked for, of which host, and what was answered,
            // each field with the bytes of its head it came from.
            decode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(conversation).Contains("decoded", StringComparison.Ordinal));
            Assert.Equal("Read from generation 1; both heads decoded. Nothing decoded is kept.", Status(conversation));
            shown = Texts(conversation);
            Assert.Contains("GET /x, of a", shown);
            Assert.Contains("200 OK", shown);
            Assert.Contains(shown, text => text.StartsWith("Decoded by InterCat's HTTP/1.1 head decoder, version 1: it read ", StringComparison.Ordinal)
                && text.EndsWith(" of the request head of exchange 7.", StringComparison.Ordinal));
            SelectableTextBlock host = Named<SelectableTextBlock>(conversation, name => name.StartsWith("Host, ", StringComparison.Ordinal));
            Assert.Equal("a", host.Text);
            Assert.True(host.IsEffectivelyVisible);
            Assert.Equal("Host, bytes 17 to 23 (7 bytes): a", AutomationProperties.GetName(host));
            Assert.DoesNotContain(shown, text => text.StartsWith("Not decoded", StringComparison.Ordinal));

            // Beneath each body, what its head declared of it, beside what was recorded.
            Assert.Contains("Its head declares no body, and none was recorded.", shown);
            TextBlock held = Named<TextBlock>(conversation, name => name.StartsWith("Its response body, as its head declares it", StringComparison.Ordinal));
            Assert.Equal("Its head declares 5 bytes, and its recorded buffers hold them all.", held.Text);
            Assert.DoesNotContain("caution", held.Classes);

            // Show its buffers closes it and lists the exchange's buffers in InterCat's window.
            Button buffers = Labelled(conversation, "Show its buffers");
            Assert.Equal(SessionConversationWindow.BuffersDo, Offers.Offered(buffers));
            buffers.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0);
            await workspace.EvidenceReady;
            Assert.True(workspace.IsEvidenceRung);
            Assert.Equal(4, workspace.RungRows.Count);
            Assert.False(workspace.HasSelectedHttpExchange);

            // Back at the exchanges, the second's request head lacks a buffer and it has no response: neither head is whole,
            // so neither is decoded, which its button says, and the parts not whole are named.
            Assert.True(workspace.Ascend());
            await workspace.HttpReady;
            workspace.SelectedRung = workspace.RungRows[1];
            Dispatch();
            offered.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SessionConversationWindow lacking = await Owned(window);
            await Until(() => Status(lacking).StartsWith("Read from generation", StringComparison.Ordinal));
            shown = Texts(lacking);
            Assert.Contains("Head: 19 B in 2 buffers, not recorded whole", shown);
            Assert.Contains("Head: none recorded", shown);
            Assert.Contains("not whole: request head, response head, response body", shown);
            Assert.Contains("not known: its response's end was not recorded", shown);
            Assert.Equal("Neither of its heads was recorded whole, so neither is decoded: what a head lacks could hold any of its lines.",
                Offers.WhyNot(Labelled(lacking, "Decode its heads")));
            await Until(() => lacking.FocusManager?.GetFocusedElement() == Labelled(lacking, "Show its buffers"));
            lacking.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(workspace.IsHttpChannelRung);

            // The third recorded no head at all, only its response's body: there is nothing to decode.
            workspace.SelectedRung = workspace.RungRows[2];
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            SessionConversationWindow bodyOnly = await Owned(window);
            await Until(() => Status(bodyOnly).StartsWith("Read from generation", StringComparison.Ordinal));
            Assert.Equal("Neither of its heads was recorded, so there is nothing to decode.",
                Offers.WhyNot(Labelled(bodyOnly, "Decode its heads")));
            bodyOnly.Close();
            await Until(() => window.OwnedWindows.Count == 0);

            // The fourth's body is whole by its buffers and holds fewer bytes than its head declared, which is said in the
            // caution colour.
            workspace.SelectedRung = workspace.RungRows[3];
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            SessionConversationWindow shortBody = await Owned(window);
            await Until(() => Status(shortBody).StartsWith("Read from generation", StringComparison.Ordinal));
            Labelled(shortBody, "Decode its heads").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Status(shortBody).Contains("decoded", StringComparison.Ordinal));
            TextBlock fewer = Named<TextBlock>(shortBody, name => name.StartsWith("Its response body, as its head declares it", StringComparison.Ordinal));
            Assert.Equal("Its head declares 10 bytes, and its recorded buffers hold 4 of them, though their places and ends make the "
                + "body whole.", fewer.Text);
            Assert.Contains("caution", fewer.Classes);
            Assert.True(fewer.IsEffectivelyVisible);
            shortBody.Close();
            await Until(() => window.OwnedWindows.Count == 0);

            // F1's sheet lists the key among the ranked table's.
            Assert.Contains(WindowKeys.All, key => (key.Where, key.Keys) == ("Ranked table", "C"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "P2: a whole head kept without consent to inspect it is offered, and its decoding says it read none; an exchange no longer held offers nothing")]
    public async Task AHeadKeptWithoutConsentIsNotDecoded()
    {
        using var session = Session(ContentInspectionV1.Disabled);
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            string key = workspace.RungRows[0].Key;
            using (var conversation = new SessionConversationWindow(session.Path, session.Store.SessionId, key,
                EvidencePolicy.IncludeCorrelated, null, workspace.TimeBase))
            {
                _ = conversation.ShowDialog<bool>(owner);
                await Until(() => Status(conversation).StartsWith("Read from generation", StringComparison.Ordinal));
                Assert.Contains("HTTP exchange 7", Texts(conversation));
                Labelled(conversation, "Decode its heads").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Status(conversation).Contains("decoded", StringComparison.Ordinal));
                Assert.Equal("Read from generation 1; neither head decoded; each side says why. Nothing decoded is kept.", Status(conversation));
                string[] shown = Texts(conversation);
                Assert.Equal(2, shown.Count(text => text.StartsWith("Not decoded: ", StringComparison.Ordinal)
                    && text.Contains("without consent to inspect", StringComparison.Ordinal)));
                Assert.DoesNotContain(shown, text => text.Contains("GET", StringComparison.Ordinal));
                conversation.Close();
            }

            // A key the generation no longer holds says so, and offers neither action.
            Assert.True(HttpExchangeKeys.TryParseExchange(key, out string channel, out _));
            using var gone = new SessionConversationWindow(session.Path, session.Store.SessionId,
                HttpExchangeKeys.Exchange(channel, 9, 9, 9), EvidencePolicy.IncludeCorrelated, null, workspace.TimeBase);
            _ = gone.ShowDialog<bool>(owner);
            await Until(() => Status(gone).StartsWith("This generation holds no such exchange", StringComparison.Ordinal));
            Assert.Equal("No exchange is shown; the status line says why.", Offers.WhyNot(Labelled(gone, "Decode its heads")));
            Assert.Equal("No exchange is shown; the status line says why.", Offers.WhyNot(Labelled(gone, "Show its buffers")));
            await Until(() => gone.FocusManager?.GetFocusedElement() == Labelled(gone, "Close"));
            gone.Close();
        }
        finally
        {
            owner.Close();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "P2: a buffer's content viewer opens its exchange's conversation, which offers no buffers from there, and a record of no exchange offers none")]
    public async Task ABufferOpensItsExchangesConversation()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord buffer = page.Records.Single(record => record.Observation.RawRecordOrdinal == 2);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            // At its smallest, with its bytes shown, the viewer of a head's buffer offers every action without cutting one off.
            using var content = new SessionContentWindow(session.Path, page.SessionId, buffer, EvidencePolicy.IncludeCorrelated,
                SessionClock.Session(TimeZoneInfo.Utc));
            content.Width = content.MinWidth;
            content.Height = content.MinHeight;
            _ = content.ShowDialog(owner);
            Button open = Labelled(content, "Its exchange's conversation…");
            await Until(() => open.IsEffectivelyVisible && open.IsEffectivelyEnabled);
            Assert.Equal(SessionContentWindow.ConversationDoes, Offers.Offered(open));
            await Until(() => Labelled(content, "Show the kept bytes") is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
            Labelled(content, "Show the kept bytes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => content.GetLogicalDescendants().OfType<Button>()
                .Any(button => Equals(button.Content, "Decode its part") && button.IsEffectivelyVisible));
            Assert.Empty(LegibleTextTests.CutOff(content, "the content of an HTTP head's buffer"));

            // It opens the exchange the buffer belongs to, whose buffers are not offered from a dialog of its own.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => content.OwnedWindows.OfType<SessionConversationWindow>().Any());
            SessionConversationWindow conversation = content.OwnedWindows.OfType<SessionConversationWindow>().Single();
            await Until(() => Status(conversation).StartsWith("Read from generation", StringComparison.Ordinal));
            Assert.Contains("HTTP exchange 7 of PID 4242", Texts(conversation));
            Assert.False(Labelled(conversation, "Show its buffers").IsVisible);
            await Until(() => conversation.FocusManager?.GetFocusedElement() == Labelled(conversation, "Decode its heads"));
            conversation.Close();
            await Until(() => content.OwnedWindows.Count == 0);
            content.Close();

            // The lifecycle record is no buffer, so its viewer offers no conversation.
            using var other = new SessionContentWindow(session.Path, page.SessionId,
                page.Records.Single(record => record.Observation.Mechanism != Mechanism.Http));
            _ = other.ShowDialog(owner);
            Dispatch();
            Assert.False(Labelled(other, "Its exchange's conversation…").IsVisible);
            other.Close();
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the conversation window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var problems = new List<string>();
        try
        {
            foreach (int index in new[] { 0, 1, 2, 3 })
            {
                using var conversation = new SessionConversationWindow(session.Path, session.Store.SessionId,
                    workspace.RungRows[index].Key, EvidencePolicy.IncludeCorrelated, "client.exe · PID 4242", workspace.TimeBase);
                conversation.Width = conversation.MinWidth;
                conversation.Height = conversation.MinHeight;
                _ = conversation.ShowDialog<bool>(owner);
                await Until(() => Status(conversation).StartsWith("Read from generation", StringComparison.Ordinal));
                Check($"exchange {index + 1} read");
                Button decode = Labelled(conversation, "Decode its heads");
                if (decode.IsEnabled)
                {
                    decode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Status(conversation).Contains("decoded", StringComparison.Ordinal));
                    Check($"exchange {index + 1} decoded");
                }

                conversation.Close();

                void Check(string where)
                {
                    Dispatch();
                    problems.AddRange(LegibleTextTests.CutOff(conversation, where));
                    problems.AddRange(AccessibilityAuditTests.Unheard(conversation, out _).Select(problem => $"{where}: {problem}"));
                }
            }
        }
        finally
        {
            owner.Close();
            window.Close();
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [AvaloniaFact(DisplayName = "P2: a process's exchanges export with what their heads say only once the window has said the table is content, each exchange of the scope a line")]
    public async Task ExchangesExportWithTheirHeadsOnceTheTableIsSaidToBeContent()
    {
        using var session = Session(ContentInspectionV1.HexAndText);
        using var files = new InterCat.Capture.Journal.Tests.TemporaryDirectory();
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        try
        {
            // At a process's HTTP exchanges the export is offered, saying beforehand that the table is content.
            Button export = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ExportHeadsButton");
            Assert.True(export.IsEffectivelyVisible);
            Assert.True(export.IsEffectivelyEnabled);
            Assert.Equal("Export with their heads…", export.Content);
            Assert.Equal(workspace.ExportHttpHeadsTip, ToolTip.GetTip(export));
            Assert.Equal(workspace.ExportHttpHeadsTip, AutomationProperties.GetHelpText(export));
            Assert.EndsWith("read from content kept with consent to inspect it; the table is content.", workspace.ExportHttpHeadsTip,
                StringComparison.Ordinal);

            // Its prompt says what the table holds and that it is content before a file is chosen, legibly and heard, and
            // Escape declines.
            Window prompt = MainWindow.HttpHeadsPrompt();
            Task<bool> asked = prompt.ShowDialog<bool>(window);
            await Until(() => prompt.FocusManager?.GetFocusedElement() == Labelled(prompt, "Cancel"));
            Assert.Contains(MainWindow.HttpHeadsHolds, Texts(prompt));
            Assert.Contains(MainWindow.HttpHeadsAreContent, Texts(prompt));
            Assert.Empty(LegibleTextTests.CutOff(prompt, "the prompt before exporting heads"));
            Assert.Empty(AccessibilityAuditTests.Unheard(prompt, out _));
            prompt.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(await asked);

            // Pressed, the button asks first, and declined it asks for no file; accepted, it writes the file chosen, offered
            // under a name saying what it holds, and the window says what was written where.
            var choices = new AnsweredChoices(window);
            window.Choices = choices;
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 1);
            Labelled(window.OwnedWindows[0], "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal(0, choices.Asked);
            string chosen = Path.Combine(files.Path, "chosen.csv");
            choices.Answer(chosen);
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 1);
            Labelled(window.OwnedWindows[0], "Choose table file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => detail.Text?.EndsWith("share it only as you would that content.", StringComparison.Ordinal) == true);
            Assert.Equal("Export the exchanges with what their heads say", choices.Saves[0].Title);
            Assert.EndsWith(".heads.csv", choices.Saves[0].SuggestedFileName, StringComparison.Ordinal);
            Assert.StartsWith($"Exported 4 exchanges and what their heads say to {chosen};", detail.Text, StringComparison.Ordinal);
            Assert.True(File.Exists(chosen));

            // Written, the table holds every exchange the rung lists, a line each, with what its heads say; a head not
            // whole is not decoded, and its cell says why.
            string path = Path.Combine(files.Path, "heads.csv");
            HttpHeadsExport written = await window.WriteHttpHeadsAsync(path);
            Assert.Equal((4, 1, false), (written.Exchanges, written.Undecoded, written.More));
            string[] lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(string.Join(",", [.. HttpExchangeTable.Columns, .. HttpExchangeTable.HeadColumns]), lines[0]);
            Assert.Equal(5, lines.Length);
            Assert.Equal(workspace.RungRows.Select(row => row.Key), lines.Skip(1).Select(line => line.Split(',')[6]));
            Assert.Contains(",4242,,http:", lines[1], StringComparison.Ordinal);
            Assert.Contains(",http1-head,1,GET,/x,HTTP/1.1,a,200,OK,HTTP/1.1,true,true,,,", lines[1], StringComparison.Ordinal);
            Assert.Contains(",\"The part is not whole, so it is not decoded:", lines[2], StringComparison.Ordinal);
            Assert.Contains(",GET,/short,HTTP/1.1,b,200,OK,HTTP/1.1,", lines[4], StringComparison.Ordinal);
            Assert.Equal($"Exported 4 exchanges and what their heads say to {path}; 1 head was not decoded, its cell saying why. "
                + "It holds content: share it only as you would that content.", MainWindow.HttpHeadsWritten(written, path));

            // A brushed interval exports the exchanges listed in it alone, and a limit says it left the rest out.
            // The interval's counts are applied once read, and only then is the rung's HTTP read again for it.
            workspace.SelectInterval(new TimeRange(15, 35));
            await workspace.IntervalReady;
            await workspace.HttpReady;
            Dispatch();
            Assert.Equal(2, workspace.RungRows.Count);
            written = await window.WriteHttpHeadsAsync(path);
            lines = File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(workspace.RungRows.Select(row => row.Key), lines.Skip(1).Select(line => line.Split(',')[6]));
            Assert.Equal((2, 1, false), (written.Exchanges, written.Undecoded, written.More));
            workspace.ListedExportLimit = 1;
            written = await window.WriteHttpHeadsAsync(path);
            Assert.Equal((1, true), (written.Exchanges, written.More));
            Assert.StartsWith("Exported the first 1 exchange of the scope and what their heads say to ", MainWindow.HttpHeadsWritten(written, path),
                StringComparison.Ordinal);
            Assert.Equal(2, File.ReadAllText(path).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

            // Off a process's HTTP exchanges it is not offered, and an export says there is nothing to write.
            Assert.True(workspace.Ascend());
            await workspace.HttpReady;
            Dispatch();
            Assert.False(workspace.OffersHttpHeadsExport);
            Assert.False(export.IsVisible);
            Assert.False(workspace.CanExportHttpHeads);
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ExportHttpHeadsAsync());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "P2: heads kept without consent to inspect them are exported undecoded, each cell saying why, and never as what they said")]
    public async Task HeadsKeptWithoutConsentExportUndecoded()
    {
        using var session = Session(ContentInspectionV1.Disabled);
        using var files = new InterCat.Capture.Journal.Tests.TemporaryDirectory();
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        try
        {
            string path = Path.Combine(files.Path, "withheld.csv");
            HttpHeadsExport written = await window.WriteHttpHeadsAsync(path);
            Assert.Equal((4, 5), (written.Exchanges, written.Undecoded));
            string table = File.ReadAllText(path);
            Assert.DoesNotContain("GET", table, StringComparison.Ordinal);
            Assert.Equal(4, table.Split("without consent to inspect", StringSplitOptions.None).Length - 1);
            Assert.EndsWith("; 5 heads were not decoded, their cells saying why. It holds content: share it only as you would "
                + "that content.", MainWindow.HttpHeadsWritten(written, path), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Process 4242's exchange 7 - its request head in two buffers, its response head and body each in one - its exchange 8,
    /// whose request head lacks a buffer and which has no response, its exchange 9, of which only its response's body was
    /// recorded, and its exchange 10, whose body holds fewer bytes than its head declared; each buffer's bytes kept under
    /// <paramref name="inspection"/>.
    /// </summary>
    private static TemporarySession Session(ContentInspectionV1 inspection)
    {
        string[] messages =
        [
            "GET /x HTTP/1.1\r\nHo", "st: a\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", "hello",
            "GET /a HTTP/1.1\r\n", "\r\n", "tail",
            "GET /short HTTP/1.1\r\nHost: b\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n", "four",
        ];
        (long Ticks, ushort Event, long Number, long Sequence, long Flags)[] buffers =
        [
            (10, 2001, 7, 0, 1), (11, 2001, 7, 1, 2), (12, 2003, 7, 0, 3), (13, 2004, 7, 0, 3), (20, 2001, 8, 0, 1), (21, 2001, 8, 2, 2),
            (30, 2004, 9, 0, 3),
            (40, 2001, 10, 0, 3), (41, 2003, 10, 0, 3), (42, 2004, 10, 0, 3),
        ];
        ObservationRowV1[] rows = [.. buffers.Select((buffer, index) => Http(buffer.Ticks, buffer.Event, (ulong)(index + 1), messages[index].Length))];
        SourceFieldRowV1[] fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, buffer.Number),
                Field(rows[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 4_242, 1_000_000), .. rows], fields: fields,
            content: (ContentHeader(inspection: inspection),
                [.. messages.Select((message, index) => Content(rows[index], Encoding.ASCII.GetBytes(message), 64, ContentEncodingV1.Binary))]));
        session.Store.ReleaseSegmentReaders();
        return session;
    }

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242, with a session time.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
            SessionRelativeTicks = ticks * 100,
        };

    private static async Task<SessionConversationWindow> Owned(Window window)
    {
        await Until(() => window.OwnedWindows.OfType<SessionConversationWindow>().Any());
        return window.OwnedWindows.OfType<SessionConversationWindow>().Single();
    }

    private static string Status(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetName(block) == "Conversation status").Text ?? string.Empty;

    private static string[] Texts(Window window) =>
        [.. window.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text)).Select(block => block.Text!)];

    private static T Named<T>(Window window, Func<string, bool> name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) is { } said && name(said));

    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
