using System.Collections.Concurrent;

namespace WebViewHostBridge.Tests;

public class BridgeEndpointTests
{
    /// <summary>Endpoint whose transport delivers straight into its peer.</summary>
    private sealed class LinkedEndpoint : BridgeEndpoint
    {
        public LinkedEndpoint? Peer { get; set; }

        public ConcurrentQueue<string> Sent { get; } = new();

        public bool Deliver { get; set; } = true;

        protected override async Task SendAsync(string json, CancellationToken ct)
        {
            Sent.Enqueue(json);
            if (Deliver && Peer is not null)
                await Peer.ReceiveAsync(json, ct);
        }
    }

    private static (LinkedEndpoint host, LinkedEndpoint page) Pair(TimeSpan? timeout = null)
    {
        var host = new LinkedEndpoint { RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        var page = new LinkedEndpoint { RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        host.Peer = page;
        page.Peer = host;
        return (host, page);
    }

    private sealed record Context(int CaseId, string Operator);

    private sealed record OrderCompleted(int OrderId, string[] Codes);

    [Fact]
    public async Task Request_GetsReplyFromHandler()
    {
        var (host, page) = Pair();
        host.On("getContext", (_, _) => Task.FromResult<object?>(new Context(42, "op")));

        var context = await page.RequestAsync<Context>("getContext");

        Assert.Equal(new Context(42, "op"), context);
    }

    [Fact]
    public async Task Notification_DeliversPayload_AndExpectsNoReply()
    {
        var (host, page) = Pair();
        OrderCompleted? received = null;
        host.On("orderCompleted", (m, _) =>
        {
            received = m.PayloadAs<OrderCompleted>();
            return Task.FromResult<object?>(null);
        });

        await page.PostAsync("orderCompleted", new OrderCompleted(7, ["A1", "B2"]));

        Assert.Equal(7, received?.OrderId);
        Assert.Equal(["A1", "B2"], received!.Codes);
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task Request_HandlerThrows_RequesterGetsBridgeRequestException()
    {
        var (host, page) = Pair();
        host.On("save", (_, _) => throw new InvalidOperationException("Validation failed"));

        var ex = await Assert.ThrowsAsync<BridgeRequestException>(() => page.RequestAsync<object>("save"));

        Assert.Equal("save", ex.Type);
        Assert.Equal("Validation failed", ex.Error);
    }

    [Fact]
    public async Task Request_NoHandler_FailsFastInsteadOfTimingOut()
    {
        var (_, page) = Pair(TimeSpan.FromMinutes(1));

        var ex = await Assert.ThrowsAsync<BridgeRequestException>(() => page.RequestAsync<object>("unknown"));

        Assert.Contains("No handler", ex.Error);
    }

    [Fact]
    public async Task Request_NoReply_TimesOut()
    {
        var (_, page) = Pair(TimeSpan.FromMilliseconds(100));
        page.Deliver = false;

        await Assert.ThrowsAsync<TimeoutException>(() => page.RequestAsync<object>("getContext"));
    }

    [Fact]
    public async Task Request_Cancelled_ThrowsOperationCanceled()
    {
        var (_, page) = Pair();
        page.Deliver = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.RequestAsync<object>("getContext", ct: cts.Token));
    }

    [Fact]
    public async Task Receive_UnhandledNotification_ReturnsFalse_SoTransportCanFallBack()
    {
        var (host, _) = Pair();

        Assert.False(await host.ReceiveAsync("""{"type":"openForm","formName":"FCard","parameters":{"id":1}}"""));
        Assert.False(await host.ReceiveAsync("not json"));
        Assert.False(await host.ReceiveAsync("""{"foo":1}"""));
    }

    [Fact]
    public async Task Receive_ReplyWithUnknownId_IsIgnored()
    {
        var (host, _) = Pair();

        Assert.True(await host.ReceiveAsync("""{"type":"x","replyTo":"nope"}"""));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task On_DisposeRegistration_Unregisters_ButKeepsNewerHandler()
    {
        var (host, page) = Pair();
        var first = host.On("ping", (_, _) => Task.FromResult<object?>("first"));
        first.Dispose();
        await Assert.ThrowsAsync<BridgeRequestException>(() => page.RequestAsync<string>("ping"));

        var second = host.On("ping", (_, _) => Task.FromResult<object?>("second"));
        first.Dispose();

        Assert.Equal("second", await page.RequestAsync<string>("ping"));
        second.Dispose();
    }

    [Fact]
    public async Task Dispose_FailsPendingRequests()
    {
        var (_, page) = Pair();
        page.Deliver = false;
        var pending = page.RequestAsync<object>("getContext");

        page.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => page.PostAsync("x"));
    }

    [Fact]
    public async Task BothDirections_WorkOnTheSamePair()
    {
        var (host, page) = Pair();
        host.On("getContext", (_, _) => Task.FromResult<object?>(new Context(1, "a")));
        page.On("hasUnsavedChanges", (_, _) => Task.FromResult<object?>(true));

        Assert.Equal(1, (await page.RequestAsync<Context>("getContext"))?.CaseId);
        Assert.True(await host.RequestAsync<bool>("hasUnsavedChanges"));
    }
}
