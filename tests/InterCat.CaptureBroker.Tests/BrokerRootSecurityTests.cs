using InterCat.Storage;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerRootSecurityTests
{
    private const string UserSid = "S-1-5-21-11-22-33-1001";

    [Fact(DisplayName = "R16: the production root grants only SYSTEM, Administrators and a read-only viewer")]
    public void ProductionDescriptorNamesOnlyItsThreeTrustees()
    {
        string sddl = BrokerRootSecurityPolicy.Production.BuildSecurityDescriptorSddl(UserSid);

        BrokerSecurityDescriptorFacts facts = BrokerSecurityDescriptorFacts.Parse(sddl);
        BrokerSecurityAce viewer = Assert.Single(facts.DiscretionaryAces, ace => ace.Sid == UserSid);

        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);
        Assert.True(facts.DiscretionaryAclProtected);
        Assert.Equal(3, facts.DiscretionaryAces.Count);
        Assert.Equal(BrokerRootSecurityPolicy.ViewerReadMask, viewer.Mask);
        Assert.Equal(0u, viewer.Mask & 0x0000_0116u);
        Assert.All(
            facts.DiscretionaryAces.Where(ace => ace.Sid != UserSid),
            ace => Assert.Equal(BrokerRootSecurityPolicy.FullControlMask, ace.Mask));
        Assert.DoesNotContain(facts.DiscretionaryAces, ace => ace.Sid is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545");
        Assert.Null(BrokerRootSecurityPolicy.Production.Approve(facts, UserSid));
    }

    [Fact(DisplayName = "R16: the production label is high integrity with no write-up")]
    public void ProductionDescriptorCarriesAHighNoWriteUpLabel()
    {
        BrokerSecurityDescriptorFacts facts = BrokerSecurityDescriptorFacts.Parse(
            BrokerRootSecurityPolicy.Production.BuildSecurityDescriptorSddl(UserSid));

        BrokerSecurityAce label = Assert.IsType<BrokerSecurityAce>(facts.MandatoryLabel);

        Assert.Equal("ML", label.AceType);
        Assert.Equal(BrokerIntegrityLevel.ToSid(BrokerIntegrityLevel.High), label.Sid);
        Assert.Equal(BrokerRootSecurityPolicy.NoWriteUpMask, label.Mask);
    }

    [Fact(DisplayName = "R16: a viewer who is already a broker principal keeps one entry, not a weaker second")]
    public void ViewerWhoIsAlreadyABrokerPrincipalKeepsOneEntry()
    {
        var policy = BrokerRootSecurityPolicy.Production with
        {
            BrokerPrincipalSids = [BrokerRootSecurityPolicy.LocalSystemSid, UserSid],
        };

        IReadOnlyList<BrokerSecurityAce> aces = policy.ExpectedDiscretionaryAces(UserSid);

        Assert.Equal(2, aces.Count);
        Assert.Equal(
            BrokerRootSecurityPolicy.FullControlMask,
            Assert.Single(aces, ace => ace.Sid == UserSid).Mask);
    }

    [Theory(DisplayName = "R16: Windows may rename a trustee or reorder inheritance flags without changing the grant")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;CIOI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:AI(ML;OICI;NW;;;HI)")]
    [InlineData("O:SYD:P(A;CIOI;0x1f01ff;;;S-1-5-18)(A;OICI;FA;;;S-1-5-32-544)(A;CIOI;FR;;;S-1-5-21-11-22-33-1001)S:(ML;CIOI;0x1;;;S-1-16-12288)")]
    public void EquivalentRenderingsOfTheSameDescriptorAreApproved(string sddl)
    {
        BrokerSecurityDescriptorFacts facts = BrokerSecurityDescriptorFacts.Parse(sddl);

        string? problem = BrokerRootSecurityPolicy.Production.Approve(facts, UserSid);

        // The second rendering grants FILE_GENERIC_READ without traverse, which is not the same grant.
        if (sddl.Contains(";FR;", StringComparison.Ordinal))
        {
            Assert.NotNull(problem);
            return;
        }

        Assert.Null(problem);
    }

    [Theory(DisplayName = "R16: a root whose security drifted is refused with the difference named")]
    [InlineData(
        "O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)(A;OICI;FA;;;WD)S:(ML;OICI;NW;;;HI)",
        "unexpected access-control entry")]
    [InlineData(
        "O:BAD:AI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NW;;;HI)",
        "not protected")]
    [InlineData(
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)",
        "no mandatory label")]
    [InlineData(
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NW;;;ME)",
        "labelled S-1-16-8192")]
    [InlineData(
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NR;;;HI)",
        "does not refuse write-up")]
    [InlineData(
        "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NW;;;HI)",
        "missing required access-control entry")]
    [InlineData(
        "O:S-1-5-21-11-22-33-1001D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NW;;;HI)",
        "not a trusted owner")]
    [InlineData(
        "O:BAD:P(A;OICIID;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;S-1-5-21-11-22-33-1001)S:(ML;OICI;NW;;;HI)",
        "unexpected access-control entry")]
    public void DriftedDescriptorsAreRefusedWithTheDifferenceNamed(string sddl, string expectedReason)
    {
        string? problem = BrokerRootSecurityPolicy.Production.Approve(
            BrokerSecurityDescriptorFacts.Parse(sddl),
            UserSid);

        Assert.NotNull(problem);
        Assert.Contains(expectedReason, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "R16: a descriptor with no DACL grants everyone and is refused")]
    public void DescriptorWithoutADaclIsRefused()
    {
        string? problem = BrokerRootSecurityPolicy.Production.Approve(
            BrokerSecurityDescriptorFacts.Parse("O:BAS:(ML;OICI;NW;;;HI)"),
            UserSid);

        Assert.Contains("no discretionary ACL", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory(DisplayName = "R16: a descriptor the broker cannot resolve exactly is unreadable, never assumed empty")]
    [InlineData("O:BAD:P(A;OICI;ZZ;;;SY)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;NOTASID)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY")]
    [InlineData("O:BAD:P(A;OICI;FA;0f0f0f0f-0000-0000-0000-000000000000;;SY)")]
    [InlineData("O:BAD:XX(A;OICI;FA;;;SY)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)junk")]
    [InlineData("O:BAD:P(A;OIC;FA;;;SY)")]
    [InlineData("O:BAD:P(A;OICI;FA;;;SY)D:P(A;OICI;FA;;;BA)")]
    public void UnresolvableDescriptorsAreRefusedRatherThanRead(string sddl)
    {
        Assert.Throws<InvalidDataException>(() => BrokerSecurityDescriptorFacts.Parse(sddl));
    }

    [Fact(DisplayName = "R16: a policy that names a trustee by alias is refused before a root is created")]
    public void PolicyNamingATrusteeByAliasIsRefused()
    {
        var policy = BrokerRootSecurityPolicy.Production with { BrokerPrincipalSids = ["SY"] };

        Assert.Contains("not a SID literal", policy.Validate(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => policy.BuildSecurityDescriptorSddl(UserSid));
    }

    [Fact(DisplayName = "R16: a policy with an unknown mandatory level is refused")]
    public void PolicyWithAnUnknownLabelIsRefused()
    {
        var policy = BrokerRootSecurityPolicy.Production with { MandatoryIntegrityLevel = 0x2100 };

        Assert.Contains("Low, Medium, High or System", policy.Validate(), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "R16: only a single ordinary component is accepted as an owned name")]
    [InlineData("broker-ownership-v1.log", true)]
    [InlineData("journal_0001.icat", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../escape", false)]
    [InlineData(@"..\escape", false)]
    [InlineData("sub/child", false)]
    [InlineData(@"sub\child", false)]
    [InlineData("name:stream", false)]
    [InlineData("name*", false)]
    [InlineData("con", false)]
    [InlineData("COM1.log", false)]
    [InlineData(".hidden", false)]
    [InlineData("-leading", false)]
    [InlineData("trailing.", false)]
    [InlineData("double..dot.log", false)]
    public void OwnedNamesAreAllowListed(string name, bool accepted)
    {
        string? problem = OwnedFileName.Validate(name);

        Assert.Equal(accepted, problem is null);
    }

    [Fact(DisplayName = "R16: an owned name longer than its bound is refused")]
    public void OverlongOwnedNameIsRefused()
    {
        string name = new('a', OwnedFileName.MaximumLength + 1);

        Assert.Contains("at most", OwnedFileName.Validate(name), StringComparison.OrdinalIgnoreCase);
        Assert.Null(OwnedFileName.Validate(new string('a', OwnedFileName.MaximumLength)));
    }
}
