namespace WebViewHostBridge.Tests;

public class BridgeProtocolTests
{
    [Fact]
    public void Serialize_UsesCamelCase_AndOmitsNulls()
    {
        var json = BridgeProtocol.Serialize(BridgeProtocol.Notification("orderCompleted", new { OrderId = 5 }));

        Assert.Equal("""{"type":"orderCompleted","payload":{"orderId":5}}""", json);
    }

    [Fact]
    public void Request_HasId_ReplyPointsToIt()
    {
        var request = BridgeProtocol.Request("getContext");
        var reply = BridgeProtocol.Reply(request, 1);

        Assert.True(request.IsRequest);
        Assert.True(reply.IsReply);
        Assert.Equal(request.Id, reply.ReplyTo);
        Assert.Equal(1, reply.PayloadAs<int>());
    }

    [Fact]
    public void Reply_ToNotification_Throws()
        => Assert.Throws<ArgumentException>(() => BridgeProtocol.Reply(BridgeProtocol.Notification("x")));

    [Fact]
    public void TryParse_RoundTrips()
    {
        var request = BridgeProtocol.Request("save", new { Id = 3 });

        var parsed = BridgeProtocol.TryParse(BridgeProtocol.Serialize(request));

        Assert.Equal(request.Id, parsed?.Id);
        Assert.Equal("save", parsed?.Type);
        Assert.Equal(3, parsed?.Payload?.GetProperty("id").GetInt32());
    }

    [Fact]
    public void TryParse_LegacyFlatMessage_KeepsType()
        => Assert.Equal("openForm", BridgeProtocol.TryParse("""{"type":"openForm","formName":"F","parameters":{}}""")?.Type);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{broken")]
    [InlineData("""{"payload":1}""")]
    public void TryParse_NotABridgeMessage_ReturnsNull(string? json) => Assert.Null(BridgeProtocol.TryParse(json));

    [Fact]
    public void PayloadAs_NoPayload_ReturnsDefault()
        => Assert.Null(BridgeProtocol.Notification("x").PayloadAs<string>());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyType_Throws(string type)
    {
        Assert.Throws<ArgumentException>(() => BridgeProtocol.Notification(type));
        Assert.Throws<ArgumentException>(() => BridgeProtocol.Request(type));
    }
}
