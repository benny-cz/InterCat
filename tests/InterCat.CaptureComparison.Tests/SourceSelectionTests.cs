using InterCat.Capture.Windows;
using Xunit;

namespace InterCat.CaptureComparison.Tests;

public sealed class SourceSelectionTests
{
    [Fact(DisplayName = "P27: an impact run captures only the sources it names, the Explore pair unless told otherwise")]
    public void ARunCapturesTheSourcesItNames()
    {
        Assert.Equal(
            [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelNetworkSourceId],
            SourceSelection.Default);
        Assert.Equal([WindowsSourceCatalog.RpcSourceId], SourceSelection.Parse("rpc"));
        Assert.Equal(
            [WindowsSourceCatalog.KernelNetworkSourceId, WindowsSourceCatalog.RpcSourceId],
            SourceSelection.Parse("Network, rpc"));
        Assert.All(SourceSelection.Accepted, name => Assert.NotNull(SourceSelection.Parse(name)));

        // An unknown or repeated name is refused, so a result never says it measured a source it did not capture.
        Assert.Null(SourceSelection.Parse("alpc"));
        Assert.Null(SourceSelection.Parse("rpc,rpc"));
        Assert.Null(SourceSelection.Parse(string.Empty));
    }
}
