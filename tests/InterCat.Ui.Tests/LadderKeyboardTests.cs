using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(InterCat.Ui.Tests.TestApplication))]

namespace InterCat.Ui.Tests;

/// <summary>
/// The headless lane the M0 interaction review asked for. The earlier review could implement the
/// keyboard path but not confirm it, because synthetic key delivery could not be driven from an
/// automation script. Here the keys are delivered to a real window and the result is asserted, so
/// "implemented and unverified" becomes "verified" (R15, section 6.7).
/// </summary>
public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class LadderKeyboardTests
{
    [AvaloniaFact(DisplayName = "R15: the table toggle is reachable by keyboard while a list has focus")]
    public void TableToggleRespondsToItsKey()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        Assert.False(viewModel.ShowTables);

        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);

        Assert.True(viewModel.ShowTables);

        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);

        Assert.False(viewModel.ShowTables);
    }

    [AvaloniaFact(DisplayName = "R13: Enter descends a rung and Escape returns to exactly where it was")]
    public void EnterDescendsAndEscapeRestores()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        string machineBadge = viewModel.LevelBadge;
        string machineSummary = viewModel.LevelSummary;
        viewModel.SelectedRung = viewModel.RungRows[0];

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        Assert.True(viewModel.CanAscend);
        Assert.Equal(2, viewModel.Crumbs.Count);
        Assert.True(viewModel.Crumbs[^1].IsCurrent);
        Assert.NotEmpty(viewModel.RungRows);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(machineBadge, viewModel.LevelBadge);
        Assert.Equal(machineSummary, viewModel.LevelSummary);
        Assert.False(viewModel.CanAscend);
        Assert.Single(viewModel.Crumbs);
    }

    [AvaloniaFact(DisplayName = "R13: Alt and Left ascend, the second gesture the ladder promises")]
    public void AltLeftAscends()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(viewModel.CanAscend);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);

        Assert.False(viewModel.CanAscend);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "R13: evidence is one key away from the rung the user is on")]
    public void EvidenceIsOneKeyAway()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);

        Assert.Equal("L5 · EVIDENCE", viewModel.LevelBadge);
        Assert.NotEmpty(viewModel.RungRows);
        Assert.Contains(viewModel.Filters, filter => filter.Field == "scope");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "R13: a filter a descent implied can be removed without changing the level")]
    public void DeleteRemovesTheSelectedFilter()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Single(viewModel.Filters);
        string level = viewModel.LevelBadge;
        viewModel.SelectedFilter = viewModel.Filters[0];

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(viewModel.Filters);
        Assert.False(viewModel.HasFilters);
        Assert.Equal(level, viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "R13: descending to the deepest rung never leaves an unexplained empty pane")]
    public void EveryRungEitherHasRowsOrSaysWhyNot()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);

        for (int depth = 0; depth < 5; depth++)
        {
            if (viewModel.IsEmptyRung)
            {
                Assert.False(string.IsNullOrWhiteSpace(viewModel.EmptyReason));
                return;
            }

            Assert.NotEmpty(viewModel.RungRows);
            viewModel.SelectedRung = viewModel.RungRows[0];
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        }

        Assert.Equal("L5 · EVIDENCE", viewModel.LevelBadge);
        Assert.NotEmpty(viewModel.RungRows);
    }

    [AvaloniaFact(DisplayName = "R15: the breadcrumb returns to a named rung and drops the rungs below it")]
    public void BreadcrumbReturnsToANamedRung()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(3, viewModel.Crumbs.Count);

        CrumbRow machine = viewModel.Crumbs[0];
        viewModel.SelectedCrumb = machine;

        Assert.Single(viewModel.Crumbs);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
    }

    private static (Window Window, WorkspaceViewModel ViewModel) Open()
    {
        var window = new MainWindow();
        window.Show();
        return (window, (WorkspaceViewModel)window.DataContext!);
    }

    /// <summary>
    /// Focus the ranked list first. A focused list is exactly the situation the review found broken, so
    /// the test would pass for the wrong reason if it left focus on the window.
    /// </summary>
    private static void FocusRankedTable(Window window)
    {
        ListBox list = window.GetControl<ListBox>("RungList");
        list.Focus();
    }
}
