using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

public sealed class EtlCanonicalImportTests
{
    [Fact(DisplayName = "I2: an import names the evidence it cannot find instead of importing nothing")]
    public void AMissingEtlIsNamedRatherThanImportedAsEmpty()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"intercat-absent-{Guid.NewGuid():N}.etl");

        FileNotFoundException refusal = Assert.Throws<FileNotFoundException>(() =>
            EtlCanonicalImport.Import(missing, Plan()));

        Assert.Equal(missing, refusal.FileName);
    }

    [Theory(DisplayName = "I2: an import requires both a path and a compiled admission plan")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnImportRequiresItsInputs(string path)
    {
        Assert.Throws<ArgumentException>(() => EtlCanonicalImport.Import(path, Plan()));
        Assert.Throws<ArgumentNullException>(() => EtlCanonicalImport.Import("source.etl", null!));
    }

    [Fact(DisplayName = "I2: an import refuses a bound outside its declared range before reading a record")]
    public void AnImportRefusesAnImpossibleBound()
    {
        string path = Path.Combine(Path.GetTempPath(), $"intercat-empty-{Guid.NewGuid():N}.etl");
        File.WriteAllBytes(path, [0]);
        try
        {
            Assert.Throws<ArgumentException>(() => EtlCanonicalImport.Import(
                path,
                Plan(),
                RetainedEvidencePolicy.MetadataOnly,
                new CanonicalImportOptions { MaximumEntriesInMemory = 0 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OwnedSessionPlan Plan() => new()
    {
        Identity = CaptureSessionIdentity.Create("test", 1),
        Sources = [],
        Providers = [],
    };
}
