using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// R5 in the window: an L2 direction row, its selector entry and its table scope name a direction by the one word a
/// channel row and a hover card use, raised as a label.
/// </summary>
public sealed class DirectionWordsTests
{
    [Fact(DisplayName = "R5: a direction row's label is its direction's one word, and a code no version knows is named by its number")]
    public void ADirectionRowsLabelIsItsWord()
    {
        using var workspace = new WorkspaceViewModel();
        Assert.Equal(["All directions", "Outbound", "Inbound", "Bidirectional", "Unknown direction", "No data direction"],
            workspace.DirectionLaneOptions.Select(option => option.Label));

        // A code no version knows is not guessed into "no data direction".
        Assert.Equal("Direction 9", WorkspaceViewModel.DirectionLabel((Direction)9));
    }
}
