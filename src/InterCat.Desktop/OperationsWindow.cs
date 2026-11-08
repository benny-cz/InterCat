using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// An operation as the operations window lists it: indented by its depth, in `icat messages --operations`'s words, the first
/// of a parent never recorded, or of the operations no root reaches, carrying that group's heading.
/// </summary>
internal sealed record OperationRow(CooperativeOperationNode Node, string Line, string? Heading) : IAccessibleRow
{
    public CooperativeOperation Operation => Node.Operation;

    /// <summary>How far the row is indented: its depth, and one more beneath a group's heading.</summary>
    public int Indent { get; init; }

    public string AccessibleName => (Heading is null ? string.Empty : Heading + ". ")
        + string.Create(CultureInfo.CurrentCulture, $"Level {Indent + 1}: {Line}.");

    public override string ToString() => Line;
}

/// <summary>The operation chosen in the operations window, and the processes it and the operations under it are the application's in.</summary>
internal sealed record OperationChoice(CooperativeOperation Operation, IReadOnlyList<ProcessInstanceId> Processes);

/// <summary>
/// The operations applications instrumented with InterCat's SDK said they started and completed
/// (`contracts/cooperative-operations-v1.md`, ADR-109), in `icat messages --operations`'s words: each under the operation it
/// was started under - another process's, or one the capture never recorded - with what became of it, and the chosen one's
/// identity, parent and times. Choosing an operation closes the window with the processes it and the operations under it are
/// the application's in, which the workspace selects together; the window itself changes nothing. What an application says
/// is its own word, which the window keeps saying.
/// </summary>
internal sealed class OperationsWindow : Window, IDisposable
{
    /// <summary>How many operations the window lists, in the tree's order.</summary>
    public const int Listed = 2_000;

    /// <summary>Why the window's actions wait while the session is read.</summary>
    private const string Reading = "The session's operations are being read.";

    /// <summary>Why the window's actions are not offered when no reading could be shown.</summary>
    private const string NoReading = "No operations are shown; the status line says why.";

    private readonly string path;
    private readonly Guid expectedSessionId;
    private readonly long expectedGeneration;
    private readonly EvidencePolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock coverage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly ListBox operations = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock identity = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock outcome = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock times = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock under = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock apart = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button choose = new() { Content = "Select its processes" };
    private readonly Button close = new() { Content = "Close" };
    private CooperativeOperationReading? reading;
    private bool loading;
    private bool closed;
    private bool disposed;

    /// <summary>Why the window's actions are not offered when no reading could be shown; null while one is.</summary>
    private string? unshown;

