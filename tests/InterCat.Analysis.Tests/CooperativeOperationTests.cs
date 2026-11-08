using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// The cooperative SDK's operations (ADR-109): an operation is its identity, started once and completed once in the process
/// holding it, placed under the operation its start names - another process's where the application carried its identity
/// there; a record names an operation only by the identity it carries, and only the SDK's own provider's records are read.
/// </summary>
public sealed class CooperativeOperationTests
{
    private static readonly Guid Request = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");
    private static readonly Guid Serving = Guid.Parse("1a2b3c4d-5e6f-4a0b-9c1d-2e3f4a5b6c7d");
    private static readonly Guid Answer = Guid.Parse("2b3c4d5e-6f7a-4b1c-8d2e-3f4a5b6c7d8e");
    private static readonly Guid Again = Guid.Parse("3c4d5e6f-7a8b-4c2d-9e3f-4a5b6c7d8e9f");
    private static readonly Guid ServingAgain = Guid.Parse("4d5e6f7a-8b9c-4d3e-8f4a-5b6c7d8e9fa0");
    private static readonly Guid Notice = Guid.Parse("5e6f7a8b-9cad-4e4f-9a5b-6c7d8e9fa0b1");

    [Fact(DisplayName = "§15: an operation is placed under the operation its start names, another process's where the application carried its identity there: the tree walks each root and everything under it in the order first recorded, each completed one with its status and how long it ran")]
    public void OperationsFormATreeAcrossProcesses()
    {
        var sdk = new Sdk();
        sdk.Start(10, 100, Request, null, "request-1", sourceTicks: 9_010);
        sdk.Start(20, 200, Serving, Request, "serve-1");
        sdk.Start(25, 100, Notice, Request, "notice-1");
        sdk.Start(30, 100, Answer, Serving, "answer-1");
        sdk.Complete(35, 100, Answer, 0);
        sdk.Complete(40, 200, Serving, 0, sourceTicks: 9_040);
        sdk.Complete(45, 100, Notice, 0);
        sdk.Complete(50, 100, Request, 0);

        // An SDK says a start under no parent with the empty identity, as ETW's own link says none.
        sdk.Start(60, 100, Again, Guid.Empty, "request-2");
        sdk.Start(65, 200, ServingAgain, Again, "serve-2");
        sdk.Complete(70, 100, Again, 5);
        using var session = new TemporarySession();
        CooperativeOperationIndex index = sdk.Derive(session.Store);

        // Each operation in the order first recorded, its tree walked root by root, everything under each child before the
        // next child.
        Assert.Equal([Request, Serving, Notice, Answer, Again, ServingAgain], index.Operations.Select(operation => operation.Operation));
        Assert.Equal(
            [
                (Request, 0, CooperativeOperationPlace.Root), (Serving, 1, CooperativeOperationPlace.Child),
                (Answer, 2, CooperativeOperationPlace.Child), (Notice, 1, CooperativeOperationPlace.Child),
                (Again, 0, CooperativeOperationPlace.Root), (ServingAgain, 1, CooperativeOperationPlace.Child),
            ],
            index.Tree.Select(node => (node.Operation.Operation, node.Depth, node.Place)));
        Assert.Equal([Serving, Notice], index.ChildrenOf(Request).Select(operation => operation.Operation));
        Assert.Empty(index.ChildrenOf(Answer));
        Assert.Empty(index.UnrecordedParents);

        // A completed operation has its status, and how long it ran from its start's record to its completion's; a failure
        // is the application's own, and one never completed is open, running as far as anything recorded says.
        CooperativeOperation request = index.Find(Request)!;
        Assert.Equal((CooperativeOperationState.Completed, "request-1", (Guid?)null, (long?)0, (long?)400_000),
            (request.State, request.Name, request.Parent, request.Status, request.DurationNanoseconds));
        Assert.Equal(((long?)9_010, (long?)100_000, 100), (request.Start!.SourceTicks, request.Start.SessionNanoseconds, request.Start.ProcessId));
        CooperativeOperation serving = index.Find(Serving)!;
        Assert.Equal((CooperativeOperationState.Completed, (Guid?)Request, 200, (long?)9_040, (long?)200_000),
            (serving.State, serving.Parent, serving.Start!.ProcessId, serving.Completion!.SourceTicks, serving.DurationNanoseconds));
        Assert.Equal(((long?)5, (long?)100_000, (Guid?)null), (index.Find(Again)!.Status, index.Find(Again)!.DurationNanoseconds, index.Find(Again)!.Parent));
        Assert.Equal((CooperativeOperationState.Open, (long?)null, (long?)null),
            (index.Find(ServingAgain)!.State, index.Find(ServingAgain)!.Status, index.Find(ServingAgain)!.DurationNanoseconds));

        // Each record's process binds to the instance its lifecycle records began.
        Assert.True(request.Start.Process.IsBound);
        Assert.True(serving.Start.Process.IsBound);
        Assert.NotEqual(request.Start.Process.Instance, serving.Start.Process.Instance);
        Assert.Equal(0, index.WithoutOperation);
        Assert.Null(index.Find(Guid.NewGuid()));
    }

