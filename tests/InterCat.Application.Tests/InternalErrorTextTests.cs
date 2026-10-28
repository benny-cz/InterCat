using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The words the command line and the window say an error nothing mapped in (§20.6 Internal): a defect in InterCat, named
/// by its kind and in its own words, ending as a sentence so the words after it start one of their own.
/// </summary>
public sealed class InternalErrorTextTests
{
    [Fact(DisplayName = "§20.6: an error nothing mapped is said as a defect in InterCat, by its kind and in its own words, ended as a sentence")]
    public void AnErrorNothingMappedIsSaidByItsKindAndWords()
    {
        Assert.Equal("What was being done stopped on an error InterCat did not expect, a defect in InterCat rather than in what was "
            + "asked. InvalidCastException: A row was read as the wrong kind.",
            InternalErrorText.Stopped("What was being done", new InvalidCastException("A row was read as the wrong kind")));

        // Words that end a sentence end it; any other ending is given a full stop, and spaces around them are dropped.
        Assert.Equal("FormatException: Is it a moment?", InternalErrorText.Named(new FormatException("Is it a moment?")));
        Assert.Equal("FormatException: Not a moment!", InternalErrorText.Named(new FormatException("  Not a moment!  ")));
        Assert.Equal("FormatException: Ended.", InternalErrorText.Named(new FormatException("Ended.")));
        Assert.Equal("KeyNotFoundException: The key 'tcp:7' was not present.",
            InternalErrorText.Named(new KeyNotFoundException("The key 'tcp:7' was not present")));

        // An error that says nothing is named by its kind alone.
        Assert.Equal("InvalidOperationException.", InternalErrorText.Named(new InvalidOperationException(" ")));
        Assert.Equal("InvalidOperationException.", InternalErrorText.Named(new InvalidOperationException(string.Empty)));

        Assert.Throws<ArgumentException>(() => InternalErrorText.Stopped(" ", new FormatException("x")));
        Assert.Throws<ArgumentNullException>(() => InternalErrorText.Stopped("icat", null!));
        Assert.Throws<ArgumentNullException>(() => InternalErrorText.Named(null!));
    }
}
