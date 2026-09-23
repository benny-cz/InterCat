using System.Globalization;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

public sealed class GraphLayoutIntegrationTests
{
    [Fact(DisplayName = "R13: a non-tour workspace labels its own extent and uses its own graph identity")]
    public async Task NonTourWorkspaceUsesItsOwnExtentAndGraphIdentity()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create() with
        {
            Title = "Imported session",
            Extent = new TimeRange(0, 42 * WorkspaceTime.TicksPerSecond),
        };
        using var viewModel = new WorkspaceViewModel(snapshot, "generation-42");
        await viewModel.LayoutReady;

        Assert.Equal(snapshot, viewModel.Snapshot);
        Assert.Equal($"All {42m.ToString("N1", CultureInfo.CurrentCulture)} seconds", viewModel.IntervalLabel);
        GraphLayoutResult expected = GraphLayout.Compute(
            "generation-42", snapshot.Groups, snapshot.Processes, snapshot.Edges,
            previous: snapshot.Processes.ToDictionary(node => node.Id, node => new GraphPoint(node.X, node.Y)));
        foreach (ProcessNode node in snapshot.Processes)
        {
            Assert.Equal(expected.Positions[node.Id], viewModel.GraphPositions[node.Id]);
        }

        viewModel.SelectInterval(new TimeRange(10 * WorkspaceTime.TicksPerSecond, 12 * WorkspaceTime.TicksPerSecond));
        Assert.Equal(
            $"{10m.ToString("N1", CultureInfo.CurrentCulture)}s – {12m.ToString("N1", CultureInfo.CurrentCulture)}s",
            viewModel.IntervalLabel);
    }

    [Fact(DisplayName = "R13: an empty session workspace opens without a fabricated selected process")]
    public async Task EmptyWorkspaceHasNoFabricatedSelection()
    {
        WorkspaceSnapshot empty = SyntheticWorkspace.Create() with
        {
            Title = "Empty session",
            Groups = [], Processes = [], Edges = [], Channels = [], Operations = [], Evidence = [], Timeline = [],
        };
        using var viewModel = new WorkspaceViewModel(empty, "empty-generation");
        await viewModel.LayoutReady;

        Assert.Null(viewModel.SelectedProcess);
        Assert.Equal("Nothing selected", viewModel.SelectionTitle);
        Assert.Empty(viewModel.GraphPositions);
    }

    [Fact(DisplayName = "R12: desktop graph reads stable layout coordinates instead of evidence-model positions")]
    public async Task DesktopUsesSeparateGraphLayout()
    {
        using var viewModel = new WorkspaceViewModel();
        await viewModel.LayoutReady;
        GraphLayoutResult expected = GraphLayout.Compute(
            "synthetic-tour-v1", viewModel.Snapshot.Groups, viewModel.Snapshot.Processes, viewModel.Snapshot.Edges,
            previous: viewModel.Snapshot.Processes.ToDictionary(
                node => node.Id, node => new GraphPoint(node.X, node.Y)));

        Assert.Equal(viewModel.Snapshot.Processes.Count, viewModel.GraphPositions.Count);
        foreach (ProcessNode node in viewModel.Snapshot.Processes)
        {
            Assert.Equal(expected.Positions[node.Id], viewModel.GraphPositions[node.Id]);
        }

        Assert.Contains(viewModel.Snapshot.Processes, node =>
            viewModel.GraphPositions[node.Id] != new GraphPoint(node.X, node.Y));
    }
}