    [Fact(DisplayName = "§15: an operation completed with no start recorded is a root whose start went unseen, one started or completed twice or in two processes conflicts, one under a parent never recorded is listed under it, parents naming each other round are reached by no root, and a record naming no operation is counted apart")]
    public void OperationsSayWhatTheirRecordsDoNot()
    {
        Guid unseen = Guid.NewGuid(), underUnseen = Guid.NewGuid(), startedTwice = Guid.NewGuid(), completedTwice = Guid.NewGuid();
        Guid twoProcesses = Guid.NewGuid(), unrecorded = Guid.NewGuid(), underUnrecorded = Guid.NewGuid(), below = Guid.NewGuid();
        Guid one = Guid.NewGuid(), other = Guid.NewGuid();
        var sdk = new Sdk();
        sdk.Complete(10, 100, unseen, 0);
        sdk.Start(11, 100, underUnseen, unseen, "after a start unseen");
        sdk.Start(12, 100, startedTwice, null, "started");
        sdk.Start(13, 100, startedTwice, null, "started again");
        sdk.Start(14, 100, completedTwice, null, "completed twice");
        sdk.Complete(15, 100, completedTwice, 0);
        sdk.Complete(16, 100, completedTwice, 0);
        sdk.Start(17, 100, twoProcesses, null, "completed elsewhere");
        sdk.Complete(18, 200, twoProcesses, 0);
        sdk.Start(19, 200, underUnrecorded, unrecorded, "under another capture's");
        sdk.Start(20, 200, below, underUnrecorded, "below it");
        sdk.Start(21, 100, one, other, "one");
        sdk.Start(22, 100, other, one, "the other");
        sdk.Start(23, 100, Guid.Empty, null, "no operation");
        sdk.Fixture(24, 100);
        using var session = new TemporarySession();
        CooperativeOperationIndex index = sdk.Derive(session.Store);

        Assert.Equal(
            [
                (unseen, 0, CooperativeOperationPlace.Root), (underUnseen, 1, CooperativeOperationPlace.Child),
                (startedTwice, 0, CooperativeOperationPlace.Root), (completedTwice, 0, CooperativeOperationPlace.Root),
                (twoProcesses, 0, CooperativeOperationPlace.Root), (underUnrecorded, 0, CooperativeOperationPlace.UnderUnrecorded),
                (below, 1, CooperativeOperationPlace.Child), (one, 0, CooperativeOperationPlace.Circular),
                (other, 1, CooperativeOperationPlace.Circular),
            ],
            index.Tree.Select(node => (node.Operation.Operation, node.Depth, node.Place)));
        Assert.Equal([unrecorded], index.UnrecordedParents);

        // A start unseen is no start under none: its parent is unknown, and its completion says its status.
        CooperativeOperation start = index.Find(unseen)!;
        Assert.Equal((CooperativeOperationState.StartUnseen, (Guid?)null, (string?)null, 0, 1, (long?)0, (long?)null),
            (start.State, start.Parent, start.Name, start.Starts, start.Completions, start.Status, start.DurationNanoseconds));
        Assert.Equal(CooperativeOperationState.Open, index.Find(underUnseen)!.State);

        // Conflicting: started twice, its first start's name kept and none chosen; completed twice; or in two processes.
        Assert.Equal((CooperativeOperationState.Conflicting, 2, "started"),
            (index.Find(startedTwice)!.State, index.Find(startedTwice)!.Starts, index.Find(startedTwice)!.Name));
        Assert.Equal((CooperativeOperationState.Conflicting, 2, (long?)null),
            (index.Find(completedTwice)!.State, index.Find(completedTwice)!.Completions, index.Find(completedTwice)!.DurationNanoseconds));
        Assert.Equal((CooperativeOperationState.Conflicting, 100, 200),
            (index.Find(twoProcesses)!.State, index.Find(twoProcesses)!.Start!.ProcessId, index.Find(twoProcesses)!.Completion!.ProcessId));

        // A record naming no operation is counted apart; another provider's record of the same number is no operation's.
        Assert.Equal(1, index.WithoutOperation);
        Assert.Equal(9, index.Operations.Count);
    }

