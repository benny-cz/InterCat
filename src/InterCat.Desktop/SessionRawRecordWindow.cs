using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// An exact original-envelope view. Body bytes remain hidden until the user requests a bounded preview. The record is
/// found by its stable raw identity, so it stays reachable while a live capture publishes newer generations.
/// </summary>
internal sealed class SessionRawRecordWindow : Window, IDisposable
{
    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly SessionEvidenceRecord selected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock heading = new()
    {
        Text = "Original journal envelope for one selected normalized fact",
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock disclosure = new()
    {
        Text = "Body bytes may be sensitive. They are hidden until you choose Reveal; the preview is capped "
            + "at 256 bytes, inert hexadecimal, and is not a decoded message or export.",
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap };
    private readonly Button reveal = new() { Content = "Reveal up to 256 retained body bytes", IsEnabled = false };
    private bool loading;
    private bool closed;
    private bool disposed;
    private bool revealed;

    public SessionRawRecordWindow(string path, Guid expectedSessionId, SessionEvidenceRecord selected)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.selected = selected;
        Title = "InterCat · Original journal record";
        Width = 850;
        Height = 620;
        MinWidth = 650;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        status.Text = "Locating the retained original record…";
        reveal.Click += (_, _) => _ = LoadAsync(revealBytes: true);
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { reveal, close },
        };
        var header = new StackPanel { Spacing = 5, Children = { heading, status, disclosure } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(detail, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(detail);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = LoadAsync(revealBytes: false);
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task LoadAsync(bool revealBytes)
    {
        if (loading || closed || (revealBytes && revealed)) return;
        loading = true;
        reveal.IsEnabled = false;
        detail.Text = string.Empty;
        status.Text = revealBytes ? "Reading a bounded byte preview…" : "Verifying the original journal record…";
        try
        {
            CancellationToken token = lifetime.Token;
            // The row's raw identity is stable across generations, so a live publication since the row was read
            // does not strand it: the current generation's retained journals are searched and checked against it.
            SessionRawRecordDetail result = await Task.Run(() => SessionRawRecordQuery.ReadRetained(
                SharedSessionStores.Open(path, expectedSessionId), expectedSessionId,
                selected, revealBytes, cancellationToken: token), token);
            if (closed) return;
            if (!result.Available)
            {
                status.Text = "Original record unavailable in retained evidence";
                detail.Text = result.UnavailableReason;
                return;
            }

            revealed = revealBytes;
            status.Text = $"Verified generation {result.Generation:N0} · {result.JournalName}";
            if (result.AdmissionPolicyId == RedactedSessionPackage.Policy)
            {
                // A redacted package holds no original record; its entry must never be presented as one.
                Title = "InterCat · Synthetic record of a redacted package";
                heading.Text = "Synthetic record of a redacted session package";
                disclosure.Text = "This package holds no original record. This entry carries the row's pseudonymous "
                    + "descriptor, header and reading, and never a body or extended data.";
            }

            detail.Text = Describe(result);
            reveal.IsEnabled = !revealed && result.RetainedBodyLength > 0;
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight scan without altering evidence.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "Could not verify the original record: " + exception.Message;
        }
        finally
        {
            loading = false;
        }
    }

    private static string Describe(SessionRawRecordDetail result)
    {
        EventHeaderFieldsV1 header = result.Header!.Value;
        BufferContextFieldsV1 buffer = result.BufferContext!.Value;
        var lines = new List<string>
        {
            $"Raw identity: {result.ObservationId.RawRecordId}",
            $"Provider {header.ProviderId:N} · event {header.EventId} · version {header.Version} · opcode {header.Opcode} · task {header.Task}",
            $"Header PID/TID {header.ProcessId}/{header.ThreadId} · activity {header.ActivityId} · related {header.RelatedActivityId}",
            $"Clock {result.ClockId} · encoding {result.TimestampEncoding} · native reading {result.NativeTicks}",
            $"Processor {buffer.ProcessorNumber} · logger {buffer.LoggerId} · pointer size {result.PointerSize} bytes",
            $"Schema reference {result.SchemaReference?.ToString(CultureInfo.InvariantCulture) ?? "none"} · fingerprint {result.SchemaFingerprint ?? "unavailable"}",
            $"Admission policy reference {result.AdmissionPolicyReference} · {result.AdmissionPolicyId}",
            $"Body: {result.BodyClassification} / {result.BodyDisposition} · original {result.OriginalBodyLength:N0} bytes · retained {result.RetainedBodyLength:N0} bytes",
            $"Extended items retained {result.ExtendedItems.Count:N0} · omitted {result.OmittedExtendedItemCount:N0}",
        };
        foreach (RawExtendedItemSummary item in result.ExtendedItems)
            lines.Add($"  item type {item.Type}, flags {item.Flags}: original {item.OriginalLength:N0}, retained {item.RetainedLength:N0} bytes");
        if (result.BodyPreview is { } bytes)
        {
            lines.Add("Retained body preview · inert hex:");
            for (int offset = 0; offset < bytes.Length; offset += 16)
            {
                int count = Math.Min(16, bytes.Length - offset);
                lines.Add($"  {offset:X4}: {Convert.ToHexString(bytes.AsSpan(offset, count))}");
            }
            if (result.BodyPreviewTruncated)
                lines.Add("Preview stopped at 256 bytes; more retained body bytes exist.");
        }
        else if (result.RetainedBodyLength > 0)
            lines.Add("Retained body bytes are hidden. Choose Reveal to inspect a bounded prefix.");
        else
            lines.Add("No body bytes were retained; the disposition above gives the recorded reason.");
        return string.Join(Environment.NewLine, lines);
    }
}
