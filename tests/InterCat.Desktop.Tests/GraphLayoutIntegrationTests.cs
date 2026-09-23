using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Desktop.Tests;

public sealed class GraphLayoutIntegrationTests
{
    [Fact(DisplayName = "R12: desktop graph reads stable layout coordinates instead of evidence-model positions")]
    public void DesktopUsesSeparateGraphLayout()
    {
        var viewModel = new WorkspaceViewModel();
        GraphLayoutResult expected = GraphLayout.Compute(
            "synthetic-tour-v1", viewModel.Snapshot.Groups, viewModel.Snapshot.Processes, viewModel.Snapshot.Edges);

        Assert.Equal(viewModel.Snapshot.Processes.Count, viewModel.GraphPositions.Count);
        foreach (ProcessNode node in viewModel.Snapshot.Processes)
        {
            Assert.Equal(expected.Positions[node.Id], viewModel.GraphPositions[node.Id]);
        }

        Assert.Contains(viewModel.Snapshot.Processes, node =>
            viewModel.GraphPositions[node.Id] != new GraphPoint(node.X, node.Y));
    }
}
