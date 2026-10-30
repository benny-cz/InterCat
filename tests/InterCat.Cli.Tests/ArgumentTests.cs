using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>How icat reads its arguments: options in any order, operands that look like options, and verbs.</summary>
public sealed class ArgumentTests
{
    [Fact(DisplayName = "R18: an option keeps its value wherever it is written, and an operand may start with a dash")]
    public void AnOptionKeepsItsValueWhereverItIsWritten()
    {
        // Before the session or after it, an option's value is its own: options are read first.
        foreach (string[] args in new[]
        {
            new[] { "--metric", "observations", "session", "--json" },
            ["session", "--metric", "observations", "--json"],
            ["--json", "session", "--metric", "observations"],
        })
        {
            var reader = new CommandLine(args);
            Assert.Equal("observations", reader.TakeOption("--metric"));
            Assert.True(reader.TryTakeFlag("--json"));
            Assert.Equal("session", reader.TakePositional());
            Assert.False(reader.TryReportUnknown(out _));
        }

        // A negative number is an operand, such as a view that starts before the investigation's epoch.
        var view = new CommandLine(["view", "workspace", "early", "-5", "-.5", "10"]);
        Assert.Equal(["view", "workspace", "early", "-5", "-.5", "10"], Enumerable.Range(0, 6).Select(_ => view.TakePositional()));
        Assert.Null(view.TakePositional());

        // After a bare --, every argument is an operand: a note that starts with a dash, or one that reads as an option.
        var note = new CommandLine(["note", "workspace", "--json", "--", "-> retry storm", "--json"]);
        Assert.True(note.TryTakeFlag("--json"));
        Assert.Equal(["note", "workspace", "-> retry storm", "--json"], Enumerable.Range(0, 4).Select(_ => note.TakePositional()));
        Assert.False(note.TryReportUnknown(out _));

        // What is left is reported, an option before an operand.
        var stray = new CommandLine(["-x", "--", "extra"]);
        Assert.True(stray.TryReportUnknown(out string? unknown));
        Assert.Equal("-x", unknown);
        Assert.Equal("extra", stray.TakePositional());
        Assert.True(new CommandLine(["--", "extra"]).TryReportUnknown(out string? leftover));
        Assert.Equal("extra", leftover);

        // A refusal names what it could not take, quoted as given, so its full stop is never read as part of it: an option
        // it does not know, an operand read as one because it starts with a dash - which says how to write it - or an
        // operand past the last.
        Assert.Equal("Unknown option '--bogus'.", CommandLine.Unknown("--bogus"));
        Assert.Equal("Unknown option '-x'.", CommandLine.Unknown("-x"));
        Assert.Equal("Unknown option '-> retry': an operand that starts with a dash follows --, as in -- \"-> retry\".",
            CommandLine.Unknown("-> retry"));
        Assert.Contains("follows --", CommandLine.Unknown("--rows=2"), StringComparison.Ordinal);
        Assert.Equal("Unexpected argument 'extra': the command takes no more.", CommandLine.Unknown("extra"));

        // An option the command took and that is given again is said to be given twice, never called unknown.
        var twice = new CommandLine(["--generation", "1", "--json", "--generation", "7", "--json"]);
        Assert.Equal("1", twice.TakeOption("--generation"));
        Assert.True(twice.TryTakeFlag("--json"));
        Assert.True(twice.TryReportUnknown(out string? again));
        Assert.Equal("--generation is given more than once; give it once.", twice.Refusal(again!));
        Assert.Equal("--json is given more than once; give it once.", twice.Refusal("--json"));
        Assert.Equal("Unknown option '--bogus'.", twice.Refusal("--bogus"));
        Assert.Equal("Unexpected argument '7': the command takes no more.", twice.Refusal("7"));
    }

    [Fact(DisplayName = "R18: what a command could not take is refused as it read it: an option without its value, one written with it after '=', one mistyped")]
    public void WhatIsLeftIsRefusedAsTheCommandReadIt()
    {
        // The command looks for its options, given or not, and what is left is refused by what it looked for.
        var reader = new CommandLine(["--jsno", "--outptu", "x", "--rows=2", "--json=true", "--note=a b", "--output=", "--zz=1", "--limit"]);
        Assert.False(reader.TryTakeFlag("--json"));
        Assert.Null(reader.TakeOption("--output"));
        Assert.Null(reader.TakeOption("--rows"));
        Assert.Null(reader.TakeOption("--note"));
        Assert.Null(reader.TakeIntegerOption("--limit"));
        Assert.True(reader.TryReportUnknown(out string? first));
        Assert.Equal("--jsno", first);

        // An option it looked for that ends the line was given without its value, not unknown.
        Assert.Equal("--limit is given without its value; give one after it.", reader.Refusal("--limit"));

        // One written with its value after '=', as other tools take it, is said how icat takes it.
        Assert.Equal("--rows takes its value as the argument after it, as in --rows 2, not after '='.", reader.Refusal("--rows=2"));
        Assert.Equal("--note takes its value as the argument after it, as in --note \"a b\", not after '='.", reader.Refusal("--note=a b"));
        Assert.Equal("--output takes its value as the argument after it, not after '='.", reader.Refusal("--output="));
        Assert.Equal("--json takes no value; give it alone.", reader.Refusal("--json=true"));
        Assert.Contains("follows --", reader.Refusal("--zz=1"), StringComparison.Ordinal);

        // One a letter or two from an option it looked for is answered with it; one far from every one is only unknown.
        Assert.Equal("Unknown option '--jsno'; did you mean --json?", reader.Refusal("--jsno"));
        Assert.Equal("Unknown option '--outptu'; did you mean --output?", reader.Refusal("--outptu"));
        Assert.Equal("Unknown option '-json'; did you mean --json?", reader.Refusal("-json"));
        Assert.Equal("Unknown option '--bogus'.", reader.Refusal("--bogus"));

        // An operand is never answered with an option, however near: it was not written as one.
        Assert.Equal("Unexpected argument 'output': the command takes no more.", reader.Refusal("output"));
        Assert.Throws<ArgumentNullException>(() => reader.Refusal(null!));
    }

