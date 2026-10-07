namespace WebViewHostBridge.Tests;

public class HostUserAgentTests
{
    private const string Product = "Sample-Shell";
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    [Fact]
    public void Marker_IsProductSlashVersion()
    {
        Assert.Equal("Sample-Shell/1.0", HostUserAgent.Marker(Product));
        Assert.Equal("Sample-Shell/2.3", HostUserAgent.Marker(Product, "2.3"));
    }

    [Fact]
    public void IsHost_UserAgentWithMarker_True()
        => Assert.True(HostUserAgent.IsHost(ChromeUserAgent + " " + HostUserAgent.Marker(Product), Product));

    [Fact]
    public void IsHost_IgnoresVersionAndCase()
        => Assert.True(HostUserAgent.IsHost(ChromeUserAgent + " sample-shell/9.9", Product));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(ChromeUserAgent)]
    [InlineData(ChromeUserAgent + " Sample-ShellX/1.0")]
    [InlineData(ChromeUserAgent + " XSample-Shell/1.0")]
    [InlineData(ChromeUserAgent + " Other-Shell/1.0")]
    public void IsHost_WithoutMarker_False(string? userAgent)
        => Assert.False(HostUserAgent.IsHost(userAgent, Product));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("My Shell")]
    [InlineData("My/Shell")]
    public void InvalidProduct_Throws(string product)
    {
        Assert.Throws<ArgumentException>(() => HostUserAgent.Marker(product));
        Assert.Throws<ArgumentException>(() => HostUserAgent.IsHost(ChromeUserAgent, product));
    }
}
