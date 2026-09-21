using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class BaselineContractTests
{
    [Fact(DisplayName = "R21: a budget nothing measured reports as unmeasured, never as a pass")]
    public void AnUnmeasuredBudgetIsNotAPass()
    {
        PerformanceBudget budget = PerformanceBudgets.Find(PerformanceBudgets.CallbackAdmission);

        BudgetResult result = PerformanceBudgets.Evaluate(budget, null, "nothing produces this yet");

        Assert.Equal(BudgetOutcome.Unmeasured, result.Outcome);
        Assert.Null(result.Measured);
        Assert.Contains("unmeasured", result.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "R21: a report covers the whole budget table, so an absent budget cannot read as met")]
    public void EveryBudgetAppearsInAReport()
    {
        BudgetResult measured = PerformanceBudgets.Evaluate(
            PerformanceBudgets.Find(PerformanceBudgets.CallbackAdmission),
            4_096,
            "one level measured it");

        IReadOnlyList<BudgetResult> complete = PerformanceBudgets.Complete([measured], "nothing measured it");

        Assert.Equal(PerformanceBudgets.All.Count, complete.Count);
        Assert.Equal(
            PerformanceBudgets.All.Select(budget => budget.Id),
            complete.Select(result => result.Budget.Id));
        Assert.Single(complete, result => result.Outcome == BudgetOutcome.Met);
        Assert.Equal(
            PerformanceBudgets.All.Count - 1,
            complete.Count(result => result.Outcome == BudgetOutcome.Unmeasured));
    }

    [Fact(DisplayName = "R3: a budget is met or missed against its own direction, not against a bare number")]
    public void BudgetDirectionDecidesTheOutcome()
    {
        PerformanceBudget latency = PerformanceBudgets.Find(PerformanceBudgets.CallbackAdmission);
        PerformanceBudget throughput = PerformanceBudgets.Find(PerformanceBudgets.SustainedIngest);

        Assert.True(latency.LowerIsBetter);
        Assert.False(throughput.LowerIsBetter);
        Assert.Equal(
            BudgetOutcome.Met,
            PerformanceBudgets.Evaluate(latency, latency.Target, "exactly at the target").Outcome);
        Assert.Equal(
            BudgetOutcome.Missed,
            PerformanceBudgets.Evaluate(latency, latency.Target + 1, "just past it").Outcome);
        Assert.Equal(
            BudgetOutcome.Met,
            PerformanceBudgets.Evaluate(throughput, throughput.Target, "exactly at the target").Outcome);
        Assert.Equal(
            BudgetOutcome.Missed,
            PerformanceBudgets.Evaluate(throughput, throughput.Target - 1, "just short").Outcome);
    }

    [Fact(DisplayName = "R21: overhead stays unmeasured until something measures it")]
    public void OverheadIsUnmeasuredUntilMeasured()
    {
        Assert.Equal(OverheadClass.Unmeasured, OverheadClassCalculator.Classify(null));
        Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(0));
        Assert.Equal(OverheadClass.Low, OverheadClassCalculator.Classify(1));
        Assert.Equal(OverheadClass.Moderate, OverheadClassCalculator.Classify(1.01));
        Assert.Equal(OverheadClass.Moderate, OverheadClassCalculator.Classify(5));
        Assert.Equal(OverheadClass.High, OverheadClassCalculator.Classify(5.01));
    }

    [Fact(DisplayName = "R3: a capture impact with no elapsed time is unmeasured rather than infinite")]
    public void ImpactRefusesAZeroInterval()
    {
        Assert.Null(OverheadClassCalculator.ImpactPercentagePoints(1, 0, 8));
        Assert.Equal(
            12.5,
            OverheadClassCalculator.ImpactPercentagePoints(1, 1, 8)!.Value,
            3);
    }

    [Fact(DisplayName = "R3: paired impact preserves observed noise but never classifies a negative cost")]
    public void PairedImpactClampsOnlyTheClassifiedCost()
    {
        Assert.Equal(3.5, OverheadClassCalculator.PairedImpactPercentagePoints(20, 23.5));
        Assert.Equal(0, OverheadClassCalculator.PairedImpactPercentagePoints(23.5, 20));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverheadClassCalculator.PairedImpactPercentagePoints(-1, 2));
    }

    [Fact(DisplayName = "R3: paired throughput regression refuses no baseline and clamps only improvement")]
    public void PairedThroughputNeedsABaseline()
    {
        Assert.Null(OverheadClassCalculator.ThroughputRegressionPercent(0, 10));
        Assert.Equal(5, OverheadClassCalculator.ThroughputRegressionPercent(1_000, 950));
        Assert.Equal(0, OverheadClassCalculator.ThroughputRegressionPercent(1_000, 1_100));
    }

    [Fact(DisplayName = "R21: a machine that falls short of the reference names every way it does")]
    public void ReferenceGapsAreNamed()
    {
        var small = new MachineDescriptor
        {
            OperatingSystem = "test",
            Architecture = "X64",
            LogicalProcessors = 4,
            TotalPhysicalMemoryBytes = 8L * 1024 * 1024 * 1024,
            Storage = new()
            {
                Volume = "T:\\",
                FileSystem = "NTFS",
                TotalBytes = 1,
                AvailableBytes = 1,
                ProbeBytes = 0,
                SequentialWriteBytesPerSecond = null,
                SequentialReadBytesPerSecond = null,
                UnavailableReason = "the probe did not run",
            },
        };

        Assert.False(small.MeetsReferenceMachine);
        Assert.Equal(3, small.ReferenceGaps.Count);
        Assert.Contains(small.ReferenceGaps, gap => gap.Contains("logical processors", StringComparison.Ordinal));
        Assert.Contains(small.ReferenceGaps, gap => gap.Contains("memory", StringComparison.Ordinal));
        Assert.Contains(small.ReferenceGaps, gap => gap.Contains("not measured", StringComparison.Ordinal));
        Assert.Null(small.Storage.MeetsReferenceDevice);
    }

    [Fact(DisplayName = "R21: a machine that meets the reference has no gaps to name")]
    public void AReferenceMachineHasNoGaps()
    {
        var reference = new MachineDescriptor
        {
            OperatingSystem = "test",
            Architecture = "X64",
            LogicalProcessors = ReferenceMachine.LogicalProcessors,
            TotalPhysicalMemoryBytes = ReferenceMachine.MemoryBytes,
            Storage = new()
            {
                Volume = "T:\\",
                FileSystem = "NTFS",
                TotalBytes = 1,
                AvailableBytes = 1,
                ProbeBytes = 1,
                SequentialWriteBytesPerSecond = ReferenceMachine.WriteBytesPerSecond,
                SequentialReadBytesPerSecond = ReferenceMachine.ReadBytesPerSecond,
                UnavailableReason = null,
            },
        };

        Assert.True(reference.MeetsReferenceMachine);
        Assert.Empty(reference.ReferenceGaps);
        Assert.True(reference.Storage.MeetsReferenceDevice);
    }

    [Fact(DisplayName = "R9: the callback allocation budget is measurable, so it can be met")]
    public void TheAllocationBudgetIsMeasurable()
    {
        PerformanceBudget budget = PerformanceBudgets.Find(PerformanceBudgets.CallbackAllocation);

        // A budget stated as exactly zero cannot be met by any measurement, because every measurement
        // carries a fixed setup cost. ADR-009 states the bound at the slope's resolution instead.
        Assert.True(budget.Target > 0, "a budget that cannot be met is not a budget");
        Assert.True(budget.Target <= 1, "the bound must stay far below anything that matters at rate");
        Assert.Equal(
            BudgetOutcome.Met,
            PerformanceBudgets.Evaluate(budget, 0.0009, "the measured slope").Outcome);
        Assert.Equal(
            BudgetOutcome.Missed,
            PerformanceBudgets.Evaluate(budget, 1_400, "a thread total that includes the adapter").Outcome);
    }

    [Fact(DisplayName = "P27: a build outside the matrix is untested, never probably working")]
    public void AnUnlistedBuildIsUntested()
    {
        BuildSupport support = SupportedBuilds.Resolve(19045, "X64");

        Assert.False(support.IsSupported);
        Assert.Equal(BuildSupportTier.Untested, support.Tier);
        Assert.Null(support.ReleaseName);
        Assert.Contains("not in the section 1.3 matrix", support.Describe(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P27: a supported build states its release and whether it is a pre-release branch")]
    public void ASupportedBuildStatesItsBranch()
    {
        BuildSupport prerelease = SupportedBuilds.Resolve(26220, "X64");
        BuildSupport retail = SupportedBuilds.Resolve(26200, "X64");

        Assert.True(prerelease.IsSupported);
        Assert.True(prerelease.IsPrerelease);
        Assert.Equal("Windows 11 25H2 x64", prerelease.ReleaseName);
        Assert.Contains("pre-release", prerelease.Describe(), StringComparison.Ordinal);

        Assert.True(retail.IsSupported);
        Assert.False(retail.IsPrerelease);
        Assert.DoesNotContain("pre-release", retail.Describe(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P27: a release is listed by exact build numbers, so a near build is not accepted")]
    public void ANearBuildIsNotAccepted()
    {
        Assert.False(SupportedBuilds.IsSupported(26221, "X64"));
        Assert.False(SupportedBuilds.IsSupported(26201, "X64"));
        Assert.False(SupportedBuilds.IsSupported(26220, "Arm64"));
    }
}