    [Fact(DisplayName = "R18: a value that must be one of a set of names is refused with them as typed, a near one answered with the name meant")]
    public void AValueFromASetIsAnsweredWithTheNameMeant()
    {
        // An enumeration's names as a person types them, in the order of their values.
        Assert.Equal("nothing-before", CommandLine.Kebab("NothingBefore"));
        Assert.Equal("requested-io-bytes", CommandLine.Kebab("RequestedIoBytes"));
        Assert.Equal("tcp", CommandLine.Kebab("Tcp"));
        Assert.Equal(["direct-only", "include-correlated", "include-candidates", "all-including-conflicting"],
            CommandLine.Kebabs<EvidencePolicy>());
        Assert.Throws<ArgumentNullException>(() => CommandLine.Kebab(null!));

        // A value far from every name is refused with them all, in the one form; one a letter or two from a name, whatever
        // its case or its dashes, is answered with that name as it is typed; one not given is said to be required.
        string[] names = ["records", "bytes-sent", "bytes-sent-and-received"];
        const string Expected = "--rank-by expects records, bytes-sent or bytes-sent-and-received; ";
        Assert.Equal(Expected + "'busiest' is not one.", CommandLine.Expects("--rank-by", "busiest", names));
        Assert.Equal(Expected + "'byts-sent' is not one; did you mean bytes-sent?", CommandLine.Expects("--rank-by", "byts-sent", names));
        Assert.Equal(Expected + "'BytesSnet' is not one; did you mean bytes-sent?", CommandLine.Expects("--rank-by", "BytesSnet", names));
        Assert.Equal(Expected + "'BytesSentAndReceived' is not one; did you mean bytes-sent-and-received?",
            CommandLine.Expects("--rank-by", "BytesSentAndReceived", names));
        Assert.Equal("--rank-by is required: records, bytes-sent or bytes-sent-and-received.", CommandLine.Expects("--rank-by", null, names));
        Assert.Throws<ArgumentNullException>(() => CommandLine.Expects("--rank-by", "x", (IReadOnlyList<string>)null!));
    }

    [Fact(DisplayName = "R18: a name is answered with those a letter or two from it, no more than one for every three letters given")]
    public void ANameIsAnsweredWithTheNearest()
    {
        // A letter left out, added, changed or swapped with the next is one.
        Assert.Equal(["processes"], NameText.Nearest("proceses", Icat.Commands));
        Assert.Equal(["export"], NameText.Nearest("expotr", Icat.Commands));
        Assert.Equal(["pin"], NameText.Nearest("pni", Icat.Commands));
        Assert.Equal(["pin"], NameText.Nearest("pins", Icat.Commands));
        Assert.Equal(["session"], NameText.Nearest("sesions", Icat.Commands));

        // Never more than two, nor more than one for every three letters given, so a short name is never answered at random.
        Assert.Empty(NameText.Nearest("procxxxes", Icat.Commands));
        Assert.Empty(NameText.Nearest("pinss", Icat.Commands));
        Assert.Empty(NameText.Nearest("xx", Icat.Commands));

        // Every one as near as the nearest is named, once, in the order given, and none farther.
        Assert.Equal(["import", "export"], NameText.Nearest("ixport", Icat.Commands));
        Assert.Equal(["--pid"], NameText.Nearest("--pidd", ["--pi", "--pid"]));
        Assert.Equal(["--pid", "--pin"], NameText.Nearest("--pi", ["--pid", "--pin", "--pid", "--pi-x"]));
        Assert.Equal("--pid or --pin", NameText.Either(["--pid", "--pin"]));
        Assert.Equal("a, b or c", NameText.Either(["a", "b", "c"]));
        Assert.Throws<ArgumentNullException>(() => NameText.Nearest(null!, []));
        Assert.Throws<ArgumentNullException>(() => NameText.Nearest("x", null!));
        Assert.Throws<ArgumentNullException>(() => NameText.Either(null!));
    }

    [Fact(DisplayName = "R18: a verb is read first, and no option is read after an operand")]
    public void AVerbIsReadFirst()
    {
        // A verb decides which options follow, so it is the first argument, and an option's value never is.
        var measure = new CommandLine(["udp", "--seed", "5"]);
        Assert.Equal("udp", measure.TakeVerb());
        Assert.Equal(5, measure.TakeIntegerOption("--seed"));
        Assert.Null(new CommandLine(["--seed", "5", "tcp"]).TakeVerb());
        Assert.Null(new CommandLine([]).TakeVerb());

        // Reading an option after an operand could have read its value as the operand, so it is a mistake in icat itself.
        var late = new CommandLine(["session", "--rows", "2"]);
        Assert.Equal("session", late.TakePositional());
        Assert.Contains("read every option first",
            Assert.Throws<InvalidOperationException>(() => late.TakeOption("--rows")).Message, StringComparison.Ordinal);
    }
}
