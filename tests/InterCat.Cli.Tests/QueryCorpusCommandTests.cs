using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// §13.2's specification identity across the golden corpus (R18, §10.5): each of FX-QUERY-001's request shapes, written as
/// the command line that asks it, is the specification itself to <c>icat metric</c> - the same canonical bytes and the
/// same identity the specification has over the session read.
/// </summary>
public sealed class QueryCorpusCommandTests
{
    [Fact(DisplayName = "R18: every golden specification asked of icat metric prints the specification's own canonical bytes and identity over the session it reads")]
    public async Task TheGoldenCorpusAskedOfTheCommandLine()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1),
            Lifecycle(2, ObservationKind.Create, 200, 2),
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        var nodes = SessionOverviewProjector.Project(session.Store).Nodes;
        ProcessInstanceId focus = nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId counterpart = nodes.Single(node => node.ProcessId == 200).Id;

        int asked = 0;
        foreach ((string name, MetricRequest request) in QueryCorpus.Of(focus, counterpart))
        {
            QueryIdentity expected = SessionMetrics.Identify(session.Store, request)!;
            string[] command = ["metric", session.Path, .. Arguments(request), "--print-canonical", "--json"];
            (InterCatExitCode code, string output, string error) = await Run(command);
            Assert.True(code == InterCatExitCode.Success, $"{name}: icat {string.Join(' ', command)} answered {code}: {error}");
            // The rows asked for are no part of the canonical bytes or the identity, and are printed beside them.
            using JsonDocument printed = JsonDocument.Parse(output);
            JsonElement root = printed.RootElement;
            int? rows = root.TryGetProperty("requestedRows", out JsonElement top) && top.ValueKind != JsonValueKind.Null ? top.GetInt32() : null;
            Assert.Equal(
                (name, expected.CanonicalSpecification, expected.Token, expected.RequestedRows),
                (name, root.GetProperty("canonicalSpecification").GetString(), root.GetProperty("token").GetString(), rows));
            asked++;
        }

        Assert.Equal(19, asked);
    }

    /// <summary>The command line that asks <paramref name="request"/>: every member it was given, as icat metric reads it.</summary>
    private static List<string> Arguments(MetricRequest request)
    {
        var words = new List<string>();
        void Say(string option, string value) => words.AddRange([option, value]);
        if (request.Basis != AnalysisBasis.SourceObservations) Say("--basis", CommandArgument.Typed(request.Basis));
        Say("--metric", CommandArgument.Typed(request.Metric));
        if (request.ByteDomain is { } domain) Say("--byte-domain", CommandArgument.Typed(domain));
        if (request.AccountingSide is { } side) Say("--side", CommandArgument.Typed(side));
        if (request.RateNumerator is { } numerator) Say("--rate-numerator", CommandArgument.Typed(numerator));
        if (request.DurationInterval is { } duration) Say("--duration", CommandArgument.Typed(duration));
        if (request.Cohort is { } cohort) Say("--cohort", CommandArgument.Typed(cohort));
        if (request.Statistic is { } statistic) Say("--statistic", CommandArgument.Typed(statistic));
        if (request.Layer is { } layer) Say("--layer", CommandArgument.Typed(layer));
        if (request.Mechanism is { } mechanism) Say("--mechanism", CommandArgument.Typed(mechanism));
        if (request.Interval is { } interval)
        {
            Say("--interval", string.Create(CultureInfo.InvariantCulture, $"{interval.StartTicks}:{interval.EndTicks}"));
        }

        if (request.Grouping is { } grouping)
        {
            Say("--group-by", grouping switch
            {
                LaneGrouping.InstanceOnly => "process",
                LaneGrouping.UserSession => "session",
                _ => CommandArgument.Typed(grouping),
            });
        }

        if (request.RequestedRows is { } rows) Say("--top", rows.ToString(CultureInfo.InvariantCulture));
        if (request.EvidencePolicy != EvidencePolicy.IncludeCorrelated) Say("--evidence-policy", CommandArgument.Typed(request.EvidencePolicy));
        if (request.Owner is { } owner) Say("--owner", owner.ToString());
        if (request.Participant is { } participant) Say("--participant", participant.ToString());
        if (request.Sender is { } sender) Say("--sender", sender.ToString());
        if (request.Receiver is { } receiver) Say("--receiver", receiver.ToString());
        if (request.Peer is { } peer) Say("--peer", peer.ToString());
        if (request.Between is { } between)
        {
            Say("--between", string.Join(',', between.First));
            Say("--and", string.Join(',', between.Second));
            Say("--direction", CommandArgument.Typed(between.Direction));
        }

        return words;
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
