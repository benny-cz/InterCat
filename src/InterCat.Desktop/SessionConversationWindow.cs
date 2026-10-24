using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// One HTTP exchange's conversation (M8's conversation inspector, `contracts/http-exchanges-v1.md` §5d): its request and
/// its response side by side, each with what was recorded of its head and body - their bytes and buffers, and whether they
/// make the part whole - read from the records' metadata alone; and, only when the person asks, its two heads read whole
/// from the bytes the capture kept with consent to inspect them and decoded by the HTTP/1.1 head decoder: what the request
/// asked for, of which host, and what the response answered, each field with the bytes of its head it came from. Nothing
/// decoded is kept, and no body is read here. Show its buffers closes the window and opens the exchange's buffers in
/// InterCat's window, where C shows a buffer's content and its whole part: from the exchange to its messages and on to
/// the fragments they were recorded in.
/// </summary>
internal sealed class SessionConversationWindow : Window, IDisposable
{
    /// <summary>Why the window's actions wait while the exchange is read.</summary>
    private const string Reading = "The exchange is being read.";

    /// <summary>Why Decode its heads waits while they are read and decoded.</summary>
    private const string Decoding = "Its heads are being read and decoded.";

    /// <summary>Why the window's actions are not offered once no exchange could be shown.</summary>
    private const string Unread = "No exchange is shown; the status line says why.";

    /// <summary>What Decode its heads does, which a screen reader says beside its label.</summary>
    internal const string DecodeDoes = "Read its request and response heads, each whole, from the bytes kept with consent to "
        + "inspect them, and decode them: what the request asked for, of which host, and what the response answered, each "
        + "field with the bytes it came from. The decoding is made each time you ask, and never kept.";

    /// <summary>What Show its buffers does.</summary>
    internal const string BuffersDo = "Close this view and open the exchange's buffers in InterCat's window, as Enter on its "
        + "row does; C there shows a buffer's content and its whole part.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly string exchangeKey;
    private readonly EvidencePolicy policy;
    private readonly string? process;
    private readonly SessionClock clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock heading = new() { Text = "HTTP exchange", FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid facts = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 3 };
    private readonly TextBlock disclosure = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ConversationSide request = new("Request", "request");
    private readonly ConversationSide response = new("Response", "response");
    private readonly Button decode = new() { Content = "Decode its heads" };
    private readonly Button buffers = new() { Content = "Show its buffers" };
    private readonly Button close = new() { Content = "Close" };
    private HttpConversation? shown;
    private bool reading = true;
    private bool decoding;
    private bool closed;
    private bool disposed;

