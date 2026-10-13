using InterCat.Application;
using InterCat.Desktop.Presentation;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>An investigation's member rows as a screen reader says them (R15).</summary>
public sealed class InvestigationRowTests
{
    [Fact(DisplayName = "R15: a member's row says what to do with it once: open it, or relink it, unless why it is not there says so")]
    public void AMemberRowSaysWhatToDoOnce()
    {
        // A member at its path opens; one whose reason gives no step, as a damaged session's does, is told to relink.
        Assert.EndsWith("its own clock. Press Enter to open it.", Row(null, holds: true).AccessibleName, StringComparison.Ordinal);
        Assert.EndsWith("its own clock. A segment is damaged. Relink it to open it.",
            Row("A segment is damaged.", holds: false).AccessibleName, StringComparison.Ordinal);

        // One whose reason already says to relink it is not told so again.
        string moved = "Nothing is at this path: the session was moved or removed. " + InvestigationWorkspace.RelinkWhereItIs;
        Assert.EndsWith("its own clock. " + moved, Row(moved, holds: false).AccessibleName, StringComparison.Ordinal);
    }

    private static InvestigationMemberRow Row(string? reason, bool holds) => new(
        Guid.NewGuid(), "Session 0a1b2c3d, present", "host lab-1 · alpha", reason, "Its time is its own clock", "/sessions/alpha",
        holds ? WorkspaceMemberState.Present : WorkspaceMemberState.Unreadable, holds);
}
