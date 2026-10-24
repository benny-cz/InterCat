using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A table of a process's exchanges with what their heads say (`contracts/http-exchanges-v1.md` §5f): its CSV, how many
/// exchanges it holds, how many of their heads were recorded and not decoded, and whether an export's limit left
/// exchanges of the scope out.
/// </summary>
public sealed record HttpHeadsExport(string Content, int Exchanges, int Undecoded, bool More);

/// <summary>
/// A process's HTTP exchanges on the ladder (ADR-037, M8): the process rung lists them as one channel, the channel rung
/// lists the exchanges a page at a time, each with what was recorded of its four parts, and an exchange descends to its own
/// buffers, where C opens a buffer's content and its whole part. Everything a row says comes from the records' metadata and
/// source fields; no content is read to list them. C on an exchange opens its conversation, which reads what its heads say
/// only when asked (`contracts/http-exchanges-v1.md` §5d).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private HttpChannelsLoad? httpChannels;
    private HttpExchangesLoad? httpExchanges;
    private IReadOnlyList<RungRow> httpChannelRows = [];
    private IReadOnlyList<RungRow> httpExchangeRows = [];
    private HttpExchangeSpanPage? httpSpans;
    private (string Key, TimeRange Viewport, int Columns)? requestedHttpSpans;
    private CancellationTokenSource? httpSpanQuery;

    /// <summary>Whether the user is at a published session's HTTP channel rung, whose rows are a process's exchanges.</summary>
    public bool IsHttpChannelRung => evidenceSource is not null
        && ladder.Current.Level == DetailLevel.Channel
        && HttpExchangeKeys.IsHttp(ladder.Current.Focus?.Key);

    /// <summary>Completes when the latest read of HTTP exchanges has applied or reported a problem.</summary>
    public Task HttpReady { get; private set; } = Task.CompletedTask;

    public bool CanLoadMoreExchanges => IsHttpChannelRung && httpExchanges is { Loading: false, Problem: null, More: true };

    /// <summary>The exchange chosen at the HTTP channel rung, whose conversation C opens; null where none is chosen.</summary>
    public HttpExchangeRow? SelectedHttpExchange => IsHttpChannelRung && DescribedRow is { } row && httpExchanges is { } load
        ? load.Exchanges.FirstOrDefault(exchange => exchange.Key == row.Key)
        : null;

    /// <summary>Whether an exchange is chosen, so its conversation can be opened.</summary>
    public bool HasSelectedHttpExchange => SelectedHttpExchange is not null;

    /// <summary>The process whose exchanges the HTTP channel rung lists, by its name and PID; null elsewhere.</summary>
    public string? HttpExchangeProcess =>
        IsHttpChannelRung && HttpExchangeKeys.TryParseChannel(ladder.Current.Focus?.Key, out ProcessInstanceId instance)
            ? wholeSnapshot.Processes.FirstOrDefault(process => process.Id == instance)?.NameWithPid
            : null;

    /// <summary>Whether the rung lists a process's HTTP exchanges, whose heads Export with their heads… writes.</summary>
    public bool OffersHttpHeadsExport => IsHttpChannelRung;

    /// <summary>Whether the HTTP channel rung lists exchanges whose heads an export can write.</summary>
    public bool CanExportHttpHeads => IsHttpChannelRung && evidenceSource is not null
        && httpExchanges is { Problem: null, Exchanges.Count: > 0 };

    /// <summary>What exporting the exchanges with their heads does, or why there is nothing to export yet.</summary>
    public string ExportHttpHeadsTip => CanExportHttpHeads
        ? "Write this process's exchanges in the scope to a table with what their heads say - what each request asked for, "
            + "of which host, and what was answered - read from content kept with consent to inspect it; the table is content."
        : NothingToExport;

    /// <summary>
    /// This process's exchanges in the rung's scope, every one up to an export's limit, each with what its heads say, as the
    /// table `icat exchanges --decode --format csv` writes (`contracts/http-exchanges-v1.md` §5f), read off the UI thread
    /// in one pass of one generation. A head not whole, or kept without consent, is not decoded, and its cell says why.
    /// </summary>
    public async Task<HttpHeadsExport> ExportHttpHeadsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanExportHttpHeads || evidenceSource is not { } source || ladder.Current.Focus is not { } channel)
        {
            throw new InvalidOperationException(ExportHttpHeadsTip);
        }

        HttpExchangeTableRead read = await source.HttpHeadsTableAsync(channel.Key, CountedScope, ListedExportLimit, cancellationToken)
            .AnsweredLater();
        if (read.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        int undecoded = read.Lines.Sum(line =>
            (line.Heads?.Request is { Read: null } ? 1 : 0) + (line.Heads?.Response is { Read: null } ? 1 : 0));
        return new(HttpExchangeTable.Csv(read.Lines, decoded: true), read.Lines.Count, undecoded, read.More);
    }

    private void RaiseHttpHeadsExport()
    {
        OnPropertyChanged(nameof(OffersHttpHeadsExport));
        OnPropertyChanged(nameof(CanExportHttpHeads));
        OnPropertyChanged(nameof(ExportHttpHeadsTip));
    }

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

    /// <summary>
    /// What an HTTP channel rung states: its exchanges, how many were recorded whole, how long one took, and a next page
    /// that could not be read.
    /// </summary>
    private string HttpExchangeSummary(bool brief)
    {
        if (httpExchanges is not { } load) return string.Empty;
        if (load.Problem is not null) return "Exchanges unavailable";
        if (load.Channel is not { } channel) return "Reading exchanges…";
        string loaded = (load.Exchanges.Count < channel.Exchanges
                ? string.Create(CultureInfo.CurrentCulture, $" · {load.Exchanges.Count:N0} listed")
                : string.Empty)
            + (load is { PageProblem: { } failed, Loading: false } ? NextPageFailed(failed, brief) : string.Empty);
        return brief
            ? channel.Outcome(CultureInfo.CurrentCulture) + loaded
            : channel.Outcome(CultureInfo.CurrentCulture) + loaded
                + " · grouped by WinINet's exchange numbers; E shows an exchange's buffers, and C its conversation";
    }

    /// <summary>
    /// A page of exchanges that could not be read. The first leaves the rung with none - the previous scope's are not
    /// passed off as this one's - and says <paramref name="first"/>; a later one leaves the exchanges listed, says
    /// <paramref name="later"/> of the next page, and Load more asks for it again when <paramref name="again"/>.
    /// </summary>
    private static void Failed(HttpExchangesLoad load, string first, string later, int offset, bool again)
    {
        if (offset > 0)
        {
            load.PageProblem = later;
            load.More &= again;
            return;
        }

        load.Exchanges.Clear();
        load.ReplaceOnFirstPage = false;
        load.Problem = first;
        load.More = false;
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
            load.Problem = exception.Message;
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
                // A channel the generation no longer holds has no next page either; the exchanges listed stay.
                Failed(load, page.Problem, page.Problem, offset, again: false);
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
                load.PageProblem = null;
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
            Failed(load, "This process's HTTP exchanges could not be read: " + exception.Message, exception.Message, offset,
                again: true);
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
            ? [.. exchanges.Exchanges.Select(exchange => HttpExchangeRungRow(exchange, tokens, TimeBase))]
            : [];
        RaiseRpcChanged();
        OnPropertyChanged(nameof(HasSelectedHttpExchange));
    }

    /// <summary>A process's HTTP exchanges as one row: how many, how they were recorded, and the bytes of their messages.</summary>
    private static RungRow HttpChannelRungRow(HttpChannelSummary channel, FamilyTokens tokens)
    {
        LadderRow source = ProcessRungRows.HttpChannel(channel, CultureInfo.CurrentCulture);
        string detail = source.Detail;
        string bytes = HttpBytes(channel);
        // The rail is narrow, so the row reads "HTTP exchanges"; its crumb, filter and records' scope say the whole name.
        return new(channel.Key, "HTTP exchanges", detail, channel.Records.ToString("N0", CultureInfo.CurrentCulture),
            bytes, tokens.Label, tokens.Glyph, string.Empty, NavigationState.Name(DetailLevel.Channel), source)
        {
            SpokenName = $"{HttpChannelSummary.Name}, {detail}, {bytes}, {Spoken.Count(channel.Records, "buffer record")}. "
                + "Press Enter to list the exchanges.",
        };
    }

    /// <summary>What a process's HTTP exchanges' messages held, as their row and the card state it.</summary>
    private static string HttpBytes(HttpChannelSummary channel) => string.Create(CultureInfo.CurrentCulture,
        $"{channel.RequestBytes:N0} B sent, {channel.ResponseBytes:N0} B received in HTTP messages");

    /// <summary>One exchange's row: how long it took, or why that is not known, and what was recorded of its parts.</summary>
    private static RungRow HttpExchangeRungRow(HttpExchangeRow row, FamilyTokens tokens, SessionClock clock)
    {
        HttpExchange exchange = row.Exchange;
        string when = clock.Record(exchange.FirstNanoseconds, CultureInfo.CurrentCulture);

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
                + "buffers, or C to read its conversation.",
        };
    }

    private void CancelHttp()
    {
        CancelHttpChannels();
        CancelHttpExchanges();
        CancelHttpSpans();
    }

    /// <summary>Completes when the latest read of the exchange lane's exchanges has applied or reported a problem.</summary>
    public Task HttpSpansReady { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The HTTP channel rung's exchanges within the drawn viewport, in reading order; null where no exchange lane is drawn,
    /// and empty where the view holds more exchanges than the lane draws one by one and <see cref="HttpExchangeDensity"/>
    /// draws them instead.
    /// </summary>
    public IReadOnlyList<HttpExchangeSpanView>? HttpExchangeSpans => ShowsHttpExchangeLane ? httpSpans!.Exchanges : null;

    /// <summary>The exchange lane's density columns (§6.2), when the viewport holds more exchanges than it draws one by one.</summary>
    public HttpExchangeDensity? HttpExchangeDensity => ShowsHttpExchangeLane ? httpSpans!.Density : null;

    /// <summary>
    /// Whether the timeline draws the process's HTTP exchanges as a lane of bars under the machine row, each from its first
    /// buffer to its response's end, as an RPC channel's calls are drawn from their start to their stop.
    /// </summary>
    public bool ShowsHttpExchangeLane => IsHttpChannelRung && httpSpans is { Problem: null };

    /// <summary>
    /// What the exchange lane holds, in one short line: every exchange in view, drawn one by one or as density. Formatted
    /// once per read of the lane, as the call lane's note is, since the timeline states it on every repaint (R11).
    /// </summary>
    public string HttpExchangeLaneNote
    {
        get
        {
            if (httpSpans is not { Problem: null } spans || !IsHttpChannelRung)
            {
                return string.Empty;
            }

            if (!ReferenceEquals(httpExchangeLaneNote.Page, spans))
            {
                httpExchangeLaneNote = (spans, OperationLaneNote(spans.Total, spans.Density is not null));
            }

            return httpExchangeLaneNote.Text;
        }
    }

    private (object? Page, string Text) httpExchangeLaneNote = (null, string.Empty);

    /// <summary>The exchange selected in the HTTP channel rung's table, which the exchange lane outlines.</summary>
    public string? SelectedHttpExchangeKey => IsHttpChannelRung ? selectedRung?.Key : null;

    /// <summary>Selects an exchange the lane drew, when its row is among those listed; false when it is not.</summary>
    public bool SelectHttpExchange(string key)
    {
        if (!IsHttpChannelRung || RungRows.FirstOrDefault(row => row.Key == key) is not { } row)
        {
            return false;
        }

        SelectedRung = row;
        return true;
    }

    /// <summary>What an exchange the lane draws is, for its hover card: how long it took, its messages and how it was recorded.</summary>
    public HoverCard DescribeHttpExchangeHover(HttpExchangeSpanView span)
    {
        ArgumentNullException.ThrowIfNull(span);
        string title = span.Ended
            ? "HTTP exchange · " + OperationText.Duration((span.LastTicks - span.FirstTicks) * 100, CultureInfo.CurrentCulture)
            : "HTTP exchange · its response's end was not recorded";
        return new(title,
        [
            string.Create(CultureInfo.CurrentCulture, $"Exchange {span.Number:N0} of its process"),
            "Started " + TimeBase.Instant(span.FirstTicks, Math.Max(1, span.LastTicks - span.FirstTicks + 1),
                CultureInfo.CurrentCulture),
            string.Create(CultureInfo.CurrentCulture, $"{span.RequestBytes:N0} B sent, {span.ResponseBytes:N0} B received"),
            span.Complete ? "Recorded whole" : "Not recorded whole; its row says which part",
            RungRows.Any(row => row.Key == span.Key) ? "Click selects its row" : "Load more exchanges to select its row",
        ]);
    }

    /// <summary>What one density column of the exchange lane holds, for its hover card.</summary>
    public HoverCard DescribeHttpDensityHover(int column)
    {
        HttpExchangeDensity density = HttpExchangeDensity ?? throw new InvalidOperationException("The exchange lane draws no density.");
        TimeRange interval = density.ColumnInterval(column);
        long running = density.Running[column];
        long incomplete = density.Incomplete[column];
        string title = running == 0
            ? "No exchange running"
            : string.Create(CultureInfo.CurrentCulture, $"{running:N0} {(running == 1 ? "exchange" : "exchanges")} running");
        var lines = new List<string>();
        if (incomplete > 0)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture, $"{incomplete:N0} of them not recorded whole"));
        }

        lines.Add("From " + TimeBase.Instant(interval.StartTicks, interval.SpanTicks, CultureInfo.CurrentCulture)
            + " for " + WorkspaceTime.FormatDuration(interval.SpanTicks, CultureInfo.CurrentCulture));
        lines.Add(string.Create(CultureInfo.CurrentCulture,
            $"The busiest column holds {density.Maximum:N0}; an exchange counts in every column it ran in"));
        lines.Add("Click selects this interval; zoom in to see each exchange");
        return new(title, lines);
    }

    /// <summary>
    /// Reads the exchanges the lane draws for <paramref name="viewport"/> at an HTTP channel rung, superseding a read still
    /// under way; any other rung lets the lane go.
    /// </summary>
    private void RequestHttpSpans(TimeRange viewport)
    {
        if (disposed)
        {
            return;
        }

        if (!IsHttpChannelRung || evidenceSource is null)
        {
            if (httpSpans is not null || requestedHttpSpans is not null)
            {
                CancelHttpSpans();
                RaiseHttpSpansChanged();
            }

            return;
        }

        string key = ladder.Current.Focus?.Key ?? string.Empty;
        int columns = CallDensityColumns;
        if (requestedHttpSpans == (key, viewport, columns))
        {
            return;
        }

        requestedHttpSpans = (key, viewport, columns);
        httpSpanQuery?.Cancel();
        httpSpanQuery?.Dispose();
        var query = new CancellationTokenSource();
        httpSpanQuery = query;
        HttpSpansReady = LoadHttpSpansAsync(evidenceSource, key, viewport, columns, query);
    }

    private async Task LoadHttpSpansAsync(
        SessionEvidenceSource source, string key, TimeRange viewport, int columns, CancellationTokenSource query)
    {
        try
        {
            HttpExchangeSpanPage page = await source.HttpSpansAsync(key, viewport, columns, query.Token).AnsweredLater();
            if (disposed || !ReferenceEquals(httpSpanQuery, query)) return;
            httpSpans = page;
        }
        catch (OperationCanceledException) when (query.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(httpSpanQuery, query)) return;
            httpSpans = new(Guid.Empty, 0, viewport, [], 0, exception.Message);
        }

        RaiseHttpSpansChanged();
    }

    private void CancelHttpSpans()
    {
        httpSpanQuery?.Cancel();
        httpSpanQuery?.Dispose();
        httpSpanQuery = null;
        requestedHttpSpans = null;
        httpSpans = null;
    }

    private void RaiseHttpSpansChanged()
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(HttpExchangeSpans));
        OnPropertyChanged(nameof(HttpExchangeDensity));
        OnPropertyChanged(nameof(ShowsHttpExchangeLane));
        OnPropertyChanged(nameof(HttpExchangeLaneNote));
        OnPropertyChanged(nameof(TimelineCaption));
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

        /// <summary>
        /// Whether the exchanges shown while this scope's first page is read are an earlier publication's for this process and
        /// scope, rather than the previous scope's, so a later publication may take them on in turn.
        /// </summary>
        public bool Adopted { get; set; }

        public HttpChannelSummary? Channel { get; set; }

        public List<HttpExchangeRow> Exchanges { get; } = [];

        public bool More { get; set; }

        public bool Loading { get; set; }

        public string? Problem { get; set; }

        /// <summary>Why the next page could not be read, the exchanges of the pages before it still listed; null otherwise.</summary>
        public string? PageProblem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
