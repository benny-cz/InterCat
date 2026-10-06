using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// One record's kept content (§3.7, ADR-036): what the bytes are, how many of the message were kept and which are
/// missing, and - only after the person asks - the bytes themselves, as inert hexadecimal and, where the source declares
/// text, that text with every control and invisible character made visible. A typed range chooses which bytes are shown,
/// copied or saved; at most a bounded window is shown at once, and nothing is decoded, searched or sent anywhere. A
/// buffer of a part shows its part instead when asked: whole, as one run of bytes, or, when it is not whole, buffer by
/// buffer with each gap a line of its own, chosen by buffer and never saved as one (M8).
/// </summary>
internal sealed class SessionContentWindow : Window, IDisposable
{
    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New");

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly SessionEvidenceRecord selected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid facts = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 3 };
    private readonly TextBlock disclosure = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer factsScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button reveal = new() { Content = "Show the kept bytes", IsEnabled = false, IsVisible = false };
    private readonly TextBox first = new() { Width = 110 };
    private readonly TextBox last = new() { Width = 110 };
    private readonly Button showRange = new() { Content = "Show" };
    private readonly Button showAll = new() { Content = "All kept bytes" };
    private readonly TextBlock rangeSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock rangeProblem = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel rangePanel = new() { Spacing = 6, IsVisible = false };
    private readonly ListBox hex = new() { FontFamily = Monospace, FontSize = 12 };
    private readonly SelectableTextBlock text = new() { FontFamily = Monospace, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock textNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TabItem textTab = new() { Header = "Text" };
    private readonly TabControl views = new() { IsVisible = false };
    private readonly Button copy = new() { Content = "Copy as hex", IsEnabled = false };
    private readonly Button save = new() { Content = "Save bytes…", IsEnabled = false };
    private readonly TextBlock partStatement = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button partToggle = new() { Content = "Show its whole part", IsVisible = false };
    private readonly TextBlock rangeFrom = new() { Text = "Bytes from", VerticalAlignment = VerticalAlignment.Center };
    private SessionContentDetail? detail;
    private SessionContentPartDetail? part;
    private byte[]? shownBytes;
    private bool showingPart;
    private ContentPartLines? partLines;
    private ContentRange? kept;
    private ContentRange? range;
    private bool loading;
    private bool saving;
    private bool closed;
    private bool disposed;

    public SessionContentWindow(string path, Guid expectedSessionId, SessionEvidenceRecord selected)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.selected = selected;
        Title = "InterCat · Content of one record";
        Width = 900;
        Height = 700;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = "Kept content of the selected record",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };
        status.Text = "Finding the record's kept content…";
        status.Classes.Add("muted");
        disclosure.Text = "Message bytes may be sensitive. They stay hidden until you choose to show them. Then they are "
            + "shown as inert hexadecimal and, where the source declares text, as that text with control characters made "
            + "visible; at most 64 KiB at once. Nothing is decoded, searched or sent anywhere, and a copy or a saved file "
            + "holds exactly the bytes you chose.";

        AutomationProperties.SetName(reveal, "Show the kept bytes");
        AutomationProperties.SetName(status, "Content status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(first, "First byte of the range");
        AutomationProperties.SetName(last, "Last byte of the range");
        AutomationProperties.SetName(showRange, "Show this range of bytes");
        AutomationProperties.SetHelpText(showAll, "Show all kept bytes");
        AutomationProperties.SetName(hex, "Hex view of the chosen bytes");
        AutomationProperties.SetName(text, "The chosen bytes as the text their source declares");
        AutomationProperties.SetHelpText(copy, "Copy the chosen bytes as hex");
        AutomationProperties.SetHelpText(save, "Save the chosen bytes to a file");
        AutomationProperties.SetName(rangeSummary, "Which bytes are shown");
        AutomationProperties.SetName(partToggle, "Switch between this buffer and its part");
        rangeProblem.Classes.Add("caution");

        // A hex dump reads line under line: its lines keep a text line's height rather than a menu row's, and its one or
        // two tabs are labels, not headings.
        Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 1)),
                new Setter(Layoutable.MinHeightProperty, 0d),
            },
        });
        Styles.Add(new Style(selector => selector.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 14d),
                new Setter(Layoutable.MinHeightProperty, 0d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 4)),
            },
        });
        hex.Bind(TemplatedControl.BackgroundProperty, hex.GetResourceObservable("Surface.Plot"));
        // A part shown with its gaps holds lines that are not bytes: a buffer's heading, and each gap in the caution
        // colour, wrapped within the view rather than scrolled past, and read aloud as its words alone.
        ScrollViewer.SetHorizontalScrollBarVisibility(hex, ScrollBarVisibility.Disabled);
        hex.ItemTemplate = new FuncDataTemplate<ContentLine>((shown, _) =>
        {
            var line = new TextBlock { FontFamily = Monospace, FontSize = 12 };
            line.Bind(TextBlock.TextProperty, new Binding(nameof(ContentLine.Line)));
            line.Bind(AutomationProperties.NameProperty,
                new Binding(shown is ContentMark ? nameof(ContentMark.Text) : nameof(ContentLine.Line)));
            if (shown is ContentMark mark)
            {
                line.TextWrapping = TextWrapping.Wrap;
                if (mark.Gap)
                {
                    line.Classes.Add("caution");
                }
                else
                {
                    line.FontWeight = FontWeight.SemiBold;
                }
            }

            return line;
        });
        textTab.Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                PinnedTop(textNote),
                new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            },
        };
        views.Items.Add(new TabItem { Header = "Hex", Content = hex });
        views.Items.Add(textTab);

        reveal.Click += (_, _) => _ = LoadAsync(revealBytes: true);
        showRange.Click += (_, _) => ApplyTypedRange();
        showAll.Click += (_, _) =>
        {
            if (GappedPart() is { } gapped && ContentBytesView.RecordedBuffers(gapped) is { } recorded) ShowBuffers(recorded);
            else if (kept is { } all) Show(all);
        };
        first.KeyDown += ApplyOnEnter;
        last.KeyDown += ApplyOnEnter;
        partToggle.Click += (_, _) => TogglePart();
        copy.Click += (_, _) => _ = CopyAsync();
        save.Click += (_, _) => _ = SaveAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        // A buffer of a part - an HTTP head or body - says whether its part was kept whole, and shows it whole if so (M8).
        rangePanel.Children.Add(partStatement);
        rangePanel.Children.Add(partToggle);
        rangePanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                rangeFrom,
                first,
                new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center },
                last,
                showRange,
                showAll,
            },
        });
        rangePanel.Children.Add(rangeSummary);
        rangePanel.Children.Add(rangeProblem);

        factsScroller.Content = new StackPanel { Spacing = 6, Children = { facts, disclosure } };
        AutomationProperties.SetName(factsScroller, "What was kept of the record's message, and under which policy");
        var header = new StackPanel { Spacing = 6, Children = { heading, status, factsScroller, reveal, rangePanel } };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { copy, save, close },
        };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(views, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(views);
        grid.Children.Add(footer);
        Content = grid;

        Opened += (_, _) => _ = LoadAsync(revealBytes: false);
        Closed += (_, _) => Dispose();
        views.SizeChanged += (_, _) => FitFacts();
    }

    /// <summary>The least room the facts keep beside shown bytes: about three of their rows.</summary>
    internal const double MinimumFactsHeight = 72;

    /// <summary>The room shown bytes keep before the facts give up any: the tabs and about ten lines.</summary>
    internal const double MinimumBytesHeight = 200;

    /// <summary>
    /// Once the bytes are shown they are what a person came to read. The facts and the notice above them share the room
    /// between the heading and the range with the bytes, and give up as much of it as leaves the bytes ten lines, scrolling
    /// within what they keep: the smallest window showed the bytes two lines at a time. A taller window keeps both whole.
    /// </summary>
    private void FitFacts()
    {
        double shared = factsScroller.Bounds.Height + views.Bounds.Height;
        double fitted = views.IsVisible ? Math.Max(MinimumFactsHeight, shared - MinimumBytesHeight) : double.PositiveInfinity;
        if (Math.Abs(fitted - factsScroller.MaxHeight) > 0.5 || double.IsInfinity(fitted) != double.IsInfinity(factsScroller.MaxHeight))
        {
            factsScroller.MaxHeight = fitted;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private static Control PinnedTop(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        control.Margin = new Thickness(0, 4, 0, 6);
        return control;
    }

    private async Task LoadAsync(bool revealBytes)
    {
        if (loading || closed) return;
        loading = true;
        reveal.IsEnabled = false;
        status.Text = revealBytes ? "Reading and checking the kept bytes…" : "Finding the record's kept content…";
        try
        {
            CancellationToken token = lifetime.Token;
            // The record's raw identity is stable across generations, so a live publication since its page was read
            // does not strand it; the read holds a lease, so no retention release removes the chunk beneath it.
            SessionContentDetail result = await Task.Run(() => SessionContentQuery.Read(
                SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, selected, revealBytes, token), token);
            if (closed) return;
            detail = result;

            // A buffer of an HTTP head or body belongs to a part, found from source fields alone; its bytes are read only
            // when the record's are asked for.
            if (result.Available && selected.Observation.Mechanism == Mechanism.Http)
            {
                bool withBytes = revealBytes && result.Bytes is not null;
                part = await Task.Run(() => SessionContentPartQuery.Read(
                    SharedSessionStores.Open(path, expectedSessionId), expectedSessionId, selected.Observation, withBytes, token), token);
                if (closed) return;
            }
            if (!result.Available || result.Entry is not { } entry)
            {
                status.Text = "No kept content in the current generation";
                disclosure.Text = result.UnavailableReason ?? string.Empty;
                reveal.IsVisible = false;
                return;
            }

            ShowFacts(ContentBytesView.Facts(entry, selected.Observation, result.Generation, CultureInfo.CurrentCulture, part));
            if (part is { IsPart: true } stated)
            {
                partStatement.Text = stated.Statement;
                partStatement.IsVisible = true;
            }
            kept = ContentBytesView.Kept(entry.Fragment);
            status.Text = string.Create(CultureInfo.CurrentCulture,
                $"Checked {entry.ChunkName} in generation {result.Generation:N0}");
            if (kept is null)
            {
                disclosure.Text = entry.Fragment.Disposition == ContentDispositionV1.OmittedBySessionLimit
                    ? "No byte of this message was kept, so there is nothing to show."
                    : "The message held no bytes, so there is nothing to show.";
                reveal.IsVisible = false;
                return;
            }

            if (!entry.Inspectable)
            {
                disclosure.Text = "The capture kept these bytes without consent to inspect them, so they are never shown, "
                    + "copied or saved one record at a time. An original evidence package carries them as evidence.";
                reveal.IsVisible = false;
                return;
            }

            if (result.Bytes is null)
            {
                reveal.IsVisible = true;
                reveal.IsEnabled = true;
                reveal.Focus();
                return;
            }

            reveal.IsVisible = false;
            rangePanel.IsVisible = true;
            views.IsVisible = true;
            shownBytes = result.Bytes;
            if (part is { IsPart: true } found)
            {
                // A whole part is shown as one run of its bytes; one that is not whole, with its gaps in place.
                partToggle.IsVisible = true;
                partToggle.IsEnabled = found.Complete ? found.Bytes is { Length: > 0 } : found.BytesRead;
                partToggle.Content = PartToggleText(found);
            }
            textTab.IsVisible = ContentBytesView.DeclaresText(entry.Fragment.Encoding);
            textTab.Header = entry.Fragment.Encoding == ContentEncodingV1.Utf8 ? "Text (UTF-8)" : "Text (UTF-16)";
            Show(kept.Value);

            // The reveal button is gone, so the keyboard moves to the first line of bytes once it is laid out: a list
            // takes no focus of its own, and the arrows then read on from there.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (closed || hex.ItemCount == 0) return;
                hex.ScrollIntoView(0);
                if (hex.ContainerFromIndex(0) is Control line) line.Focus(NavigationMethod.Directional);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight read without altering evidence.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                status.Text = "Could not read the record's kept content: " + exception.Message;
                reveal.IsEnabled = detail?.Entry is { Inspectable: true } && detail.Bytes is null;
            }
        }
        finally
        {
            loading = false;
        }
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

    private void ApplyOnEnter(object? sender, KeyEventArgs key)
    {
        if (key.Key != Key.Enter) return;
        ApplyTypedRange();
        key.Handled = true;
    }

    private void ApplyTypedRange()
    {
        if (GappedPart() is { } gapped)
        {
            if (ContentBytesView.RecordedBuffers(gapped) is not { } recorded) return;
            if (!ContentBytesView.TryParseBuffers(first.Text, last.Text, recorded, out ContentBufferRange buffers, out string? wrong))
            {
                rangeProblem.Text = wrong;
                rangeProblem.IsVisible = true;
                return;
            }

            ShowBuffers(buffers);
            return;
        }

        if (kept is not { } all) return;
        if (!ContentBytesView.TryParseRange(first.Text, last.Text, all, out ContentRange typed, out string? problem))
        {
            rangeProblem.Text = problem;
            rangeProblem.IsVisible = true;
            return;
        }

        Show(typed);
    }

    /// <summary>Shows <paramref name="chosen"/>: its first bounded window in hex and, where declared, as text.</summary>
    private void Show(ContentRange chosen)
    {
        if (shownBytes is not { } bytes || detail?.Entry is null || kept is not { } all) return;
        range = chosen;
        rangeProblem.IsVisible = false;
        first.Text = chosen.First.ToString(CultureInfo.InvariantCulture);
        last.Text = chosen.Last.ToString(CultureInfo.InvariantCulture);
        ContentRange shown = ContentBytesView.Shown(chosen);
        ReadOnlyMemory<byte> window = ContentBytesView.Slice(bytes, all, shown);
        hex.ItemsSource = ContentBytesView.Rows(window.Span, shown.First);
        CultureInfo culture = CultureInfo.CurrentCulture;
        rangeSummary.Text = "Chosen: " + chosen.Describe(culture)
            + (showingPart
                ? string.Create(culture, $", of the {all.Length:N0} bytes of its whole part")
                : string.Create(culture, $", of the {all.Length:N0} kept"))
            + (shown == chosen
                ? "."
                : string.Create(culture, $". The view shows its first {shown.Length:N0}; a copy takes those, and Save writes all of them."));
        if (textTab.IsVisible)
        {
            ContentText read = ContentBytesView.Text(window.Span, detail.Entry!.Fragment.Encoding);
            text.Text = read.Text;
            var notes = new List<string>(3);
            if (read.Escaped > 0)
                notes.Add(string.Create(culture, $"{read.Escaped:N0} control or invisible characters are shown as marks, like ␀ or ⟨U+202E⟩"));
            if (read.Replaced > 0)
                notes.Add(string.Create(culture, $"{read.Replaced:N0} show as �, where the bytes are not this encoding - as a range cut mid-character is not"));
            if (read.Cut)
                notes.Add(string.Create(culture, $"the text stops after {ContentBytesView.MaximumTextCharacters:N0} characters"));
            textNote.Text = notes.Count == 0
                ? "The text exactly as the bytes hold it."
                : char.ToUpperInvariant(notes[0][0]) + string.Join("; ", notes)[1..] + ".";
        }

        copy.IsEnabled = true;
        save.IsEnabled = true;
    }

    /// <summary>
    /// Shows the chosen buffers of a part that is not whole (M8): each buffer's heading and kept bytes, numbered from its
    /// own first byte, and each gap where it falls, as a line no byte can pass for. Such a part is never saved as one.
    /// </summary>
    private void ShowBuffers(ContentBufferRange chosen)
    {
        if (GappedPart() is not { } gapped || ContentBytesView.RecordedBuffers(gapped) is not { } recorded) return;
        rangeProblem.IsVisible = false;
        first.Text = chosen.First.ToString(CultureInfo.InvariantCulture);
        last.Text = chosen.Last.ToString(CultureInfo.InvariantCulture);
        CultureInfo culture = CultureInfo.CurrentCulture;
        ContentPartLines lines = ContentBytesView.PartLines(gapped, chosen, culture);
        partLines = lines;
        hex.ItemsSource = lines.Lines;
        rangeSummary.Text = "Chosen: " + chosen.Describe(culture) + " of " + gapped.Name
            + (chosen == recorded
                ? ", from its first buffer recorded to its last."
                : string.Create(culture, $", whose recorded buffers run from {recorded.First:N0} to {recorded.Last:N0}."))
            + " Each buffer's bytes are numbered from its own first byte, and nothing stands in for a gap"
            + (lines.StoppedBefore is { } stop
                ? string.Create(culture, $"; the view stops before buffer {stop:N0}, at the {ContentBytesView.MaximumShownBytes:N0} bytes it shows at once")
                : string.Empty)
            + ". A part that is not whole is never saved as one: save a buffer from its own record.";
        copy.IsEnabled = lines.Lines.Count > 0;
        save.IsEnabled = false;
    }

    /// <summary>The part shown with its gaps in place, when that is what the view shows.</summary>
    private SessionContentPartDetail? GappedPart() => showingPart && part is { Complete: false } gapped ? gapped : null;

    private static string PartToggleText(SessionContentPartDetail found) =>
        found.Complete ? "Show its whole part" : "Show its part, with its gaps";

    /// <summary>
    /// Switches the view between this record's buffer and the part it belongs to (M8): a whole part as one run of its
    /// bytes, chosen by offset; a part that is not whole buffer by buffer with each gap in place, chosen by buffer, in hex
    /// alone, and never saved as one, since nothing may stand in for its missing bytes (I21, P2).
    /// </summary>
    private void TogglePart()
    {
        if (part is not { IsPart: true } found || detail?.Bytes is not { } recordBytes || detail.Entry is not { } entry
            || (found.Complete ? found.Bytes is not { Length: > 0 } : !found.BytesRead))
        {
            return;
        }

        showingPart = !showingPart;
        partToggle.Content = showingPart ? "Show this buffer only" : PartToggleText(found);
        bool byBuffers = showingPart && !found.Complete;
        rangeFrom.Text = byBuffers ? "Buffers from" : "Bytes from";
        showAll.Content = byBuffers ? "All buffers" : "All kept bytes";
        AutomationProperties.SetName(first, byBuffers ? "First buffer to show" : "First byte of the range");
        AutomationProperties.SetName(last, byBuffers ? "Last buffer to show" : "Last byte of the range");
        AutomationProperties.SetName(showRange, byBuffers ? "Show these buffers" : "Show this range of bytes");
        AutomationProperties.SetName(showAll, byBuffers ? "Show every recorded buffer" : "Show all kept bytes");

        // A gap is a line of the hex view, which no byte can pass for; text has no such line, so it is not offered.
        textTab.IsVisible = !byBuffers && ContentBytesView.DeclaresText(entry.Fragment.Encoding);
        if (!textTab.IsVisible) views.SelectedIndex = 0;
        if (byBuffers)
        {
            ShowBuffers(ContentBytesView.RecordedBuffers(found)!.Value);
            return;
        }

        partLines = null;
        shownBytes = showingPart ? found.Bytes : recordBytes;
        kept = showingPart ? new ContentRange(0, found.Bytes!.Length - 1) : ContentBytesView.Kept(entry.Fragment);
        if (kept is { } all) Show(all);
    }

    private async Task CopyAsync()
    {
        if (GappedPart() is { } gapped && partLines is { } lines && Clipboard is { } board)
        {
            await board.SetTextAsync(ContentBytesView.Dump(lines.Lines));
            if (!closed) status.Text = $"Copied the lines shown of {gapped.Name}, its gaps among them, as hex.";
            return;
        }

        if (shownBytes is not { } bytes || kept is not { } all || range is not { } chosen || Clipboard is not { } clipboard) return;
        ContentRange shown = ContentBytesView.Shown(chosen);
        await clipboard.SetTextAsync(ContentBytesView.Dump(ContentBytesView.Slice(bytes, all, shown).Span, shown.First));
        if (!closed) status.Text = "Copied " + shown.Describe(CultureInfo.CurrentCulture) + " as hex.";
    }

    /// <summary>
    /// Saves the chosen bytes as they are, to a file the person chooses (§11.2's explicit binary export). Nothing else is
    /// written, and the file is published whole or not at all.
    /// </summary>
    private async Task SaveAsync()
    {
        if (saving || GappedPart() is not null || shownBytes is not { } bytes || detail?.Entry is not { } entry
            || kept is not { } all || range is not { } chosen) return;
        saving = true;
        try
        {
            RawRecordId record = selected.ObservationId.RawRecordId;
            Avalonia.Platform.Storage.IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Save the chosen bytes",
                SuggestedFileName = string.Create(CultureInfo.InvariantCulture,
                    $"content-{record.StreamId}-{record.SourceEpoch}-{record.RecordOrdinal}{(showingPart ? "-part" : string.Empty)}-bytes-{chosen.First}-{chosen.Last}.bin"),
                DefaultExtension = "bin",
                FileTypeChoices = [new("The bytes as they are") { Patterns = ["*.bin"] }],
            });
            if (file is null || closed) return;
            string destination = file.Path.LocalPath;
            await ExportFileWriter.WriteAsync(destination, ContentBytesView.Slice(bytes, all, chosen), overwrite: true);
            if (!closed)
            {
                status.Text = showingPart
                    ? $"Saved {chosen.Describe(CultureInfo.CurrentCulture)} of {part!.Name} to {destination}."
                    : $"Saved {chosen.Describe(CultureInfo.CurrentCulture)} of {entry.ChunkName} to {destination}.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) status.Text = "Could not save the bytes: " + exception.Message;
        }
        finally
        {
            saving = false;
        }
    }
}
