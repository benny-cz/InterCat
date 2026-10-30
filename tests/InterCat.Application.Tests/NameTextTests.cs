using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The programs a person most likely meant by a name that names none (R18): those a letter or two from it, as InterCat
/// lists them, compared without case and by their executable's file name with its extension or without.
/// </summary>
public sealed class NameTextTests
{
    [Fact(DisplayName = "R18: a name is meant by one a letter or two from it whatever their case, and several are named as a sentence names them")]
    public void CaseIsNoLetter()
    {
        // A capital is no letter of its own, in a letter changed or in two swapped.
        Assert.Equal(["--json"], NameText.Nearest("--JSON", ["--output", "--json"]));
        Assert.Equal(["pin"], NameText.Nearest("PNI", ["pin", "raw"]));
        Assert.Equal(["Before the update"], NameText.Nearest("BEFORE THE UPDTE", ["After", "Before the update"]));
        Assert.Empty(NameText.Nearest("XX", ["--json"]));

        // Choices are named as a sentence names them.
        Assert.Equal("a", NameText.Either(["a"]));
        Assert.Equal("a or b", NameText.Either(["a", "b"]));
        Assert.Equal("a, b or c", NameText.Either(["a", "b", "c"]));
        Assert.Throws<ArgumentNullException>(() => NameText.Either(null!));
    }

    [Fact(DisplayName = "R18: a program is meant by a name a letter or two from it, whatever its case, with its extension or without, and as listed with its folders")]
    public void AProgramIsMeantByANameNearIt()
    {
        string[] programs = ["chrome.exe", "msedge.exe", "svchost.exe (System32)", "svchost.exe (SysWOW64)", "notepad"];

        // By its file name but for its extension, by the whole of it, and whatever the case.
        Assert.Equal(["chrome.exe"], NameText.NearestPrograms("chrme", programs));
        Assert.Equal(["chrome.exe"], NameText.NearestPrograms("CHROME.EX", programs));
        Assert.Equal(["msedge.exe"], NameText.NearestPrograms(" msedeg ", programs));
        Assert.Equal(["notepad"], NameText.NearestPrograms("notpad", programs));

        // A program listed with its folders, where two share a file name, is meant by its file name, whatever its folders
        // hold; each as near is.
        Assert.Equal(["svchost.exe (System32)", "svchost.exe (SysWOW64)"], NameText.NearestPrograms("svchots", programs));
        Assert.Equal(["tool.exe (v1.2)"], NameText.NearestPrograms("tol", ["tool.exe (v1.2)"]));

        // Only the nearest are meant, each once, in the order given; none where none is near enough.
        Assert.Equal(["procxy.exe"], NameText.NearestPrograms("procxx", ["procyy.exe", "procxy.exe", "procxy.exe"]));
        Assert.Empty(NameText.NearestPrograms("teams", programs));
        Assert.Empty(NameText.NearestPrograms("", programs));
        Assert.Throws<ArgumentNullException>(() => NameText.NearestPrograms(null!, programs));
        Assert.Throws<ArgumentNullException>(() => NameText.NearestPrograms("x", null!));
    }
}
