using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

    [AvaloniaFact(DisplayName = "§11.3: a redacted package's record is called synthetic from the window's first word, and no body is offered to reveal")]
    public void APackagesRecordIsSyntheticFromTheFirstWord()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 7)]);
        string destination = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages", Guid.NewGuid().ToString("N"));
        try
        {
            RedactedSessionPackage.Create(session.Store, destination, Committed);
            SessionStore package = SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination));
            SessionEvidencePage page = SessionEvidenceQuery.Read(package, pageSize: 1, resolveOwners: true);
            package.ReleaseSegmentReaders();
            SessionEvidenceRecord record = Assert.Single(page.Records);

            // Before a byte is read, the window calls the record what a package holds - a synthetic entry, never an
            // original - in its title, heading, status and the names a screen reader reads, and offers no body to reveal.
            using var told = new SessionRawRecordWindow(destination, page.SessionId, record, synthetic: true);
            TextBlock status = told.GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text) == "Synthetic record status");
            TextBox detail = told.GetLogicalDescendants().OfType<TextBox>().Single();
            Button reveal = told.GetLogicalDescendants().OfType<Button>()
                .Single(button => (button.Content as string)?.StartsWith("Reveal", StringComparison.Ordinal) == true);
            Assert.Equal("InterCat · Synthetic record of a redacted package", told.Title);
            Assert.Equal("Locating the synthetic record…", status.Text);
            Assert.Contains(told.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Synthetic record of a redacted session package");
            Assert.Equal("The synthetic record, field by field", AutomationProperties.GetName(detail));
            Assert.Contains(told.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("This package holds no original record. ", StringComparison.Ordinal) == true);
            Assert.False(reveal.IsVisible);
            var said = new List<string?>();
            status.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextBlock.TextProperty) said.Add(status.Text);
            };
            told.Show();
            WaitFor(() => status.Text?.StartsWith("Verified generation", StringComparison.Ordinal) == true);
            Assert.Equal("Verifying the synthetic record…", said[0]);
            Assert.Contains($" · {RedactedSessionPackage.Policy}", detail.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(told.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text?.StartsWith("Original", StringComparison.Ordinal) == true);
            Assert.True(detail.IsFocused);
            told.Close();

            // A window not told reads the record as one and says so from then on, Reveal gone with the original's name.
            using var untold = new SessionRawRecordWindow(destination, page.SessionId, record);
            untold.Show();
            WaitFor(() => untold.Title == "InterCat · Synthetic record of a redacted package");
            Assert.Equal("The synthetic record, field by field",
                AutomationProperties.GetName(untold.GetVisualDescendants().OfType<TextBox>().Single()));
            Assert.Contains(untold.GetVisualDescendants().OfType<TextBlock>(),
                text => AutomationProperties.GetName(text) == "Synthetic record status");
            Assert.DoesNotContain(untold.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible
                && (button.Content as string)?.StartsWith("Reveal", StringComparison.Ordinal) == true);
            untold.Close();

            // A row the package holds no entry for - which every row of a package has - says the package changed, never
            // that an original was released.
            SessionEvidenceRecord absent = record with
            {
                ObservationId = record.ObservationId with
                {
                    RawRecordId = record.ObservationId.RawRecordId with { RecordOrdinal = 999 },
                },
            };
            using var missing = new SessionRawRecordWindow(destination, page.SessionId, absent, synthetic: true);
            missing.Show();
            TextBlock unavailable = missing.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text) == "Synthetic record status");
            WaitFor(() => unavailable.Text == "Synthetic record unavailable in this package");
            Assert.Equal("The package's journal holds no entry for this row, though every row of a redacted package has one: "
                + "the package is not as it was saved.", missing.GetVisualDescendants().OfType<TextBox>().Single().Text);
            missing.Close();
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R21: a record kept across a release whose journal it gave up says its original is unavailable, and why")]
    public void ARecordWhoseJournalWasReleasedSaysItsOriginalIsUnavailable()
    {
        // The client's creation is kept as the process of a send after the boundary; the batch it was read in is not.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 2_000 },
        ]);
        Publish(session.Store,
        [
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 20_000 },
        ]);
        _ = InterCat.Analysis.IntervalRelease.Release(session.Store, 10_000, "older than the retained window", Committed, Committed);
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 10, resolveOwners: true);
        session.Store.ReleaseSegmentReaders();
        SessionEvidenceRecord creation = Assert.Single(page.Records,
            record => record.Observation.Kind == ObservationKind.Create);

        using var window = new SessionRawRecordWindow(session.Path, page.SessionId, creation);
        window.Show();
        TextBlock status = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => AutomationProperties.GetName(text) == "Original record status");
        TextBox detail = window.GetVisualDescendants().OfType<TextBox>().Single();
        WaitFor(() => status.Text == "Original record unavailable in retained evidence");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The reason is the record's text, with the keyboard, and nothing is offered to reveal.
        Assert.StartsWith("The original record is outside the retained journal boundary. ", detail.Text, StringComparison.Ordinal);
        Assert.True(detail.IsFocused);
        Assert.False(window.GetVisualDescendants().OfType<Button>()
            .Single(button => (button.Content as string)?.StartsWith("Reveal", StringComparison.Ordinal) == true).IsEnabled);
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
