using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// A read-only bounded inspector over a saved generation. It never follows a cursor into a newer generation;
/// the user returns to the workspace and reopens it when live evidence advances.
/// </summary>
internal sealed class SessionEvidenceWindow : Window, IDisposable
{
    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly string? channelKey;
    private readonly ProcessInstanceId? ownerProcess;
    private readonly TimeRange? interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock caveat = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox rows = new();
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap };
    private readonly Button next = new() { Content = "Next 100 rows", IsEnabled = false };
    private IReadOnlyList<SessionEvidenceRecord> currentRows = [];
    private string? nextCursor;
    private bool loading;
    private bool closed;
    private bool disposed;

    public SessionEvidenceWindow(string path, Guid expectedSessionId, long expectedGeneration,
        string? channelKey, ProcessInstanceId? ownerProcess, TimeRange? interval)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.channelKey = channelKey;
        this.ownerProcess = ownerProcess;
        this.interval = interval;
        Title = "InterCat · Source observations";
        Width = 960;
        Height = 690;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = (channelKey is not null ? $"Paired TCP channel {channelKey}"
                : ownerProcess is { } owner ? $"Rows canonically owned by process {owner}"
                : "Whole-session observed rows")
                + (interval is { } range
                    ? $" · [{range.StartTicks}, {range.EndTicks}) in 100 ns session ticks"
                    : " · all session times, including rows without a usable time"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Opening a verified evidence generation…";
        caveat.Text = "Only admitted normalized rows are shown. Process scope means the row's canonical owner, "
            + "not its possible peer. Original payload bytes and completion-paired "
            + "logical operations are not represented here.";
        rows.SelectionChanged += (_, _) =>
        {
            int index = rows.SelectedIndex;
            detail.Text = index >= 0 && index < currentRows.Count
                ? Describe(currentRows[index]) : string.Empty;
        };
        next.Click += (_, _) =>
        {
            if (nextCursor is { } cursor) _ = LoadPageAsync(cursor);
        };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { next, close },
        };
        var header = new StackPanel { Spacing = 5, Children = { scope, status, caveat } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,190,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(rows, 1);
        Grid.SetRow(detail, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(header);
        grid.Children.Add(rows);
        grid.Children.Add(detail);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = LoadPageAsync(null);
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

    private async Task LoadPageAsync(string? cursor)
    {
        if (loading || closed) return;
        loading = true;
        next.IsEnabled = false;
        currentRows = [];
        rows.ItemsSource = Array.Empty<string>();
        detail.Text = string.Empty;
        status.Text = "Reading a verified page…";
        try
        {
            CancellationToken token = lifetime.Token;
            SessionEvidencePage page = await Task.Run(() => SessionEvidenceQuery.Read(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), channelKey, interval,
                ownerProcess, pageSize: SessionEvidenceQuery.DefaultPageSize, cursor: cursor,
                cancellationToken: token), token);
            if (closed) return;
            if (page.RestartRequired || page.SessionId != expectedSessionId
                || page.Generation != expectedGeneration)
            {
                currentRows = [];
                rows.ItemsSource = Array.Empty<string>();
                detail.Text = string.Empty;
                nextCursor = null;
                status.Text = "The session has published a newer generation or the query changed. "
                    + "Close this inspector and reopen it from the current workspace; no page was shifted.";
                return;
            }

            currentRows = page.Records;
            rows.ItemsSource = currentRows.Select(DescribeBriefly).ToArray();
            rows.SelectedIndex = currentRows.Count > 0 ? 0 : -1;
            caveat.Text = page.Caveat;
            nextCursor = page.NextCursor;
            next.IsEnabled = nextCursor is not null;
            status.Text = currentRows.Count == 0
                ? "No admitted source row is in this exact scope. This is not proof of inactivity."
                : $"Generation {page.Generation:N0} · {currentRows.Count:N0} rows on this page"
                    + (nextCursor is null ? " · end of result" : " · more rows available");
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing the inspector cancels a pending read without changing the session.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                nextCursor = null;
                status.Text = "Could not read this evidence page: " + exception.Message;
            }
        }
        finally
        {
            loading = false;
        }
    }

    private static string DescribeBriefly(SessionEvidenceRecord record)
    {
        ObservationRowV1 row = record.Observation;
        return string.Create(CultureInfo.InvariantCulture,
            $"{row.SessionRelativeTicks?.ToString(CultureInfo.InvariantCulture) ?? "untimed"} ns · "
            + $"{row.Mechanism}/{row.Kind} · owner PID {row.OwnerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"} "
            + $"· event {row.EventId} · raw ordinal {row.RawRecordOrdinal}");
    }

    private static string Describe(SessionEvidenceRecord record)
    {
        ObservationRowV1 row = record.Observation;
        RawRecordId raw = record.ObservationId.RawRecordId;
        return string.Join(Environment.NewLine,
            $"Provider: {row.ProviderId:N} · event {row.EventId} · version {row.DescriptorVersion} · opcode {row.Opcode}",
            $"Schema fingerprint: {row.SchemaFingerprint}",
            $"Native reading: {row.NativeTicks} · session relative: {row.SessionRelativeTicks?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} ns",
            $"Mechanism/layer/kind/direction: {row.Mechanism} / {row.Layer} / {row.Kind} / {row.Direction}",
            $"Owner PID: {row.OwnerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} · header PID/TID: {row.HeaderProcessId}/{row.HeaderThreadId}",
            $"Source endpoint fields: family {row.EndpointAddressFamily?.ToString(CultureInfo.InvariantCulture) ?? "?"}, {row.SourceEndpointAddress?.ToString(CultureInfo.InvariantCulture) ?? "?"}:{row.SourceEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"} → {row.DestinationEndpointAddress?.ToString(CultureInfo.InvariantCulture) ?? "?"}:{row.DestinationEndpointPort?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            $"Resource name: {row.ResourceName ?? "unavailable"} · source identifier: {row.SourceIdentifier?.ToString() ?? "unavailable"}",
            $"Bytes: {row.ByteValue?.ToString(CultureInfo.InvariantCulture) ?? row.ByteAvailability.ToString()} · domain/side/unit: {row.ByteDomain?.ToString() ?? "?"}/{row.AccountingSide?.ToString() ?? "?"}/{row.MeasurementUnit?.ToString() ?? "?"}",
            $"Status: {row.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? row.StatusAvailability.ToString()} · activity: {row.ActivityId?.ToString() ?? "unavailable"} · related: {row.RelatedActivityId?.ToString() ?? "unavailable"}",
            $"Quality: attribution {row.AttributionQuality}, correlation {row.CorrelationQuality}, measurement {row.MeasurementQuality}, timing {row.TimingQuality} · markers {row.Markers}",
            $"Raw source locator: capture {raw.CaptureId}, stream {raw.StreamId}, epoch {raw.SourceEpoch}, ordinal {raw.RecordOrdinal}, journal index {row.JournalRecordIndex?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}",
            $"Fact: normalizer {record.ObservationId.NormalizerContractVersion}, key {record.ObservationId.FactKey}",
            $"This generation: {record.SegmentName}, row {record.SegmentRow}");
    }
}