    private sealed class Sdk
    {
        private static readonly Guid FixtureProvider = Guid.Parse("2d8f2e6c-6e0a-5a0a-9c48-1f1d0e5d7c11");

        private readonly List<ObservationRowV1> rows = [];
        private readonly List<SourceFieldRowV1> fields = [];
        private readonly HashSet<int> processes = [];
        private ulong ordinal = 1_000;

        public void Start(long ticks, int process, Guid operation, Guid? parent, string name, long? sourceTicks = null) =>
            Add(Record(ticks, CooperativeSdk.OperationStarted, process, operation) with
            {
                Kind = ObservationKind.RequestStart,
                RelatedActivityId = parent,
                ResourceName = name,
            }, sourceTicks);

        public void Complete(long ticks, int process, Guid operation, long status, long? sourceTicks = null) =>
            Add(Record(ticks, CooperativeSdk.OperationCompleted, process, operation) with
            {
                Kind = ObservationKind.RequestEnd,
                StatusCode = status,
                StatusAvailability = FieldAvailability.Present,
            }, sourceTicks);

        /// <summary>A record of InterCat's content fixture, recorded under the same mechanism and numbered from 1 too.</summary>
        public void Fixture(long ticks, int process) =>
            Add(Record(ticks, CooperativeSdk.OperationStarted, process, Guid.NewGuid()) with { ProviderId = FixtureProvider }, null);

        public CooperativeOperationIndex Derive(SessionStore store)
        {
            // Each process began before its records, so each binds to the instance it began.
            ObservationRowV1[] lifecycle = [.. processes.Order().Select((process, at) => Lifecycle(1 + at, ObservationKind.Create, process, (ulong)(1 + at)))];
            _ = TestSessions.Publish(store, [.. lifecycle, .. rows.OrderBy(row => row.NativeTicks)], fields: fields);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = IncrementalDerivationTests.SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            return CooperativeOperationIndex.Derive(observations, fieldSegments, instances);
        }

        private ObservationRowV1 Record(long ticks, ushort eventId, int process, Guid operation)
        {
            _ = processes.Add(process);
            return SdkRecord(ticks * 10, eventId, process, ++ordinal, operation);
        }

        private void Add(ObservationRowV1 row, long? sourceTicks)
        {
            rows.Add(row);
            if (sourceTicks is { } kept)
            {
                fields.Add(Field(row, SourceField.SourceTicks, kept));
            }
        }
    }
}
