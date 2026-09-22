using System.Runtime.Versioning;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsBrokerRootTests
{
    [Fact(DisplayName = "R16: a provisioned root carries a protected DACL and a no-write-up label")]
    public void ProvisionedRootCarriesProtectedDaclAndMandatoryLabel()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();

        BrokerRootReport report = temporary.Root.Report;
        BrokerSecurityDescriptorFacts observed =
            BrokerSecurityDescriptorFacts.Parse(report.ObservedSecurityDescriptorSddl);

        Assert.True(report.CreatedByThisBroker);
        Assert.False(report.SecurityReapplied);
        Assert.True(observed.DiscretionaryAclProtected);
        Assert.Null(temporary.Policy.Approve(observed, temporary.UserSid));
        Assert.NotNull(observed.MandatoryLabel);
        Assert.Equal("ML", observed.MandatoryLabel!.AceType);
        Assert.Equal(
            BrokerIntegrityLevel.ToSid(TemporaryBrokerRoot.CurrentIntegrityLevel),
            observed.MandatoryLabel.Sid);
        Assert.Equal(
            BrokerRootSecurityPolicy.NoWriteUpMask,
            observed.MandatoryLabel.Mask & BrokerRootSecurityPolicy.NoWriteUpMask);
        Assert.Equal(Path.Combine(report.ResolvedParentPath, TemporaryBrokerRoot.RootName), report.Path);
    }

    [Fact(DisplayName = "R16: reopening an already provisioned root adopts it without re-applying security")]
    public void ReopeningAnAlreadyProvisionedRootAdoptsIt()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();

        using WindowsBrokerRoot reopened = temporary.Reprovision();

        Assert.False(reopened.Report.CreatedByThisBroker);
        Assert.False(reopened.Report.SecurityReapplied);
        Assert.Equal(temporary.Root.Path, reopened.Path);
        Assert.Equal(temporary.Root.VolumeSerialNumber, reopened.VolumeSerialNumber);
    }

    [Fact(DisplayName = "R16: a pre-existing directory with default inherited security is repaired and revalidated")]
    public void PreExistingDirectoryWithInheritedSecurityIsRepaired()
    {
        string parent = TemporaryBrokerRoot.CreateTemporaryParent();
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, TemporaryBrokerRoot.RootName));
            BrokerRootSecurityPolicy policy = TemporaryBrokerRoot.PolicyForCurrentProcess();

            using WindowsBrokerRoot root = TemporaryBrokerRoot.Provision(parent, policy);

            Assert.False(root.Report.CreatedByThisBroker);
            Assert.True(root.Report.SecurityReapplied);
            Assert.Null(policy.Approve(
                BrokerSecurityDescriptorFacts.Parse(root.Report.ObservedSecurityDescriptorSddl),
                TemporaryBrokerRoot.CurrentUserSid));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact(DisplayName = "R16: a root owned by an untrusted principal is refused, never adopted or repaired")]
    public void RootOwnedByUntrustedPrincipalIsRefused()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        BrokerRootSecurityPolicy foreignOwner = temporary.Policy with
        {
            TrustedOwnerSids = [BrokerRootSecurityPolicy.LocalSystemSid],
            BrokerPrincipalSids = [BrokerRootSecurityPolicy.LocalSystemSid],
        };

        UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() =>
            temporary.Reprovision(foreignOwner));

        Assert.Contains("will not", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TemporaryBrokerRoot.CurrentUserSid, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "R16: a junction standing in for the root is refused rather than followed")]
    public void JunctionSubstitutedForTheRootIsRefused()
    {
        string parent = TemporaryBrokerRoot.CreateTemporaryParent();
        string elsewhere = TemporaryBrokerRoot.CreateTemporaryParent();
        try
        {
            DirectoryJunction.Create(Path.Combine(parent, TemporaryBrokerRoot.RootName), elsewhere);

            IOException refusal = Assert.Throws<IOException>(() =>
                TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()));

            Assert.Contains("reparse point", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(Path.Combine(parent, TemporaryBrokerRoot.RootName));
            Directory.Delete(parent, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact(DisplayName = "R16: a junction standing in for the parent is refused rather than followed")]
    public void JunctionSubstitutedForTheParentIsRefused()
    {
        string real = TemporaryBrokerRoot.CreateTemporaryParent();
        string link = Path.Combine(Path.GetDirectoryName(real)!, $"{Path.GetFileName(real)}-link");
        try
        {
            DirectoryJunction.Create(link, real);

            IOException refusal = Assert.Throws<IOException>(() =>
                TemporaryBrokerRoot.Provision(link, TemporaryBrokerRoot.PolicyForCurrentProcess()));

            Assert.Contains("reparse point", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFileSystemEntries(real));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [Fact(DisplayName = "R16: the held root handle refuses a rename that would move the validated directory")]
    public void HeldRootHandleRefusesRename()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        string moved = Path.Combine(temporary.Parent, "moved-root");

        Assert.ThrowsAny<IOException>(() => Directory.Move(temporary.Root.Path, moved));
        Assert.True(Directory.Exists(temporary.Root.Path));
        Assert.False(Directory.Exists(moved));
    }

    [Theory(DisplayName = "R16: a destination that is not a single ordinary name never reaches the filesystem")]
    [InlineData(@"..\escape.log")]
    [InlineData(@"nested\child.log")]
    [InlineData("nested/child.log")]
    [InlineData(@"C:\absolute.log")]
    [InlineData("stream.log:alternate")]
    [InlineData("nul")]
    [InlineData("NUL.log")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void OnlyOrdinaryOwnedNamesAreOpened(string name)
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();

        Assert.Throws<ArgumentException>(() => temporary.Root.OpenOwnedFile(
            name,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.None));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: an owned file is written and read back beneath the validated root")]
    public void OwnedFileRoundTripsBeneathTheValidatedRoot()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        byte[] payload = [1, 2, 3, 4, 5];

        using (FileStream write = temporary.Root.OpenOwnedFile(
            "owned.log",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough))
        {
            write.Write(payload);
            write.Flush(flushToDisk: true);
        }

        using FileStream read = temporary.Root.OpenOwnedFile(
            "owned.log",
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.None);
        byte[] observed = new byte[payload.Length];
        read.ReadExactly(observed);

        Assert.Equal(payload, observed);
        Assert.Equal(
            [Path.Combine(temporary.Root.Path, "owned.log")],
            Directory.GetFiles(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: replacing an owned file publishes it under the destination name in one step")]
    public void ReplacingAnOwnedFilePublishesItInOneStep()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        Write(temporary.Root, "live.log", [1, 1, 1]);
        Write(temporary.Root, "next.log", [2, 2, 2, 2]);

        temporary.Root.ReplaceOwnedFile("next.log", "live.log");

        Assert.Equal([2, 2, 2, 2], File.ReadAllBytes(Path.Combine(temporary.Root.Path, "live.log")));
        Assert.Equal(
            [Path.Combine(temporary.Root.Path, "live.log")],
            Directory.GetFiles(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: replacing a destination that is not there publishes the source anyway")]
    public void ReplacingAnAbsentDestinationPublishesTheSource()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        Write(temporary.Root, "next.log", [3, 3]);

        temporary.Root.ReplaceOwnedFile("next.log", "live.log");

        Assert.Equal([3, 3], File.ReadAllBytes(Path.Combine(temporary.Root.Path, "live.log")));
        Assert.False(File.Exists(Path.Combine(temporary.Root.Path, "next.log")));
    }

    [Fact(DisplayName = "R16: a replacement whose source is missing leaves the destination untouched")]
    public void ReplacementWithAMissingSourceLeavesTheDestinationUntouched()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        Write(temporary.Root, "live.log", [4, 4]);

        Assert.Throws<FileNotFoundException>(() => temporary.Root.ReplaceOwnedFile("next.log", "live.log"));
        Assert.Equal([4, 4], File.ReadAllBytes(Path.Combine(temporary.Root.Path, "live.log")));
    }

    [Theory(DisplayName = "R16: a replacement outside the root or onto itself never reaches the filesystem")]
    [InlineData("next.log", "next.log")]
    [InlineData("next.log", @"..\live.log")]
    [InlineData(@"..\next.log", "live.log")]
    public void ReplacementOutsideTheRootIsRefused(string source, string destination)
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        Write(temporary.Root, "next.log", [5]);

        Assert.Throws<ArgumentException>(() => temporary.Root.ReplaceOwnedFile(source, destination));
        Assert.Equal([5], File.ReadAllBytes(Path.Combine(temporary.Root.Path, "next.log")));
    }

    [Fact(DisplayName = "R16: removing an owned file reports whether one was there")]
    public void RemovingAnOwnedFileReportsWhetherOneWasThere()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        Write(temporary.Root, "stale.log", [6]);

        Assert.True(temporary.Root.RemoveOwnedFile("stale.log"));
        Assert.False(temporary.Root.RemoveOwnedFile("stale.log"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: a directory beneath the root is refused rather than deleted")]
    public void ADirectoryBeneathTheRootIsRefusedRatherThanDeleted()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        string nested = Path.Combine(temporary.Root.Path, "nested");
        Directory.CreateDirectory(nested);

        IOException refusal = Assert.Throws<IOException>(() => temporary.Root.RemoveOwnedFile("nested"));

        Assert.Contains("is a directory", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(nested));
    }

    [Fact(DisplayName = "R16: append mode is refused because it hides where a broker write landed")]
    public void AppendModeIsRefused()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();

        Assert.Throws<ArgumentException>(() => temporary.Root.OpenOwnedFile(
            "owned.log",
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            FileOptions.None));
    }

    [Fact(DisplayName = "R16: an ordinary-integrity caller keeps read access and is refused every write")]
    public void OrdinaryIntegrityCallerReadsButCannotWrite()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        string evidence = Path.Combine(temporary.Root.Path, "owned.log");
        byte[] payload = [7, 7, 7];
        using (FileStream write = temporary.Root.OpenOwnedFile(
            "owned.log",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough))
        {
            write.Write(payload);
        }

        int lowered = TemporaryBrokerRoot.CurrentIntegrityLevel == BrokerIntegrityLevel.High
            ? BrokerIntegrityLevel.Medium
            : BrokerIntegrityLevel.Low;
        using var token = LoweredIntegrityToken.Create(lowered);
        byte[]? read = null;
        Exception? writeRefusal = null;
        Exception? createRefusal = null;

        token.Run(() =>
        {
            read = File.ReadAllBytes(evidence);
            writeRefusal = Record.Exception(() =>
                File.OpenWrite(evidence).Dispose());
            createRefusal = Record.Exception(() =>
                File.WriteAllBytes(Path.Combine(temporary.Root.Path, "planted.log"), payload));
        });

        Assert.Equal(payload, read);
        Assert.IsType<UnauthorizedAccessException>(writeRefusal);
        Assert.IsType<UnauthorizedAccessException>(createRefusal);
        Assert.Equal([evidence], Directory.GetFiles(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: a broker below the requested label refuses to provision instead of labelling lower")]
    public void BrokerBelowTheRequestedLabelRefusesToProvision()
    {
        string parent = TemporaryBrokerRoot.CreateTemporaryParent();
        try
        {
            BrokerRootSecurityPolicy aboveThisProcess = TemporaryBrokerRoot.PolicyForCurrentProcess() with
            {
                MandatoryIntegrityLevel = BrokerIntegrityLevel.System,
            };

            UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() =>
                TemporaryBrokerRoot.Provision(parent, aboveThisProcess));

            Assert.Contains("label above itself", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact(DisplayName = "R16: production provisioning requires an elevated broker")]
    public void ProductionProvisioningRequiresAnElevatedBroker()
    {
        if (WindowsBrokerTokenIdentity.ReadCurrentProcess().IsElevated)
        {
            return;
        }

        string parent = TemporaryBrokerRoot.CreateTemporaryParent();
        try
        {
            UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() =>
                TemporaryBrokerRoot.Provision(parent, BrokerRootSecurityPolicy.Production));

            Assert.Contains("not elevated", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static void Write(WindowsBrokerRoot root, string name, byte[] payload)
    {
        using FileStream stream = root.OpenOwnedFile(
            name,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    [Fact(DisplayName = "R16: a missing parent is a named refusal, not a directory the broker creates")]
    public void MissingParentIsRefused()
    {
        string parent = Path.Combine(Path.GetTempPath(), "InterCat.CaptureBroker.Tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() =>
            TemporaryBrokerRoot.Provision(parent, TemporaryBrokerRoot.PolicyForCurrentProcess()));
        Assert.False(Directory.Exists(parent));
    }
}
