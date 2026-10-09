using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>The original-record window reads as words: it names a record's raw identity, never a type's fields (R15).</summary>
public sealed class RawRecordWindowTests
{
    [AvaloniaFact(DisplayName = "§3.2: the original record's raw identity is written in words, not as a record's generated fields")]
    public void TheRawIdentityReadsAsWords()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 8),
        ]);
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 1, resolveOwners: true);
        SessionEvidenceRecord record = Assert.Single(page.Records);
        RawRecordId raw = record.ObservationId.RawRecordId;

        using var window = new SessionRawRecordWindow(session.Path, page.SessionId, record);
        window.Show();
        TextBlock? status = null;
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            status = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Text?.StartsWith("Verified generation", StringComparison.Ordinal) == true);
            if (status is not null) break;
            Thread.Sleep(10);
        }

        Assert.NotNull(status);
        string detail = window.GetVisualDescendants().OfType<TextBox>().Single().Text ?? string.Empty;
        Assert.StartsWith(
            $"Raw record: capture {raw.CaptureId} · stream {raw.StreamId} · source epoch {raw.SourceEpoch} · ordinal {raw.RecordOrdinal}",
            detail, StringComparison.Ordinal);
        Assert.DoesNotContain("{ ", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("RawRecordId", detail, StringComparison.Ordinal);

        // A public provider is named beside its identity, and the envelope's codes read as words, not enumeration names.
        Assert.Contains($"Provider Microsoft-Windows-Kernel-Network ({NetworkProvider:N}) · event ", detail, StringComparison.Ordinal);
        string body = detail.Split(Environment.NewLine).Single(line => line.StartsWith("Body: ", StringComparison.Ordinal));
        Assert.Matches("^Body: [a-z ]+, [a-z ]+ · original ", body);

        // The record's text has the keyboard once read, whose caret reads it a line at a time.
        Assert.Same(window.GetVisualDescendants().OfType<TextBox>().Single(), window.FocusManager?.GetFocusedElement());

        // Escape closes it, as it closes InterCat's prompts.
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    }

    [AvaloniaFact(DisplayName = "R15: Reveal, which waits disabled while it reads, gives the record's text the keyboard back with the bytes in it")]
    public void RevealGivesTheRecordsTextTheKeyboardBack()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7)],
            bodyForRow: _ => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 3,
                Bytes = EnvelopeBuffer.CopyOf([0xCA, 0xFE, 0x01]),
            });
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 1, resolveOwners: true);
        using var window = new SessionRawRecordWindow(session.Path, page.SessionId, Assert.Single(page.Records));
        window.Show();
        Button reveal = window.GetVisualDescendants().OfType<Button>()
            .Single(button => (button.Content as string)?.StartsWith("Reveal", StringComparison.Ordinal) == true);
        TextBox detail = window.GetVisualDescendants().OfType<TextBox>().Single();
        WaitFor(() => reveal.IsEnabled && detail.IsFocused);

        // Tab reaches Reveal; Enter there reads the bytes, and the record's text, now holding them, has the keyboard.
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(reveal.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        WaitFor(() => detail.Text?.Contains("0000: CAFE01", StringComparison.Ordinal) == true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(reveal.IsEnabled);
        Assert.True(detail.IsFocused);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a record the window cannot verify leaves the keyboard on Close, with no record to read")]
    public void ARecordNotVerifiedGivesCloseTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7)]);
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 1, resolveOwners: true);

        // The directory holds another session than the one the record was read from, so nothing is verified.
        using var window = new SessionRawRecordWindow(session.Path, Guid.NewGuid(), Assert.Single(page.Records));
        window.Show();
        TextBlock status = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => AutomationProperties.GetName(text) == "Original record status");
        WaitFor(() => status.Text?.StartsWith("Could not verify the original record", StringComparison.Ordinal) == true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(string.Empty, window.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
        window.Close();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }
}
