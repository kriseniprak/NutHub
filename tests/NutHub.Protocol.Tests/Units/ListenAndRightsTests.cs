using NutHub.Core.Configuration;
using NutHub.Protocol.Security;
using NutHub.Protocol.Server;

namespace NutHub.Protocol.Tests.Units;

public sealed class ListenAndRightsTests
{
    [Theory]
    [InlineData("*", 3493, "*", "*:3493")]
    [InlineData(" 0.0.0.0 ", 3493, "0.0.0.0", "0.0.0.0:3493")]
    [InlineData("::", 3493, "::", "[::]:3493")]
    [InlineData("[::1]", 3494, "::1", "[::1]:3494")]
    [InlineData("0:0:0:0:0:0:0:1", 3493, "::1", "[::1]:3493")]
    [InlineData("::ffff:127.0.0.1", 3493, "127.0.0.1", "127.0.0.1:3493")]
    public void Endpoints_are_normalised(string address, int port, string expectedAddress, string display)
    {
        Assert.True(ListenEndpointKey.TryCreate(new ListenEndpoint { Address = address, Port = port },
                                                out ListenEndpointKey key, out string? error));
        Assert.Null(error);
        Assert.Equal(expectedAddress, key.Address);
        Assert.Equal(display, key.ToString());
    }

    [Theory]
    [InlineData("localhost", 3493)]
    [InlineData("10.0.0.x", 3493)]
    [InlineData("*", 70000)]
    public void Invalid_endpoints_are_reported(string address, int port)
    {
        Assert.False(ListenEndpointKey.TryCreate(new ListenEndpoint { Address = address, Port = port }, out _,
                                                 out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Same_socket_written_differently_is_the_same_key()
    {
        ListenEndpointKey.TryCreate(new ListenEndpoint { Address = "::1", Port = 1 }, out ListenEndpointKey a, out _);
        ListenEndpointKey.TryCreate(new ListenEndpoint { Address = "[0::1]", Port = 1 }, out ListenEndpointKey b, out _);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(NutMonitorRole.None, "Login", false)]
    [InlineData(NutMonitorRole.Secondary, "Login", true)]
    [InlineData(NutMonitorRole.Primary, "Login", true)]
    [InlineData(NutMonitorRole.Secondary, "Primary", false)]
    [InlineData(NutMonitorRole.Primary, "Primary", true)]
    [InlineData(NutMonitorRole.Secondary, "ForcedShutdown", false)]
    [InlineData(NutMonitorRole.Primary, "ForcedShutdown", true)]
    [InlineData(NutMonitorRole.Primary, "SetVariable", false)]
    [InlineData(NutMonitorRole.Primary, "InstantCommand", false)]
    public void Monitor_roles_grant_what_upsd_grants(NutMonitorRole role, string right, bool expected)
    {
        var user = new NutUserConfig { Name = "u", Monitor = role };
        Assert.Equal(expected, NutAuthorizer.HasRight(user, Enum.Parse<NutRight>(right), "load.off"));
    }

    [Fact]
    public void Actions_and_instant_commands_ignore_case()
    {
        var user = new NutUserConfig { Name = "u", Actions = ["set", "Fsd"], InstantCommands = ["Beeper.Enable"] };
        Assert.True(NutAuthorizer.HasRight(user, NutRight.SetVariable, null));
        Assert.True(NutAuthorizer.HasRight(user, NutRight.ForcedShutdown, null));
        Assert.True(NutAuthorizer.HasRight(user, NutRight.InstantCommand, "beeper.enable"));
        Assert.False(NutAuthorizer.HasRight(user, NutRight.InstantCommand, "load.off"));
        Assert.False(NutAuthorizer.HasRight(user, NutRight.Login, null));

        var all = new NutUserConfig { Name = "a", InstantCommands = ["all"] };
        Assert.True(NutAuthorizer.HasRight(all, NutRight.InstantCommand, "load.off"));
    }
}
