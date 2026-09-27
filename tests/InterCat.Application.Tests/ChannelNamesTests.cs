using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class ChannelNamesTests
{
    [Fact(DisplayName = "§3.2: a narrow row names a channel's ends ports first, and a shared host once")]
    public void ACompactNameLeadsWithThePorts()
    {
        // One host: the ports tell the channels apart, so they lead and the host follows once.
        Assert.Equal(":50000 ↔ :8080 on 127.0.0.1", ChannelNames.Compact("127.0.0.1:50000 ↔ 127.0.0.1:8080"));
        Assert.Equal(":8080 ↔ :50000 on 127.0.0.1", ChannelNames.Compact("127.0.0.1:50000 ↔ 127.0.0.1:8080", swap: true));
        Assert.Equal(":50000 ↔ :443 on [::1]", ChannelNames.Compact("[::1]:50000 ↔ [::1]:443"));

        // Two hosts keep both, abbreviated as a crumb abbreviates them, each with its port.
        Assert.Equal("10.0.0.5:50000 ↔ 10.0.0.9:8080", ChannelNames.Compact("10.0.0.5:50000 ↔ 10.0.0.9:8080"));
        Assert.Equal("[2001:db8…70:7348]:50000 ↔ [::1]:443",
            ChannelNames.Compact("[2001:db8:85a3:8d3:1319:8a2e:370:7348]:50000 ↔ [::1]:443"));

        // Text that is not one name of two ends is kept as it was.
        Assert.Equal(@"\\.\pipe\intercat-cache", ChannelNames.Compact(@"\\.\pipe\intercat-cache"));
        Assert.Equal("a ↔ b ↔ c", ChannelNames.Compact("a ↔ b ↔ c"));
    }
}