    /// <param name="policy">The evidence policy InterCat's window lists exchanges under, so the two hold the same ones.</param>
    /// <param name="process">The process whose exchange it is, by its name and PID, as InterCat's window names it.</param>
    /// <param name="clock">InterCat's window's clock, which says when the exchange began as its row does.</param>
    /// <param name="offersBuffers">
    /// Whether Show its buffers is offered: only where InterCat's window can then open them, not from a dialog of its own.
    /// </param>
    public SessionConversationWindow(
        string path, Guid expectedSessionId, string exchangeKey, EvidencePolicy policy, string? process, SessionClock clock,
        bool offersBuffers = true)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.exchangeKey = exchangeKey;
        this.policy = policy;
        this.process = process;
        this.clock = clock;
        Title = "InterCat · Conversation of one HTTP exchange";
        Width = 1_000;
        Height = 720;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        status.Text = "Reading the exchange…";
        status.Classes.Add("muted");
        AutomationProperties.SetName(status, "Conversation status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        disclosure.Text = "Its heads say what was asked and answered, which may be sensitive. They are read only when you ask, "
            + "each whole, from the bytes the capture kept with consent to inspect them, and decoded by InterCat's HTTP/1.1 "
            + "head decoder. Nothing decoded is kept, and no body is read here.";
        AutomationProperties.SetName(decode, "Decode its heads");
        AutomationProperties.SetName(buffers, "Show its buffers");
        buffers.IsVisible = offersBuffers;
        decode.Click += (_, _) => _ = DecodeAsync();
        buffers.Click += (_, _) => ShowBuffers();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        Grid.SetColumn(response.View, 1);
        sides.Children.Add(request.View);
        sides.Children.Add(response.View);
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { decode, buffers, close },
        };
        var header = new StackPanel { Spacing = 6, Children = { heading, status, facts, disclosure } };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(sides, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(sides);
        grid.Children.Add(footer);
        Content = grid;
        Offer();

        Opened += (_, _) => _ = ReadAsync(decodeHeads: false);
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

    /// <summary>Decodes the exchange's two heads, when the person asks, reading the exchange again with them.</summary>
    private Task DecodeAsync() => !decode.IsEnabled ? Task.CompletedTask : ReadAsync(decodeHeads: true);

    /// <summary>
    /// Reads the exchange from the current generation - its metadata alone, or its heads as well, decoded whole, where
    /// <paramref name="decodeHeads"/> - and shows it.
    /// </summary>
    private async Task ReadAsync(bool decodeHeads)
    {
        if (closed || decoding) return;
        reading = !decodeHeads;
        decoding = decodeHeads;
        if (decodeHeads) status.Text = "Reading and decoding its heads…";
        Offer();
        try
        {
            CancellationToken token = lifetime.Token;
            // The exchange is named by its first buffer's record, which is stable across generations; the read holds a
            // lease, so no retention release removes its records beneath it.
            HttpConversation read = await Task.Run(() => SessionHttpExchanges.Conversation(
                SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, exchangeKey, decodeHeads, policy, token), token);
            if (closed) return;
            Show(read);
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight read without altering evidence; nothing decoded was kept.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                status.Text = (decodeHeads ? "Could not decode its heads: " : "Could not read the exchange: ") + exception.Message;
            }
        }
        finally
        {
            reading = false;
            decoding = false;
            if (!closed)
            {
                Offer();
                KeepKeyboard();
            }
        }
    }

    /// <summary>
    /// Shows what <paramref name="read"/> holds: the exchange's facts and each side's parts, and each head's decoding where
    /// it was asked for; or why no exchange is shown.
    /// </summary>
    private void Show(HttpConversation read)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (read.Exchange is not { } row)
        {
            // A read that finds the exchange no longer held leaves nothing of an earlier one standing as if it were current.
            shown = null;
            status.Text = read.Problem ?? Unread;
            facts.Children.Clear();
            facts.RowDefinitions.Clear();
            request.Clear();
            response.Clear();
            return;
        }

