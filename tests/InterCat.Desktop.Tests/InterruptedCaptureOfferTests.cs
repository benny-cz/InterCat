using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Capture.Journal;
using InterCat.Desktop.Presentation;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// What the rail's card says about a capture a viewer left unfinished (§3.1 step 6): what the broker kept, what finishing
/// does, what cannot be recovered, and when there is nothing to do but wait or forget.
/// </summary>
public sealed class InterruptedCaptureOfferTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 9, 26, 17, 35, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§3.1: a finishable capture says what the broker kept and that finishing records nothing again")]
    public void AFinishableCaptureSaysWhatFinishingDoes()
    {
        InterruptedCaptureOffer offer = OfferFor(InterruptedFollowState.Finishable, sessionChunks: 1, evidenceChunks: 3)!;

        Assert.Equal("The capture from today at 17:35 is not finished saving", offer.Headline);
        Assert.Contains("2 of its 3 published chunks are not in your session yet", offer.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing is recorded again", offer.Detail, StringComparison.Ordinal);
        Assert.Equal((InterruptedCaptureAction.Finish, "Finish saving", "Forget", false),
            (offer.Action, offer.ActionLabel, offer.ForgetLabel, offer.Rechecks));
    }

    [Fact(DisplayName = "§3.1: a capture still stopping offers nothing yet and is looked at again")]
    public void ACaptureStillStoppingIsLookedAtAgain()
    {
        InterruptedCaptureOffer offer = OfferFor(InterruptedFollowState.StillRecording, sessionChunks: 1, evidenceChunks: 3)!;

        Assert.Equal("The capture from today at 17:35 is still stopping", offer.Headline);
        Assert.Contains("within a minute", offer.Detail, StringComparison.Ordinal);
        Assert.Equal((InterruptedCaptureAction.None, true), (offer.Action, offer.Rechecks));
    }

    [Fact(DisplayName = "§3.1: an unfinalized capture says which records were never kept before it saves the rest")]
    public void AnUnfinalizedCaptureSaysWhatWasLost()
    {
        InterruptedCaptureOffer offer = OfferFor(InterruptedFollowState.EndedUnfinalized, sessionChunks: 0, evidenceChunks: 1)!;

        Assert.Equal("The capture from today at 17:35 ended before it was finalized", offer.Headline);
        Assert.Contains("records after its last publication were not kept", offer.Detail, StringComparison.Ordinal);
        Assert.Contains("and its 1 published chunk is not in your session yet", offer.Detail, StringComparison.Ordinal);
        Assert.Equal((InterruptedCaptureAction.Finish, "Save what was published"), (offer.Action, offer.ActionLabel));
    }

    [Fact(DisplayName = "§3.1: a capture whose evidence is gone opens what was saved, or is only dismissed")]
    public void ACaptureWhoseEvidenceIsGoneOpensWhatWasSaved()
    {
        InterruptedCaptureOffer saved = OfferFor(
            InterruptedFollowState.EvidenceGone, sessionChunks: 1, evidenceChunks: null, problem: "The pointer is torn.")!;
        Assert.Equal("The capture from today at 17:35 can no longer be finished", saved.Headline);
        Assert.Contains("keeps the 1 published chunk followed", saved.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Reason: The pointer is torn.", saved.Detail, StringComparison.Ordinal);
        Assert.Equal((InterruptedCaptureAction.Open, "Open what was saved", "Forget"),
            (saved.Action, saved.ActionLabel, saved.ForgetLabel));

        InterruptedCaptureOffer nothing = OfferFor(InterruptedFollowState.EvidenceGone, sessionChunks: 0, evidenceChunks: null)!;
        Assert.Contains("nothing had been saved", nothing.Detail, StringComparison.Ordinal);
        Assert.Equal((InterruptedCaptureAction.None, "Dismiss"), (nothing.Action, nothing.ForgetLabel));

        Assert.Null(OfferFor(InterruptedFollowState.Complete, sessionChunks: 3, evidenceChunks: 3));
    }

    [Fact(DisplayName = "§3.1: a finish states its progress, and the session it saved states whether it is whole")]
    public void AFinishStatesItsProgressAndResult()
    {
        InterruptedFollow follow = Follow(InterruptedFollowState.Finishable, sessionChunks: 1, evidenceChunks: 40);
        Assert.Equal(
            ("Finishing the capture from today at 17:35", "12 of 40 published chunks derived, 3,210 records so far. "
                + "Stopping keeps what is derived."),
            InterruptedCaptureOffer.Finishing(follow, Step(12, 40, 3_210, finished: false), Now, Zone, Culture));
        Assert.StartsWith(
            "Reading the evidence",
            InterruptedCaptureOffer.Finishing(follow, step: null, Now, Zone, Culture).Detail,
            StringComparison.Ordinal);

        using var session = new TemporarySession();
        (string headline, string detail) = InterruptedCaptureOffer.Finished(
            new(Step(40, 40, 9_000, finished: true), session.Store, Completed: true), Culture);
        Assert.Equal("Session saved", headline);
        Assert.Contains("40 published chunks, 9,000 records", detail, StringComparison.Ordinal);
        Assert.Equal(
            "Partial session saved",
            InterruptedCaptureOffer.Finished(new(Step(1, 1, 1, finished: false), session.Store, Completed: true), Culture)
                .Headline);
    }

    [Theory(DisplayName = "§3.1: a capture is named by when it started, in the viewer's own day")]
    [InlineData(2026, 9, 26, 17, 35, "today at 17:35")]
    [InlineData(2026, 9, 25, 9, 2, "yesterday at 09:02")]
    [InlineData(2026, 9, 21, 23, 59, "21 Sep at 23:59")]
    public void ACaptureIsNamedByWhenItStarted(int year, int month, int day, int hour, int minute, string expected) =>
        Assert.Equal(
            expected,
            InterruptedCaptureOffer.When(new(year, month, day, hour, minute, 0, TimeSpan.Zero), Now, Zone, Culture));

    private static InterruptedCaptureOffer? OfferFor(
        InterruptedFollowState state, int sessionChunks, int? evidenceChunks, string? problem = null) =>
        InterruptedCaptureOffer.For(Follow(state, sessionChunks, evidenceChunks, problem), Now, Zone, Culture);

    private static InterruptedFollow Follow(
        InterruptedFollowState state, int sessionChunks, int? evidenceChunks, string? problem = null) =>
        new(
            LiveFollowTicket.For(Guid.NewGuid(), @"C:\Evidence\capture-1", @"C:\Sessions\explore-1", Started, Started.AddSeconds(30)),
            state,
            sessionChunks,
            evidenceChunks,
            problem);

    private static FollowStep Step(int derived, int evidence, long records, bool finished) => new()
    {
        MirroredChunks = 0,
        MirroredRecords = 0,
        DerivedChunks = derived,
        EvidenceChunks = evidence,
        DerivedRecords = records,
        Finished = finished,
        DerivedGeneration = 1,
        Compactions = 0,
    };
}