    /// <param name="policy">The evidence policy InterCat's window counts under, so the two name processes alike.</param>
    public OperationsWindow(string path, Guid expectedSessionId, long expectedGeneration, EvidencePolicy policy)
    {
        this.path = path;
        this.expectedSessionId = expectedSessionId;
        this.expectedGeneration = expectedGeneration;
        this.policy = policy;
        Title = "InterCat · Operations";
        Width = 1_000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scope = new TextBlock
        {
            Text = "The operations applications instrumented with InterCat's SDK said they started, each under the one it was "
                + "started under · all session times",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        };
        status.Text = "Reading the session's operations…";
        AutomationProperties.SetName(status, "Operations status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(coverage, "What the capture covered");
        var note = new TextBlock { Text = CooperativeOperationText.Note, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        AutomationProperties.SetName(apart, "What no operation holds");

        // Each operation indented by its depth, a group's heading above its first, each wrapping within the list rather than
        // running past its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(operations, ScrollBarVisibility.Disabled);
        operations.ItemTemplate = new FuncDataTemplate<OperationRow>((row, _) =>
        {
            var lines = new StackPanel { Margin = new Thickness(2, 2), Spacing = 1 };
            if (row?.Heading is { } group)
            {
                lines.Children.Add(new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            }

            lines.Children.Add(new TextBlock
            {
                Text = row?.Line,
                Margin = new Thickness(16 * (row?.Indent ?? 0), 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return lines;
        });
        AccessibleItems.Name(operations);
        AutomationProperties.SetName(operations, "Operations, each under the one it was started under. Enter selects the processes "
            + "the chosen operation and those under it are the application's in.");
        operations.SelectionChanged += (_, _) => ShowSelected();
        operations.DoubleTapped += (_, _) => ChooseSelected();
        operations.KeyDown += (_, key) =>
        {
            if (key.Key != Key.Enter) return;
            ChooseSelected();
            key.Handled = true;
        };

        AutomationProperties.SetName(outcome, "What became of the chosen operation");
        AutomationProperties.SetName(times, "When the chosen operation started and completed");
        AutomationProperties.SetName(under, "What was started under the chosen operation");
        choose.Click += (_, _) => ChooseSelected();
        close.Click += (_, _) => Close();

        // Escape closes the window, as it cancels InterCat's prompts and every Windows dialog.
        KeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            Close();
            key.Handled = true;
        };

        var details = new StackPanel { Spacing = 6, Children = { heading, identity, outcome, times, under } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("6*,5*"), ColumnSpacing = 14 };
        Grid.SetColumn(details, 1);
        body.Children.Add(operations);
        body.Children.Add(details);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { choose, close },
        };
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
        };
        var header = new StackPanel { Spacing = 5, Children = { scope, status, coverage } };
        var beneath = new StackPanel { Spacing = 4, Children = { note, apart } };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(beneath, 2);
        Grid.SetRow(actions, 3);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(beneath);
        grid.Children.Add(actions);
        Content = grid;

        // Until the session is read, its actions wait for it, saying so.
        loading = true;
        ShowSelected();
        Opened += (_, _) => ReadAsync().Watched();
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

    private async Task ReadAsync()
    {
        try
        {
            CancellationToken token = lifetime.Token;

            // A folder that now holds another session is refused as every read of the workspace's is.
            CooperativeOperationReading read = await Task.Run(() => CooperativeOperationText.Read(
                SharedSessionStores.Open(path, expectedSessionId), policy, token), token);
            if (closed) return;
            if (read.Generation < expectedGeneration)
            {
                unshown = NoReading;
                status.Text = "This folder no longer holds the generation InterCat's window shows. Close this list and open "
                    + "it again from that window.";
                return;
            }

            reading = read;
            int count = read.Index.Operations.Count;
            status.Text = string.Create(CultureInfo.CurrentCulture, $"Generation {read.Generation:N0}")
                + (read.Generation == expectedGeneration ? string.Empty
                    : string.Create(CultureInfo.CurrentCulture, $" (InterCat's window shows generation {expectedGeneration:N0}: ")
                        + "a process only the newer one holds is not selected there)")
                + " · " + CountText.Of(count, "operation")
                + string.Create(CultureInfo.CurrentCulture, $" · {read.Count(CooperativeOperationState.Completed):N0} completed")
                + string.Create(CultureInfo.CurrentCulture, $" ({read.Failed:N0} failed), {read.Count(CooperativeOperationState.Open):N0} open")
                + (count > Listed ? string.Create(CultureInfo.CurrentCulture, $" · the first {Listed:N0} listed; icat messages --operations --json lists every one") : string.Empty)
                + (read.Policy == EvidencePolicy.IncludeCorrelated ? string.Empty
                    : $" · processes named under the {CommandArgument.Typed(read.Policy)} evidence policy, as InterCat's window names them");
            coverage.Text = CoverageText.Describe(read.Coverage);
            apart.Text = read.Index.WithoutOperation == 0 ? string.Empty
                : "In no operation: " + CountText.Of(read.Index.WithoutOperation, "record") + " of the SDK's named no operation, "
                    + "its identity or process not kept.";
        }
        catch (OperationCanceledException) when (closed)
        {
            // Closing cancels an in-flight read without altering the session.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed)
            {
                unshown = NoReading;
                status.Text = "Could not read the session's operations: " + exception.Message;
            }
        }
        finally
        {
            loading = false;
            if (!closed)
            {
                List();
                KeepKeyboard();
            }
        }
    }

    /// <summary>Lists the reading's operations in the tree's order, each group's first carrying its heading, choosing the first.</summary>
    private void List()
    {
        var rows = new List<OperationRow>();
        if (reading is { } read)
        {
            bool grouped = false;
            bool circular = false;
            Guid? unrecorded = null;
            foreach (CooperativeOperationNode node in read.Index.Tree.Take(Listed))
            {
                string? group = null;
                if (node.Depth == 0)
                {
                    if (node.Place == CooperativeOperationPlace.UnderUnrecorded && node.Operation.Parent != unrecorded)
                    {
                        unrecorded = node.Operation.Parent;
                        group = CooperativeOperationText.Unrecorded(unrecorded!.Value);
                    }
                    else if (node.Place == CooperativeOperationPlace.Circular && !circular)
                    {
                        circular = true;
                        group = CooperativeOperationText.Circular;
                    }

                    grouped = node.Place is CooperativeOperationPlace.UnderUnrecorded or CooperativeOperationPlace.Circular;
                }

                rows.Add(new(node, CooperativeOperationText.Line(read, node.Operation), group) { Indent = node.Depth + (grouped ? 1 : 0) });
            }
        }

        operations.ItemsSource = rows;
        operations.SelectedIndex = rows.Count > 0 ? 0 : -1;
        ShowSelected();
    }

    /// <summary>
    /// Says what the chosen operation is, what became of it, when it started and completed and what was started under it, or,
    /// with none chosen, why the list holds none; and offers the window's actions, or says why not.
    /// </summary>
    private void ShowSelected()
    {
        OperationRow? row = operations.SelectedItem as OperationRow;
        string? none = loading ? Reading : unshown
            ?? (reading?.Index.Operations.Count == 0 ? "This session holds no operation of the SDK's." : null);
        ProcessInstanceId[] processes = row is null || reading is null ? [] : Processes(reading, row.Operation);
        choose.Content = processes.Length == 1 ? "Go to its process" : "Select its processes";
        Offering.Offer(choose, none
            ?? (row is null ? "Choose an operation first."
                : processes.Length == 0 ? "No process the evidence policy admits started or completed this operation or one under it."
                : null),
            processes.Length == 1
                ? "Close this list and select the process the operation and those under it are the application's in, in InterCat's window."
                : "Close this list and select the processes the operation and those under it are the application's in together in "
                    + "InterCat's window, as Ctrl+click does; Enter there lists their records.");

        identity.IsVisible = outcome.IsVisible = times.IsVisible = under.IsVisible = row is not null;
        apart.IsVisible = !string.IsNullOrEmpty(apart.Text);
        if (row is null || reading is not { } read)
        {
            heading.Text = reading is not { } shownReading ? string.Empty
                : shownReading.Index.Operations.Count == 0
                    ? "This session holds no operation of the SDK's: no application said one through it while the capture ran, or "
                        + "the capture did not record the SDK; the coverage above says which."
                    : string.Empty;
            return;
        }

        CooperativeOperation operation = row.Operation;
        heading.Text = CooperativeOperationText.Name(operation);
        identity.Text = "Operation " + operation.Operation + (operation.Parent is { } parent
            ? " · started under operation " + parent + (read.Index.Find(parent) is { } recorded
                ? ", " + CooperativeOperationText.Name(recorded)
                : ", which this capture did not record")
            : operation.Start is null ? " · its start not recorded, so its parent is not known" : " · started under no operation");
        outcome.Text = $"{CooperativeOperationText.Process(read, operation)} · {CooperativeOperationText.Outcome(operation)}";
        times.Text = (operation.Start is { } start ? "Started at " + When(start) : "Its start not recorded")
            + (operation.Completion is { } completion ? " · completed at " + When(completion) : " · no completion recorded");
        int children = read.Index.ChildrenOf(operation.Operation).Count;
        under.Text = children == 0 ? "No operation was started under it."
            : CountText.Of(children, "operation") + " started under it, listed beneath it.";
    }

    /// <summary>A record's instant in its session time, as the window says a time: the clock its operation's duration is read on.</summary>
    private static string When(CooperativeOperationRecord record) =>
        record.SessionNanoseconds is { } at ? SessionTimeText.Seconds(at, CultureInfo.CurrentCulture) : "a time its record did not keep";

    /// <summary>
    /// The processes the evidence policy admits that an operation and every operation under it are the application's in,
    /// each once: their starts' and completions'.
    /// </summary>
    private static ProcessInstanceId[] Processes(CooperativeOperationReading reading, CooperativeOperation top)
    {
        var found = new List<ProcessInstanceId>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<CooperativeOperation>();
        pending.Push(top);
        while (pending.TryPop(out CooperativeOperation? operation))
        {
            if (!seen.Add(operation.Operation)) continue;
            foreach (CooperativeOperationRecord record in new[] { operation.Start, operation.Completion }.OfType<CooperativeOperationRecord>())
            {
                if (record.Process.IsAdmittedUnder(reading.Policy))
                {
                    found.Add(reading.Index.Processes.Instances[record.Process.Instance].Id);
                }
            }

            foreach (CooperativeOperation child in reading.Index.ChildrenOf(operation.Operation))
            {
                pending.Push(child);
            }
        }

        return [.. found.Distinct()];
    }

    /// <summary>
    /// Gives the chosen operation's row the keyboard once the session is read - or Close, with none to choose - unless a
    /// control of the window has it. Posted, once the rows are laid out.
    /// </summary>
    private void KeepKeyboard() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (closed || FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused)) return;
        (operations.ContainerFromIndex(operations.SelectedIndex) ?? close).Focus(NavigationMethod.Tab);
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    /// <summary>The operation the user chose, whose processes the workspace selects.</summary>
    private void ChooseSelected()
    {
        if (!choose.IsEnabled || operations.SelectedItem is not OperationRow row || reading is null) return;
        Close(new OperationChoice(row.Operation, Processes(reading, row.Operation)));
    }
}