        shown = read;
        HttpExchange exchange = row.Exchange;
        heading.Text = string.Create(culture, $"HTTP exchange {exchange.Number:N0}") + (process is null ? string.Empty : " of " + process);
        IReadOnlyList<HttpPart> missing = row.NotRecordedWhole();
        ShowFacts(
        [
            new("Began", clock.Record(exchange.FirstNanoseconds, culture)),
            new("Took", exchange.DurationNanoseconds is { } duration
                ? OperationText.Duration(duration, culture)
                : "not known: its response's end was not recorded"),
            new("Recorded", missing.Count == 0
                ? "whole: every part from its first buffer to its last"
                : "not whole: " + string.Join(", ", missing.Select(HttpExchangeRow.PartName))),
        ]);
        request.Show(exchange.RequestHead, exchange.RequestBody, read.Heads?.Request, read.Heads is not null,
            read.Heads?.Asked is { } asked ? asked + (read.Heads.Host is { } host ? ", of " + host : string.Empty) : null,
            read.Heads?.RequestBody(exchange.RequestBody), bodyNone: "none recorded, as for a request without one");
        response.Show(exchange.ResponseHead, exchange.ResponseBody, read.Heads?.Response, read.Heads is not null,
            read.Heads?.Answered, read.Heads?.ResponseBody(exchange.ResponseBody), bodyNone: "none recorded");
        status.Text = string.Create(culture, $"Read from generation {read.Generation:N0}")
            + (read.Heads is not { } heads ? "; its heads are decoded only when you ask." : "; " + Decoded(heads));
    }

    /// <summary>What the decoding of the two heads came to, in a phrase: "both heads decoded; nothing decoded is kept."</summary>
    private static string Decoded(HttpExchangeHeads heads)
    {
        string[] decoded =
        [
            .. new[] { ("request", heads.Request), ("response", heads.Response) }
                .Where(head => head.Item2 is { Read: not null })
                .Select(head => head.Item1),
        ];
        return (decoded.Length switch
        {
            2 => "both heads decoded",
            1 => $"its {decoded[0]} head decoded, and each side says what was not",
            _ => "neither head decoded; each side says why",
        }) + ". Nothing decoded is kept.";
    }

    private void ShowFacts(IReadOnlyList<ContentFact> known)
    {
        facts.Children.Clear();
        facts.RowDefinitions.Clear();
        for (int index = 0; index < known.Count; index++)
        {
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = known[index].Label, FontSize = 12 };
            label.Classes.Add("muted");
            var value = new SelectableTextBlock { Text = known[index].Value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(value, known[index].Label + ": " + known[index].Value);
            Grid.SetRow(label, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            facts.Children.Add(label);
            facts.Children.Add(value);
        }
    }

    /// <summary>Offers the window's actions, or says why not on each (R15); offered, each says what it does.</summary>
    private void Offer()
    {
        string? unshown = reading ? Reading : shown?.Exchange is null ? Unread : null;
        Offering.Offer(decode, decoding ? Decoding : unshown ?? WhyNotDecode(shown!.Exchange!.Exchange), DecodeDoes);
        Offering.Offer(buffers, unshown ?? (decoding ? Decoding : null), BuffersDo);
    }

    /// <summary>
    /// Why the exchange's heads are not decoded, or null when they are offered: a head that is not whole is never decoded,
    /// so with neither whole there is nothing to decode. A whole head kept without consent is offered, and its decoding says
    /// why it read none.
    /// </summary>
    private static string? WhyNotDecode(HttpExchange exchange) =>
        !exchange.RequestHead.Recorded && !exchange.ResponseHead.Recorded
            ? "Neither of its heads was recorded, so there is nothing to decode."
            : !exchange.RequestHead.Whole && !exchange.ResponseHead.Whole
                ? "Neither of its heads was recorded whole, so neither is decoded: what a head lacks could hold any of its lines."
                : null;

    /// <summary>
    /// Gives the keyboard to Decode its heads once the exchange is read, else Show its buffers, else Close, unless a control
    /// of the window has it: the buttons wait disabled while the exchange is read, which let it go. Posted, once laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (decode.IsEnabled ? decode : buffers.IsEffectivelyEnabled && buffers.IsVisible ? buffers : close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>Closes the window, asking InterCat's window to open the exchange's buffers.</summary>
    private void ShowBuffers()
    {
        if (!buffers.IsEnabled || !buffers.IsVisible) return;
        Close(true);
    }

    /// <summary>
    /// One side of the conversation - its request or its response - as the window shows it: what was recorded of its head
    /// and body, and, once its head is decoded, what it said, each field with the bytes of the head it came from, and what
    /// was not decoded and why.
    /// </summary>
    private sealed class ConversationSide
    {
        private readonly string name;
        private readonly TextBlock head = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock body = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock declared = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly TextBlock decoder = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly SelectableTextBlock said = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 12, RowSpacing = 3, IsVisible = false };
        private readonly TextBlock notDecoded = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        public ConversationSide(string title, string name)
        {
            this.name = name;
            var heading = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 };
            notDecoded.Classes.Add("caution");
            AutomationProperties.SetName(head, $"Its {name} head");
            AutomationProperties.SetName(body, $"Its {name} body");
            View = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Spacing = 6, Children = { heading, head, body, declared, decoder, said, fields, notDecoded } },
            };
            AutomationProperties.SetName(View, $"The exchange's {name}: what was recorded of it, and what its head says once decoded");
        }

        /// <summary>The side, which scrolls within its half of the window.</summary>
        public ScrollViewer View { get; }

        /// <summary>Shows nothing of an exchange, when none is read.</summary>
        public void Clear()
        {
            head.Text = body.Text = string.Empty;
            declared.IsVisible = decoder.IsVisible = said.IsVisible = fields.IsVisible = notDecoded.IsVisible = false;
        }

        /// <summary>
        /// Shows what was recorded of the side's head and body and, where its head was decoded (<paramref name="asked"/>),
        /// what its decoding made of it: <paramref name="summary"/> above its fields, or why it decoded none; and what the
        /// head declares of the body beside what was recorded of it, in the caution colour where the two disagree.
        /// </summary>
        public void Show(HttpPartRecord headRecord, HttpPartRecord bodyRecord, DecodedContent? decoded, bool asked, string? summary,
            HttpBodyDeclaration? declaration, string bodyNone)
        {
            head.Text = "Head: " + Recorded(headRecord, "none recorded");
            body.Text = "Body: " + Recorded(bodyRecord, bodyNone);
            AutomationProperties.SetName(head, $"Its {name} head: " + Recorded(headRecord, "none recorded"));
            AutomationProperties.SetName(body, $"Its {name} body: " + Recorded(bodyRecord, bodyNone));
            declared.Text = declaration?.Statement ?? string.Empty;
            declared.IsVisible = declaration is not null;
            declared.Classes.Set("caution", declaration is { Contradicts: true });
            AutomationProperties.SetName(declared, $"Its {name} body, as its head declares it: {declaration?.Statement}");
            decoder.IsVisible = asked && decoded is not null;
            decoder.Text = decoded is null ? string.Empty : decoded.Heading + (decoded.Read is { } read
                ? ": it read " + read.Describe(CultureInfo.CurrentCulture) + " of " + (decoded.Part ?? "its head") + "."
                : ": it read no byte of " + (decoded.Part ?? "its head") + ".");
            said.Text = summary ?? string.Empty;
            said.IsVisible = summary is not null;
            AutomationProperties.SetName(said, $"Its {name} said: {summary}");
            ShowFields(decoded?.Fields ?? []);
            notDecoded.Text = decoded?.NotDecoded is { } why ? "Not decoded: " + why : string.Empty;
            notDecoded.IsVisible = decoded?.NotDecoded is not null;
        }

        /// <summary>What was recorded of a part, in words: "181 B in 2 buffers, recorded whole".</summary>
        private static string Recorded(HttpPartRecord part, string none) => !part.Recorded
            ? none
            : string.Create(CultureInfo.CurrentCulture, $"{part.Bytes:N0} B in ") + CountText.Of(part.Buffers, "buffer")
                + (part.Whole ? ", recorded whole" : ", not recorded whole");

        /// <summary>Each decoded field with the bytes of its head it came from, its value selectable and read with its name.</summary>
        private void ShowFields(IReadOnlyList<DecodedField> decoded)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            fields.Children.Clear();
            fields.RowDefinitions.Clear();
            fields.IsVisible = decoded.Count > 0;
            if (decoded.Count == 0) return;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] headers = ["Field", "Bytes", "Value"];
            for (int column = 0; column < headers.Length; column++)
            {
                var header = new TextBlock { Text = headers[column], FontSize = 12 };
                header.Classes.Add("muted");
                Grid.SetColumn(header, column);
                fields.Children.Add(header);
            }

            for (int index = 0; index < decoded.Count; index++)
            {
                DecodedField field = decoded[index];
                string bytes = field.Bytes.Describe(culture);
                fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = field.Name, FontSize = 12 };
                var span = new TextBlock { Text = bytes, FontSize = 12 };
                var value = new SelectableTextBlock { Text = field.Value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(value, $"{field.Name}, {bytes}: {field.Value}");
                Grid.SetRow(label, index + 1);
                Grid.SetRow(span, index + 1);
                Grid.SetColumn(span, 1);
                Grid.SetRow(value, index + 1);
                Grid.SetColumn(value, 2);
                fields.Children.Add(label);
                fields.Children.Add(span);
                fields.Children.Add(value);
            }
        }
    }
}
