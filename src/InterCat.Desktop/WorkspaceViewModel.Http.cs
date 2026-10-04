using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A process's HTTP exchanges on the ladder (ADR-037, M8): the process rung lists them as one channel, the channel rung
/// lists the exchanges a page at a time, each with what was recorded of its four parts, and an exchange descends to its own
/// buffers, where C opens a buffer's content and its whole part. Everything a row says comes from the records' metadata and
/// source fields; no content is read to list them.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private HttpChannelsLoad? httpChannels;
    private HttpExchangesLoad? httpExchanges;
    private IReadOnlyList<RungRow> httpChannelRows = [];
    private IReadOnlyList<RungRow> httpExchangeRows = [];

    /// <summary>Whether the user is at a published session's HTTP channel rung, whose rows are a process's exchanges.</summary>
    public bool IsHttpChannelRung => evidenceSource is not null
        && ladder.Current.Level == DetailLevel.Channel
        && HttpExchangeKeys.IsHttp(ladder.Current.Focus?.Key);

    /// <summary>Completes when the latest read of HTTP exchanges has applied or reported a problem.</summary>
    public Task HttpReady { get; private set; } = Task.CompletedTask;

    public bool CanLoadMoreExchanges => IsHttpChannelRung && httpExchanges is { Loading: false, Problem: null, More: true };

    /// <summary>Reads the HTTP channel rung's next page of exchanges, after the last one shown.</summary>
    public Task LoadMoreExchangesAsync()
    {
        if (!CanLoadMoreExchanges)
        {
            return Task.CompletedTask;
        }

        HttpReady = LoadHttpExchangesAsync(httpExchanges!);
        return HttpReady;
    }

    /// <summary>What an HTTP channel rung states: its exchanges, how many were recorded whole, and how long one took.</summary>
    private string HttpExchangeSummary(bool brief)
    {
        if (httpExchanges is not { } load) return string.Empty;
        if (load.Problem is not null) return "Exchanges unavailable";
        if (load.Channel is not { } channel) return "Reading exchanges…";
        string loaded = load.Exchanges.Count < channel.Exchanges
            ? string.Create(CultureInfo.CurrentCulture, $" · {load.Exchanges.Count:N0} listed")
            : string.Empty;
        return brief
            ? channel.Outcome(CultureInfo.CurrentCulture) + loaded
            : channel.Outcome(CultureInfo.CurrentCulture) + loaded
                + " · grouped by WinINet's exchange numbers; E shows the buffers, and C a buffer's content";
    }

    /// <summary>A row for a process's HTTP exchanges, counted in buffer records.</summary>
    private static LadderRow HttpChannelRow(string key, string label, string detail, long records) =>
        new(key, label, detail, records, null, Mechanism.Http, CoverageState.UnknownCoverage, DetailLevel.Channel,
            AccountingSide.CanonicalOwner);

    /// <summary>
    /// Starts the reads the rung needs: a process's HTTP exchanges when it raised HTTP records, and the exchanges' first page
    /// at its HTTP channel rung. Any other rung lets both go.
    /// </summary>
    private void SyncHttp()
    {
        if (IsHttpChannelRung)
        {
            string key = ladder.Current.Focus?.Key ?? string.Empty;
            CancelHttpChannels();
            if (httpExchanges is { } current && current.ChannelKey == key && current.Scope == CountedScope)
            {
                return;
            }

            CancelHttpExchanges();
            var exchanges = new HttpExchangesLoad(key, CountedScope);
            httpExchanges = exchanges;
            HttpReady = LoadHttpExchangesAsync(exchanges);
            return;
        }

        CancelHttpExchanges();
        // Whether the process made HTTP exchanges is the whole session's fact, as its RPC channels' is.
        if (evidenceSource is not null
            && ladder.Current.Level == DetailLevel.ProcessInstance
            && ladder.Current.Focus is { } focus
            && wholeSnapshot.Processes.FirstOrDefault(process => process.Id.ToString() == focus.Key) is { } process
            && process.Activity.Any(count => count.Mechanism == Mechanism.Http && count.Records > 0))
        {
            if (httpChannels is { } known && known.Instance == process.Id && known.Scope == CountedScope)
            {
                return;
            }

            // The previous scope's row stays on screen until this scope's is read, rather than blinking empty (§6.4).
            IReadOnlyList<HttpChannelSummary> previous = httpChannels is { } shown && shown.Instance == process.Id ? shown.Channels : [];
            CancelHttpChannels();
            var channels = new HttpChannelsLoad(process.Id, CountedScope) { Channels = previous };
            httpChannels = channels;
            RebuildHttpRows();
            HttpReady = LoadHttpChannelsAsync(channels);
            return;
        }

        CancelHttpChannels();
    }

    /// <summary>Re-reads a process's exchanges when the scope the rung counts changed, so they count what the rows beside them count.</summary>
    private void SyncHttpScope()
    {
        if (IsHttpChannelRung && httpExchanges is { } exchanges && exchanges.Scope != CountedScope)
        {
            var reload = new HttpExchangesLoad(exchanges.ChannelKey, CountedScope)
            {
                Channel = exchanges.Channel,
                ReplaceOnFirstPage = true,
            };
            reload.Exchanges.AddRange(exchanges.Exchanges);
            exchanges.Cancellation.Cancel();
            exchanges.Cancellation.Dispose();
            httpExchanges = reload;
            RebuildHttpRows();
            HttpReady = LoadHttpExchangesAsync(reload);
        }
        else if (!IsHttpChannelRung && ladder.Current.Level == DetailLevel.ProcessInstance
            && httpChannels is { } channels && channels.Scope != CountedScope)
        {
            SyncHttp();
        }
    }

    private async Task LoadHttpChannelsAsync(HttpChannelsLoad load)
    {
        load.Loading = true;
        RaiseRpcChanged();
        try
        {
            HttpChannelList list = await evidenceSource!.HttpChannelsAsync(load.Instance, load.Scope, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(httpChannels, load)) return;
            load.Channels = list.Channels;
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(httpChannels, load)) return;
            load.Problem = "This process's HTTP exchanges could not be read: " + exception.Message;
        }
        finally
        {
            load.Loading = false;
        }

        RebuildHttpRows();
    }

    private async Task LoadHttpExchangesAsync(HttpExchangesLoad load)
    {
        load.Loading = true;
        RaiseRpcChanged();
        int offset = load.ReplaceOnFirstPage ? 0 : load.Exchanges.Count;
        try
        {
            HttpExchangePage page = await evidenceSource!.HttpExchangesAsync(load.ChannelKey, offset, load.Scope, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(httpExchanges, load)) return;
            if (page.Problem is not null)
            {
                load.Problem = page.Problem;
                load.More = false;
            }
            else
            {
                load.Channel = page.Channel;
                if (load.ReplaceOnFirstPage)
                {
                    load.Exchanges.Clear();
                    load.ReplaceOnFirstPage = false;
                }

                load.Exchanges.AddRange(page.Exchanges);
                load.More = page.More;
            }
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(httpExchanges, load)) return;
            load.Problem = "This process's HTTP exchanges could not be read: " + exception.Message;
            load.More = false;
        }
        finally
        {
            load.Loading = false;
        }

        RebuildHttpRows();
    }

    /// <summary>Rebuilds the rows the HTTP reads supply, keeping the selected row when it is still listed.</summary>
    private void RebuildHttpRows()
    {
        if (disposed)
        {
            return;
        }

        FamilyTokens tokens = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(Mechanism.Http));
        httpChannelRows = httpChannels is { } channels
            ? [.. channels.Channels.Select(channel => HttpChannelRungRow(channel, tokens))]
            : [];
        httpExchangeRows = httpExchanges is { Channel: not null } exchanges
            ? [.. exchanges.Exchanges.Select(exchange => HttpExchangeRungRow(exchange, tokens))]
            : [];
        string? selectedKey = selectedRung?.Key;
        RaiseRpcChanged();
        if (selectedKey is not null && RungRows.FirstOrDefault(row => row.Key == selectedKey) is { } kept)
        {
            selectedRung = kept;
            OnPropertyChanged(nameof(SelectedRung));
            RaiseSelectionDescribed();
        }
    }

    /// <summary>A process's HTTP exchanges as one row: how many, how they were recorded, and the bytes of their messages.</summary>
    private static RungRow HttpChannelRungRow(HttpChannelSummary channel, FamilyTokens tokens)
    {
        string detail = "HTTP client · " + channel.Outcome(CultureInfo.CurrentCulture);
        string bytes = string.Create(CultureInfo.CurrentCulture,
            $"{channel.RequestBytes:N0} B sent, {channel.ResponseBytes:N0} B received in HTTP messages");
        // The rail is narrow, so the row reads "HTTP exchanges"; its crumb, filter and records' scope say the whole name.
        LadderRow source = HttpChannelRow(channel.Key, HttpChannelSummary.Name, detail, channel.Records);
        return new(channel.Key, "HTTP exchanges", detail, channel.Records.ToString("N0", CultureInfo.CurrentCulture),
            bytes, tokens.Label, tokens.Glyph, string.Empty, NavigationState.Name(DetailLevel.Channel), source)
        {
            SpokenName = $"{HttpChannelSummary.Name}, {detail}, {bytes}, {Spoken.Count(channel.Records, "buffer record")}. "
                + "Press Enter to list the exchanges.",
        };
    }

    /// <summary>One exchange's row: how long it took, or why that is not known, and what was recorded of its parts.</summary>
    private static RungRow HttpExchangeRungRow(HttpExchangeRow row, FamilyTokens tokens)
    {
        HttpExchange exchange = row.Exchange;
        string when = exchange.FirstNanoseconds is { } nanoseconds
            ? string.Create(CultureInfo.CurrentCulture, $"+{nanoseconds / 1_000_000_000m:0.000000} s")
            : "time unavailable";

        // The row leads with how the exchange went, which is what a list of them is scanned for; the rung names the process.
        string label = exchange.DurationNanoseconds is { } duration
            ? OperationText.Duration(duration, CultureInfo.CurrentCulture)
            : "response end not recorded";
        string parts = row.Parts(CultureInfo.CurrentCulture);
        string detail = string.Create(CultureInfo.CurrentCulture, $"{when} · exchange {exchange.Number:N0} · {parts}");
        var source = new LadderRow(row.Key, $"HTTP exchange at {when}", detail, exchange.Records, null, Mechanism.Http,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner);
        return new(row.Key, label, detail, exchange.Records.ToString("N0", CultureInfo.CurrentCulture),
            string.Create(CultureInfo.CurrentCulture, $"{exchange.RequestBytes:N0} B sent, {exchange.ResponseBytes:N0} B received"),
            tokens.Label, tokens.Glyph, string.Empty, NavigationState.Name(DetailLevel.Evidence), source)
        {
            SpokenName = $"HTTP exchange at {when}, {label}, exchange {exchange.Number:N0}, {parts}. Press Enter to open its "
                + "buffers, where C shows a buffer's content.",
        };
    }

    private void CancelHttp()
    {
        CancelHttpChannels();
        CancelHttpExchanges();
    }

    private void CancelHttpChannels()
    {
        if (httpChannels is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        httpChannels = null;
        httpChannelRows = [];
    }

    private void CancelHttpExchanges()
    {
        if (httpExchanges is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        httpExchanges = null;
        httpExchangeRows = [];
    }

    /// <summary>One process's HTTP exchanges as read for its rung.</summary>
    private sealed class HttpChannelsLoad(ProcessInstanceId instance, TimeRange? scope)
    {
        public ProcessInstanceId Instance { get; } = instance;

        /// <summary>The interval the exchanges are counted in, or the whole session (null).</summary>
        public TimeRange? Scope { get; } = scope;

        public IReadOnlyList<HttpChannelSummary> Channels { get; set; } = [];

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>One process's HTTP exchanges as read for their rung, a page at a time.</summary>
    private sealed class HttpExchangesLoad(string channelKey, TimeRange? scope)
    {
        public string ChannelKey { get; } = channelKey;

        /// <summary>The interval the exchanges are listed in, or the whole session (null).</summary>
        public TimeRange? Scope { get; } = scope;

        /// <summary>Whether the exchanges shown are the previous scope's, which this scope's first page replaces.</summary>
        public bool ReplaceOnFirstPage { get; set; }

        public HttpChannelSummary? Channel { get; set; }

        public List<HttpExchangeRow> Exchanges { get; } = [];

        public bool More { get; set; }

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
