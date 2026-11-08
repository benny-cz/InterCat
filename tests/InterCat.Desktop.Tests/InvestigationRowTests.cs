using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>An investigation's member rows as a screen reader says them (R15), and what they say of a member's time.</summary>
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

    [Fact(DisplayName = "§8.2: a session aligned by shared operations says so in the window, with the instant it places, its bound and the drift stated")]
    public void ASharedOperationsAlignmentSaysItsMode()
    {
        Guid reference = Guid.NewGuid(), member = Guid.NewGuid();
        DateTimeOffset now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var workspace = new InvestigationWorkspaceFile
        {
            Contract = InvestigationWorkspace.Contract,
            WorkspaceId = Guid.NewGuid(),
            CreatedUtc = now,
            UpdatedUtc = now,
            Members = [],
            HostAliases = [],
            TimeReference = reference,
            Alignments =
            [
                new WorkspaceAlignment
                {
                    Revision = 1,
                    SessionId = member,
                    Mode = WorkspaceAlignmentMode.SharedOperations,
                    ReferenceSessionId = reference,
                    SessionNanoseconds = 550_150_000,
                    ReferenceNanoseconds = 1_050_100_000,
                    WithinNanoseconds = 250_499,
                    DriftPartsPerMillion = 10,
                    StartsUnderReference = 2,
                    StartsUnderMember = 1,
                    RecordedUtc = now,
                },
            ],
        };
        Assert.Equal("Aligned by the operations each session started under the other's: its 0.55015 s is the reference's 1.0501 s, "
            + "within ±251 µs, drifting at most 10 ppm", InvestigationRows.TimeOf(workspace, member, CultureInfo.InvariantCulture));
    }

    private static InvestigationMemberRow Row(string? reason, bool holds) => new(
        Guid.NewGuid(), "Session 0a1b2c3d, present", "host lab-1 · alpha", reason, "Its time is its own clock", "/sessions/alpha",
        holds ? WorkspaceMemberState.Present : WorkspaceMemberState.Unreadable, holds);
}
