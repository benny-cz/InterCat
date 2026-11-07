using System.Text.RegularExpressions;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The capability catalog (§4.1): every mechanism EN-Mechanism names, with the sources recording it, what fixtures measured
/// on each build and why it stands where it does - an unsupported mechanism explained, never left out.
/// </summary>
public sealed class MechanismCatalogTests
{
    [Fact(DisplayName = "P27: the capability catalog explains every mechanism - the sources that record it, every build's measured tier and the decision behind it, one no source records included - citing only decisions the repository records")]
    public void EveryMechanismIsExplained()
    {
        Assert.Equal(
            [.. Enum.GetValues<Mechanism>().Where(mechanism => mechanism != Mechanism.UnknownMechanism)],
            MechanismCatalog.Entries.Select(entry => entry.Mechanism));
        Assert.All(MechanismCatalog.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Explanation)));

        // Every decision an explanation cites is one the repository records.
        string adrs = Path.Combine(RepositoryRoot(), "docs", "adr");
        string[] cited =
        [
            .. MechanismCatalog.Entries.SelectMany(entry => Regex.Matches(entry.Explanation, @"ADR-\d{3}").Select(match => match.Value)).Distinct(),
        ];
        Assert.NotEmpty(cited);
        Assert.All(cited, decision => Assert.True(Directory.EnumerateFiles(adrs, decision + "-*.md").Any(), $"{decision} is cited, not recorded."));

        // Named pipes: Kernel-File is their source, and FX-PIPE-001 measured them unsupported on both builds it ran on.
        MechanismCatalogEntry pipes = Entry(Mechanism.NamedPipe);
        Assert.Equal([WindowsSourceCatalog.KernelFileSourceId], pipes.SourceIds);
        Assert.Equal(2, pipes.Measured.Count);
        Assert.All(pipes.Measured, measured => Assert.Equal(("FX-PIPE-001", CapabilityTier.Unsupported), (measured.Fixture, measured.Tier)));

        // An anonymous pipe's source is one no profile requests for it; a mailslot has none, measured unsupported; and the
        // clipboard none, measured by no fixture, yet explained.
        MechanismCatalogEntry anonymous = Entry(Mechanism.AnonymousPipe);
        Assert.Empty(anonymous.SourceIds);
        Assert.Equal([WindowsSourceCatalog.KernelFileSourceId], anonymous.UnrequestedSourceIds);
        MechanismCatalogEntry mailslot = Entry(Mechanism.Mailslot);
        Assert.Empty(mailslot.SourceIds);
        Assert.Equal(("FX-MAIL-001", CapabilityTier.Unsupported), (Assert.Single(mailslot.Measured).Fixture, mailslot.Measured[0].Tier));
        Assert.Contains("ADR-085", mailslot.Explanation, StringComparison.Ordinal);
        MechanismCatalogEntry clipboard = Entry(Mechanism.Clipboard);
        Assert.Equal((0, 0), (clipboard.SourceIds.Count, clipboard.Measured.Count));
        Assert.Contains("no fixture has measured them", clipboard.Explanation, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§4.1: a search of the capability catalog keeps the mechanisms whose name, sources, fixtures or explanation hold its text, ignoring case, and every one for no text")]
    public void TheCatalogIsSearched()
    {
        Assert.Equal([Mechanism.NamedPipe, Mechanism.AnonymousPipe, Mechanism.RemoteFileOrSmb], MechanismCatalog.Find("PIPE").Select(entry => entry.Mechanism));
        Assert.Equal([Mechanism.Mailslot], MechanismCatalog.Find("fx-mail-001").Select(entry => entry.Mechanism));
        Assert.Equal([Mechanism.WindowMessage, Mechanism.Dde], MechanismCatalog.Find("ADR-086").Select(entry => entry.Mechanism));
        Assert.Equal([Mechanism.WindowMessage], MechanismCatalog.Find(WindowsSourceCatalog.Win32kSourceId).Select(entry => entry.Mechanism));
        Assert.Equal([Mechanism.SharedSection, Mechanism.Synchronization], MechanismCatalog.Find("topology only").Select(entry => entry.Mechanism));
        Assert.Empty(MechanismCatalog.Find("no such words"));
        Assert.Same(MechanismCatalog.Entries, MechanismCatalog.Find("  "));
    }

    private static MechanismCatalogEntry Entry(Mechanism mechanism) => MechanismCatalog.Entries.Single(entry => entry.Mechanism == mechanism);

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
