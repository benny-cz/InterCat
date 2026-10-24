using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// The refusals of icat pin, checkpoint, channels, exchanges and profiles that no test had run, each as a person meets it
/// (R18): an option's value refused by naming what it expects and the value given, an option given twice said to be, and
/// a checkpoint's report the one place its outcome is said.
/// </summary>
public sealed class RefusalWordsTests : IDisposable
{
    private readonly TemporaryDirectory root = new();
    private readonly TemporarySession session = new();

    public RefusalWordsTests()
    {
        Publish(session.Store,
            [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose()
    {
        session.Dispose();
        root.Dispose();
    }

    public static TheoryData<string[], string> Refusals => new()
    {
        { ["pin", "{session}", "--from", "0 s", "--reason", "keep", "--allow-mib", "lots"],
            "--allow-mib expects whole mebibytes from 1 to 1,073,741,824, the most the session may hold while the pin stands; 'lots' is not one." },
        { ["pin", "{session}", "--remove", "abc", "--from", "1 s"], "--remove names the pin it removes alone: it takes no --from, --allow-mib or --reason." },
        { ["pin", "{session}", "--allow-mib", "5"], "--allow-mib and --reason go with the moment a pin keeps records from" },
        { ["pin", "{session}", "--from", "0 s"], "A pin says why it keeps the records, so whoever finds it later knows: --reason <text>." },
        { ["pin", "{session}", "--remove", "zzz"], "No pin of this session is named zzz. icat pin \"{session}\" lists them." },
        { ["pin"], "A session directory is required: icat pin <directory>." },
        { ["channels", "{session}", "--process", "x"], "--process expects a process instance id, as icat overview or icat processes prints it; 'x' is not one." },
        { ["channels", "{session}", "--page-size", "0"], "--page-size expects a number of channels from 1 to 200; '0' is not one." },
        { ["channels", "{session}", "--cursor", "bogus"], "The channel cursor is malformed; restart from the first page." },
        { ["exchanges", "{session}", "--pid", "x"], "--pid expects a process id; 'x' is not one." },
        { ["exchanges", "{session}", "--decode", "--pid", "1", "--json"], "--decode reads the listed exchanges' heads for a person, never into JSON or a file: leave out --json and --output." },
        { ["exchanges", "{session}", "--decode", "--pid", "1", "--output", "out.json"], "--decode reads the listed exchanges' heads for a person, never into JSON or a file: leave out --json and --output." },
        { ["exchanges", "{session}", "--decode"], "--decode reads the heads of the exchanges listed: give --pid, or --exchanges with a number." },
        { ["exchanges", "{session}", "--exchanges", "lots"], "--exchanges expects a number of exchanges per process up to 10,000; 'lots' is not one." },
        { ["checkpoint"], "A published session directory is required: icat checkpoint <directory>." },
        { ["profiles", "nonsense"], "'nonsense' is no capture profile; icat profiles lists every one." },
        { ["profiles", "focused-transport", "--pid", "x"], "--pid expects a process id; 'x' is not one." },
        { ["profiles", "focused-transport", "--pid", "5", "--pid", "5"], "--pid 5 is given more than once; give it once." },
        { ["profiles", "focused-transport", "--mechanism", "smtp"],
            "--mechanism expects tcp, udp, rpc or http; 'smtp' is not one. Which of them a profile can capture is checked per profile." },
        { ["profiles", "content", "--retention", "forever"], "--retention expects stop-at-limit; 'forever' is not one." },
        { ["profiles", "content", "--inspection", "full"], "--inspection expects disabled or hex-text; 'full' is not one." },
        { ["profiles", "content", "--channel", "a", "--channel", "a"], "--channel a is given more than once; give it once." },
        { ["profiles", "explore", "--mechanism", "tcp"], "Mechanism, process and content-scope options apply only to focused-transport or content." },
        { ["profiles", "--retention", "stop-at-limit"], "Profile options require one profile name. List profiles without additional options." },
    };

    [Theory(DisplayName = "R18: icat pin, channels, exchanges, checkpoint and profiles refuse what they cannot read, naming what they expect")]
    [MemberData(nameof(Refusals))]
    public async Task WhatCannotBeReadIsRefused(string[] args, string expected)
    {
        (InterCatExitCode code, string output, string said) = await Run([.. args.Select(arg => arg.Replace("{session}", session.Path, StringComparison.Ordinal))]);

        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains(expected.Replace("{session}", session.Path, StringComparison.Ordinal), said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: a report file that exists is never replaced without --overwrite, as icat profiles says it as every command does")]
    public async Task AnExistingReportIsNotReplaced()
    {
        string report = Path.Combine(root.Path, "report.json");
        File.WriteAllText(report, "{}");
        foreach (string[] args in new[]
        {
            ["profiles", "explore", "--output", report], ["exchanges", session.Path, "--output", report],
            new[] { "checkpoint", session.Path, "--output", report },
        })
        {
            (InterCatExitCode code, _, string said) = await Run(args);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"{report} exists. Pass --overwrite to replace", said, StringComparison.Ordinal);
        }

        Assert.Equal("{}", File.ReadAllText(report));
    }

    [Fact(DisplayName = "R18: icat checkpoint says what came of it in its report alone, and that it publishes one only when the generation names none")]
    public async Task ACheckpointsReportSaysWhatCameOfIt()
    {
        (InterCatExitCode published, string first, string publishing) = await Run("checkpoint", session.Path);
        Assert.Equal(InterCatExitCode.Success, published);
        Assert.Contains("CHECKPOINT PUBLISHED", first, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Generation +1 → 2\r?$", first);
        Assert.Contains("Publishing the session's derivation checkpoint unless the generation names one, so it reopens without "
            + "reading every record.", publishing, StringComparison.Ordinal);
        Assert.DoesNotContain("Derivation checkpoint of generation", publishing, StringComparison.Ordinal);

        // Asked again, nothing is published, and why is said once: in the report.
        string report = Path.Combine(root.Path, "checkpoint.json");
        (InterCatExitCode again, string second, string said) = await Run("checkpoint", session.Path, "--output", report);
        Assert.Equal(InterCatExitCode.Success, again);
        Assert.Contains("NO CHECKPOINT PUBLISHED", second, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Generation +2\r?$", second);
        Assert.Contains("The generation already names a checkpoint", second, StringComparison.Ordinal);
        Assert.DoesNotContain("The generation already names a checkpoint", said, StringComparison.Ordinal);
        Assert.Contains($"Checkpoint report written to {report}.", said, StringComparison.Ordinal);
        Assert.Contains("\"outcome\": \"AlreadyCurrent\"", File.ReadAllText(report), StringComparison.Ordinal);

        // A recording's follow still publishes its own and says so, as every other command that writes a session does.
        string evidence = Path.Combine(root.Path, "evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2], bursts: [1]);
        (_, _, string following) = await Run("follow", evidence, Path.Combine(root.Path, "followed"));
        Assert.Contains("Publishing the session's derivation checkpoint, so it reopens without reading every record.", following,
            StringComparison.Ordinal);
        Assert.Matches(@"Derivation checkpoint of generation \d+ published as generation \d+", following);
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
