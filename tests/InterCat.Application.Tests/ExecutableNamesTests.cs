using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class ExecutableNamesTests
{
    [Fact(DisplayName = "§6.3: an executable group reads as its file name, not its device path")]
    public void AUniqueExecutableReadsAsItsFileName()
    {
        Dictionary<string, string> labels = ExecutableNames.Label(
        [
            ("svchost", @"\Device\HarddiskVolume8\Windows\System32\svchost.exe"),
            ("system", "System"),
            ("unknown", null),
            ("blank", "  "),
        ]);

        Assert.Equal("svchost.exe", labels["svchost"]);
        Assert.Equal("System", labels["system"]);
        Assert.Equal(ExecutableNames.Unwitnessed, labels["unknown"]);
        Assert.Equal(ExecutableNames.Unwitnessed, labels["blank"]);
    }

    [Fact(DisplayName = "§6.3: executables that share a file name add just enough folders to be told apart")]
    public void SharedFileNamesAddTheFewestFoldersThatDistinguishThem()
    {
        Dictionary<string, string> labels = ExecutableNames.Label(
        [
            ("git-cmd", @"\Device\HarddiskVolume8\Program Files\Git\cmd\git.exe"),
            ("git-mingw", @"\Device\HarddiskVolume8\Program Files\Git\mingw64\bin\git.exe"),
            ("webview-152", @"\Device\HarddiskVolume8\Program Files (x86)\Microsoft\EdgeWebView\Application\152.0.4191.66\msedgewebview2.exe"),
            ("webview-153", @"\Device\HarddiskVolume8\Program Files (x86)\Microsoft\EdgeWebView\Application\153.0.4234.32\msedgewebview2.exe"),
            ("tool-a", @"C:\a\same\tool.exe"),
            ("tool-b", @"C:\b\same\TOOL.EXE"),
        ]);

        Assert.Equal("git.exe (cmd)", labels["git-cmd"]);
        Assert.Equal("git.exe (bin)", labels["git-mingw"]);
        Assert.Equal("msedgewebview2.exe (152.0.4191.66)", labels["webview-152"]);
        Assert.Equal("msedgewebview2.exe (153.0.4234.32)", labels["webview-153"]);

        // "same" alone does not separate them; one more folder does, and the file name keeps its own spelling.
        Assert.Equal(@"tool.exe (a\same)", labels["tool-a"]);
        Assert.Equal(@"TOOL.EXE (b\same)", labels["tool-b"]);
        Assert.Equal(labels.Count, labels.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact(DisplayName = "§6.3: a bare name and a path with the same file name stay distinct")]
    public void ABareNameAndAPathStayDistinct()
    {
        Dictionary<string, string> labels = ExecutableNames.Label([("bare", "Registry"), ("path", @"C:\Windows\Registry")]);

        Assert.Equal("Registry", labels["bare"]);
        Assert.Equal("Registry (Windows)", labels["path"]);
    }

    [Fact(DisplayName = "R8: a duplicated group key is refused rather than silently relabelled")]
    public void DuplicateKeysAreRefused() =>
        Assert.Throws<ArgumentException>(() => ExecutableNames.Label([("k", "a.exe"), ("k", "b.exe")]));
}
