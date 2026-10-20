using InterCat.Application;
using InterCat.Domain;
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

    [Fact(DisplayName = "§7.1: a paired channel is named by the processes at its ends, the one that made it first with an arrow at the other, else joined by and")]
    public void AChannelNamesWhoConnectedToWhom()
    {
        ProcessInstanceId server = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        ProcessInstanceId client = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        string? NameOf(ProcessInstanceId id) => id == server ? "server.exe · PID 200" : id == client ? "client.exe · PID 100" : null;
        Channel channel = new("transport:k", "edge", "127.0.0.1:8080 ↔ 127.0.0.1:50000", Mechanism.Tcp, Direction.UnknownDirection, 2, null,
            CoverageState.UnknownCoverage)
        {
            FirstHolder = server,
            SecondHolder = client,
        };

        // The client made it from its end, the name's second: it leads, its endpoint first, the arrow at the server.
        Assert.Equal("client.exe · PID 100 → server.exe · PID 200 · TCP · :50000 → :8080 on 127.0.0.1",
            ChannelNames.Ends(channel with { ConnectedBy = client, ConnectedFrom = "127.0.0.1:50000" }, NameOf));

        // Made from the name's first end, its order stands.
        Assert.Equal("server.exe · PID 200 → client.exe · PID 100 · TCP · :8080 → :50000 on 127.0.0.1",
            ChannelNames.Ends(channel with { ConnectedBy = server, ConnectedFrom = "127.0.0.1:8080" }, NameOf));

        // Where the capture saw neither open it, as for a UDP flow, the name's order, joined by and.
        Assert.Equal("server.exe · PID 200 and client.exe · PID 100 · UDP · :8080 ↔ :50000 on 127.0.0.1",
            ChannelNames.Ends(channel with { Mechanism = Mechanism.Udp }, NameOf));

        // A process connected to itself leads from the end it connected from; ends on two hosts keep both.
        Assert.Equal("client.exe · PID 100 → client.exe · PID 100 · TCP · :50000 → :8080 on 127.0.0.1",
            ChannelNames.Ends(channel with { FirstHolder = client, ConnectedBy = client, ConnectedFrom = "127.0.0.1:50000" }, NameOf));
        Assert.Equal("client.exe · PID 100 → server.exe · PID 200 · TCP · 10.0.0.5:50000 → 10.0.0.9:8080",
            ChannelNames.Ends(channel with { Name = "10.0.0.9:8080 ↔ 10.0.0.5:50000", ConnectedBy = client, ConnectedFrom = "10.0.0.5:50000" },
                NameOf));

        // A process the names do not hold is unlisted, and an end no process is known to hold, unknown.
        Assert.Equal("an unlisted process and an unknown process · TCP · :8080 ↔ :50000 on 127.0.0.1",
            ChannelNames.Ends(channel with { FirstHolder = new ProcessInstanceId(Guid.Parse("00000000-0000-0000-0000-000000000009")),
                SecondHolder = null }, NameOf));
    }
}
