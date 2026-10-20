using System.Globalization;
using InterCat.Desktop.Presentation;
using Xunit;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A Windows path in wrapped text breaks after a backslash a name follows and never after its drive's colon, by characters
/// neither seen nor said (R15): Unicode's line breaking parted "C:\Windows\System32\svchost.exe" only after "C:".
/// </summary>
public sealed class PathBreaksTests
{
    private const char Joiner = '\u2060';
    private const char Break = '\u200B';

    [Theory(DisplayName = "R15: a path breaks after each backslash a name follows, its drive's colon joined to its backslash")]
    [InlineData(@"C:\Windows\System32\svchost.exe", "C:\u2060\\\u200BWindows\\\u200BSystem32\\\u200Bsvchost.exe")]
    [InlineData(@"svchost.exe" + "\n" + @"D:\Program Files\App\app.exe", "svchost.exe\nD:\u2060\\\u200BProgram Files\\\u200BApp\\\u200Bapp.exe")]
    [InlineData(@"\\server\share\file.txt", "\\\\\u200Bserver\\\u200Bshare\\\u200Bfile.txt")]
    [InlineData(@"C:\dir\ ends\", "C:\u2060\\\u200Bdir\\ ends\\")]
    public void APathBreaksBetweenItsNames(string text, string breakable)
    {
        Assert.Equal(breakable, PathBreaks.Breakable(text));
        Assert.Equal(text, PathBreaks.Breakable(text).Replace(Joiner.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(Break.ToString(), string.Empty, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R15: text with no backslash is left as it is, and a value not text passes through")]
    public void TextWithoutAPathIsLeftAlone()
    {
        const string text = "30 processes · 9 drawn as one node; Enter opens the group";
        Assert.Same(text, PathBreaks.Breakable(text));
        Assert.Same(text, PathBreaks.Instance.Convert(text, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Null(PathBreaks.Instance.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(42, PathBreaks.Instance.Convert(42, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("C:\u2060\\\u200Bx", PathBreaks.Instance.Convert(@"C:\x", typeof(string), null, CultureInfo.InvariantCulture));
    }
}
