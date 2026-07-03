using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.UnitTests;

public class PingToolTests
{
    [Fact]
    public void Ping_WithNoMessage_ReturnsPong()
    {
        var result = PingTool.Ping(null);

        Assert.True(result.Ok);
        Assert.Equal("pong", result.Message);
        Assert.True(result.ServerTimeUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Ping_WithMessage_EchoesMessage()
    {
        var result = PingTool.Ping("hello");

        Assert.True(result.Ok);
        Assert.Equal("hello", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ping_WithBlankMessage_ReturnsPong(string message)
    {
        var result = PingTool.Ping(message);

        Assert.Equal("pong", result.Message);
    }
}
