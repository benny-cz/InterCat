using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>
/// R5's one display mapping per dimension: a mechanism and a coverage state each have one set of names, which the window's
/// rows and the command line's tables read alike, so two views never disagree about what one is called.
/// </summary>
public sealed class DisplayTextTests
{
    [Fact(DisplayName = "R5: a mechanism and a coverage state each have one set of names, for a lane, a sentence and a label")]
    public void MechanismsAndStatesHaveOneSetOfNames()
    {
        // A lane names a mechanism by its usual short name; a sentence names a lifecycle as the process's or the thread's,
        // and lowers a capital that is no name or abbreviation.
        Assert.Equal(("TCP", "TCP"), (MechanismText.Name(Mechanism.Tcp), MechanismText.InSentence(Mechanism.Tcp)));
        Assert.Equal(("Process", "process lifecycle"),
            (MechanismText.Name(Mechanism.ProcessLifecycle), MechanismText.InSentence(Mechanism.ProcessLifecycle)));
        Assert.Equal(("Named pipe", "named pipe"), (MechanismText.Name(Mechanism.NamedPipe), MechanismText.InSentence(Mechanism.NamedPipe)));
        Assert.Equal(("Unix socket", "Unix socket"),
            (MechanismText.Name(Mechanism.UnixDomainSocket), MechanismText.InSentence(Mechanism.UnixDomainSocket)));
        Assert.Equal(("Application SDK", "application SDK"),
            (MechanismText.Name(Mechanism.ApplicationSdk), MechanismText.InSentence(Mechanism.ApplicationSdk)));

        // Every other mechanism reads in a sentence as its name does, at most with its first capital lowered, and no name is
        // an enumeration's identifier run together.
        foreach (Mechanism mechanism in Enum.GetValues<Mechanism>()
            .Where(mechanism => mechanism is not (Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle)))
        {
            string name = MechanismText.Name(mechanism);
            string inSentence = MechanismText.InSentence(mechanism);
            Assert.True(inSentence == name || inSentence == char.ToLowerInvariant(name[0]) + name[1..],
                $"{mechanism} is \"{name}\" in a lane and \"{inSentence}\" in a sentence.");
            Assert.DoesNotMatch("^[A-Z][a-z]+[A-Z]", name);
        }

        // A state is its value where something names it as coverage, and stands alone as a label; only unknown says what
        // it is unknown about.
        Assert.Equal(
            ["covered", "reduced fidelity", "partial gap, not extrapolated", "not collected", "unknown"],
            Enum.GetValues<CoverageState>().Select(CoverageStateText.Value));
        Assert.Equal(
            ["covered", "reduced fidelity", "partial gap, not extrapolated", "not collected", "unknown coverage"],
            Enum.GetValues<CoverageState>().Select(CoverageStateText.Label));
    }

    [Fact(DisplayName = "R5: a record's kind and layer, a missing field's reason and a capability's state, tier and overhead each read as words")]
    public void RecordAndCapabilityDimensionsReadAsWords()
    {
        // A kind is a word or two, a layer one; neither is an enumeration's identifier run together.
        Assert.Equal(("send", "request start", "unknown kind"), (ObservationText.Kind(ObservationKind.Send),
            ObservationText.Kind(ObservationKind.RequestStart), ObservationText.Kind(ObservationKind.UnknownKind)));
        Assert.All(Enum.GetValues<ObservationKind>(), kind => Assert.Matches("^[a-z]+( [a-z]+)?$", ObservationText.Kind(kind)));
        Assert.Equal(["transport", "application", "resource", "lifecycle", "collector"],
            Enum.GetValues<ObservationLayer>().Select(ObservationText.Layer));

        // Why a field holds no value: what the source withheld is not what the profile left out, was denied, or was lost.
        Assert.Equal(
            ["present", "not exposed", "not collected by the profile", "denied", "lost with its event", "not decoded: schema unknown",
                "redacted", "not applicable"],
            Enum.GetValues<FieldAvailability>().Select(ObservationText.Absence));

        // A capability's state, tier and overhead, the tiers as §14.2 names them.
        Assert.Equal(["available", "experimental", "unsupported", "permission denied", "disabled by the profile", "schema unknown",
            "provider failed"], Enum.GetValues<CapabilityState>().Select(CapabilityText.State));
        Assert.Equal(["traffic visualization", "topology only", "experimental evidence", "unsupported"],
            Enum.GetValues<CapabilityTier>().Select(CapabilityText.Tier));
        Assert.Equal(["unmeasured", "low", "moderate", "high"], Enum.GetValues<OverheadClass>().Select(CapabilityText.Overhead));

        // A value this version does not know is named by its number, never guessed.
        Assert.Equal(["kind 42", "layer 9", "availability 0", "state 0", "tier 9", "overhead 0"],
        [
            ObservationText.Kind((ObservationKind)42), ObservationText.Layer((ObservationLayer)9),
            ObservationText.Absence((FieldAvailability)0), CapabilityText.State((CapabilityState)0),
            CapabilityText.Tier((CapabilityTier)9), CapabilityText.Overhead((OverheadClass)0),
        ]);
    }
}
