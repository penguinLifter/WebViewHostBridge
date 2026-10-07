using System.Text.Json;

namespace WebViewHostBridge.Tests;

public class ConnectionAndHelpersTests
{
    private sealed class LinkedEndpoint : BridgeEndpoint
    {
        public LinkedEndpoint? Peer { get; set; }

        public bool Deliver { get; set; } = true;

        public List<string> Sent { get; } = [];

        // Like WebView2: posting does not wait for the other side's handler. Completed handlers
        // still run inline, so assertions right after a send see their effect.
        protected override Task SendAsync(string json, CancellationToken ct)
        {
            Sent.Add(json);
            if (Deliver && Peer is not null)
                _ = Peer.ReceiveAsync(json, ct);
            return Task.CompletedTask;
        }
    }

    private static (LinkedEndpoint host, LinkedEndpoint page) Pair(bool awaitConnection = false, TimeSpan? timeout = null)
    {
        var host = new LinkedEndpoint { AwaitConnection = awaitConnection, RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        var page = new LinkedEndpoint { RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        host.Peer = page;
        page.Peer = host;
        return (host, page);
    }

    // ---- handshake ----

    [Fact]
    public async Task Announce_ConnectsBothSides_WhicheverStartsFirst()
    {
        var (host, page) = Pair();
        var hostConnected = 0;
        var pageConnected = 0;
        host.Connected += (_, _) => hostConnected++;
        page.Connected += (_, _) => pageConnected++;

        host.Deliver = false;                 // the page is not listening yet: the host's hello is lost
        await host.AnnounceAsync();
        host.Deliver = true;
        Assert.False(host.IsConnected);

        await page.AnnounceAsync();           // the page starts later and announces itself

        Assert.True(host.IsConnected);
        Assert.True(page.IsConnected);
        Assert.Equal(1, hostConnected);
        Assert.Equal(1, pageConnected);
        await host.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Announce_Again_IsNotARestart()
    {
        var (host, page) = Pair();
        await page.AnnounceAsync();
        var pageWork = new TaskCompletionSource<object?>();
        page.On("slow", (_, _) => pageWork.Task);
        var pending = host.RequestAsync<string>("slow");
        var connectedAgain = false;
        host.Connected += (_, _) => connectedAgain = true;

        await page.AnnounceAsync();
        await host.AnnounceAsync();

        Assert.False(pending.IsCompleted);
        Assert.False(connectedAgain);
        pageWork.SetResult("done");
        Assert.Equal("done", await pending);
    }

    [Fact]
    public async Task PageReload_FailsPendingRequests_AndConnectsTheNewPage()
    {
        var (host, page) = Pair();
        await page.AnnounceAsync();
        page.On("slow", (_, _) => new TaskCompletionSource<object?>().Task);
        var pending = host.RequestAsync<string>("slow");
        var connectedAgain = false;
        host.Connected += (_, _) => connectedAgain = true;

        var reloaded = new LinkedEndpoint { Peer = host };
        host.Peer = reloaded;
        await reloaded.AnnounceAsync();

        var ex = await Assert.ThrowsAsync<BridgeException>(() => pending);
        Assert.Contains("restarted", ex.Message);
        Assert.True(connectedAgain);
        Assert.True(reloaded.IsConnected);
    }

    [Fact]
    public async Task AwaitConnection_RequestWaitsForThePage()
    {
        var (host, page) = Pair(awaitConnection: true);
        page.On("getContext", (_, _) => Task.FromResult<object?>(42));

        var request = host.RequestAsync<int>("getContext");
        await Task.Delay(50);
        Assert.False(request.IsCompleted);
        Assert.Empty(host.Sent);

        await page.AnnounceAsync();

        Assert.Equal(42, await request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AwaitConnection_NobodyConnects_TimesOutWithClearMessage()
    {
        var (host, _) = Pair(awaitConnection: true, timeout: TimeSpan.FromMilliseconds(100));

        var request = await Assert.ThrowsAsync<TimeoutException>(() => host.RequestAsync<int>("getContext"));
        var post = await Assert.ThrowsAsync<TimeoutException>(() => host.PostAsync("refresh"));

        Assert.Contains("did not connect", request.Message);
        Assert.Contains("did not connect", post.Message);
    }

    [Fact]
    public async Task WithoutAwaitConnection_SendsRightAway()
    {
        var (host, page) = Pair();
        page.On("getContext", (_, _) => Task.FromResult<object?>(1));

        Assert.False(host.IsConnected);
        Assert.Equal(1, await host.RequestAsync<int>("getContext"));
    }

    [Fact]
    public async Task ResetConnection_FailsPending_AndDisconnects()
    {
        var (host, page) = Pair();
        await page.AnnounceAsync();
        page.On("slow", (_, _) => new TaskCompletionSource<object?>().Task);
        var pending = host.RequestAsync<string>("slow");

        host.ResetConnection();

        var ex = await Assert.ThrowsAsync<BridgeException>(() => pending);
        Assert.Contains("disconnected", ex.Message);
        Assert.False(host.IsConnected);

        await page.AnnounceAsync();
        Assert.True(host.IsConnected);
    }

    [Fact]
    public async Task Dispose_FailsConnectionWaiters()
    {
        var (host, _) = Pair();
        var waiting = host.WhenConnectedAsync();

        host.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
    }

    [Fact]
    public async Task ReservedTypes_AreRejectedForApplications()
    {
        var (host, _) = Pair();

        Assert.Throws<ArgumentException>(() => host.On(BridgeProtocol.HelloType, (_, _) => Task.FromResult<object?>(null)));
        Assert.Throws<ArgumentException>(() => { _ = host.PostAsync("$bridge.custom"); });
        await Assert.ThrowsAsync<ArgumentException>(() => host.RequestAsync<int>("$bridge.custom"));
    }

    [Fact]
    public async Task UnknownReservedMessage_IsSwallowed()
    {
        var (host, _) = Pair();

        Assert.True(await host.ReceiveAsync("""{"type":"$bridge.future"}"""));
        Assert.Empty(host.Sent);
    }

    // ---- protocol ----

    [Fact]
    public void TryParse_AcceptsMessageWrappedInJsonString()
    {
        var json = BridgeProtocol.Serialize(BridgeProtocol.Request("save", new { Id = 1 }));

        var parsed = BridgeProtocol.TryParse(JsonSerializer.Serialize(json));

        Assert.Equal("save", parsed?.Type);
        Assert.True(parsed?.IsRequest);
    }

    [Fact]
    public void TryParse_FlatLegacyMessage_KeepsExtraFields()
    {
        var parsed = BridgeProtocol.TryParse("""{"type":"openForm","formName":"F","parameters":{"id":7}}""");

        Assert.Equal("F", parsed?.ExtensionData?["formName"].GetString());
    }

    [Fact]
    public void Serialize_WithoutExtensionData_StaysCompact()
        => Assert.Equal("""{"type":"x"}""", BridgeProtocol.Serialize(BridgeProtocol.Notification("x")));

    // ---- typed handlers ----

    private sealed record Order(int Id);

    [Fact]
    public async Task OnRequest_Typed_RoundTrip()
    {
        var (host, page) = Pair();
        host.OnRequest<Order, string>("describe", (order, _) => Task.FromResult($"order {order?.Id}"));
        host.OnRequest("ping", _ => Task.FromResult("pong"));

        Assert.Equal("order 5", await page.RequestAsync<string>("describe", new Order(5)));
        Assert.Equal("pong", await page.RequestAsync<string>("ping"));
    }

    [Fact]
    public async Task OnNotification_Typed_ReceivesPayload()
    {
        var (host, page) = Pair();
        Order? sync = null, async = null;
        host.OnNotification<Order>("a", order => sync = order);
        host.OnNotification<Order>("b", (order, _) => { async = order; return Task.CompletedTask; });

        await page.PostAsync("a", new Order(1));
        await page.PostAsync("b", new Order(2));

        Assert.Equal(1, sync?.Id);
        Assert.Equal(2, async?.Id);
    }

    // ---- close guard ----

    [Fact]
    public async Task CloseGuard_AsksThePage()
    {
        var (host, page) = Pair();
        var saved = false;
        using var _ = BridgeCloseGuard.Register(page,
            _ => Task.FromResult(true),
            _ => { saved = true; return Task.FromResult(new SaveAttemptResult(true)); });
        var guard = new BridgeCloseGuard(host);

        Assert.True(await guard.HasUnsavedChangesAsync());
        Assert.True((await guard.RequestSaveAsync()).Success);
        Assert.True(saved);
    }

    [Fact]
    public async Task CloseGuard_SaveError_BecomesUnsuccessfulResult()
    {
        var (host, page) = Pair();
        BridgeCloseGuard.Register(page,
            _ => Task.FromResult(true),
            _ => throw new BridgeException("Fill in the policy number"));

        var result = await new BridgeCloseGuard(host).RequestSaveAsync();

        Assert.False(result.Success);
        Assert.Equal("Fill in the policy number", result.ErrorMessage);
    }

    [Fact]
    public async Task CloseGuard_Registration_DisposesBothHandlers()
    {
        var (host, page) = Pair();
        var registration = BridgeCloseGuard.Register(page, _ => Task.FromResult(false), _ => Task.FromResult(new SaveAttemptResult(true)));

        registration.Dispose();

        await Assert.ThrowsAsync<BridgeRequestException>(() => new BridgeCloseGuard(host).HasUnsavedChangesAsync());
        Assert.False((await new BridgeCloseGuard(host).RequestSaveAsync()).Success);
    }

    // ---- form opener ----

    [Fact]
    public async Task FormOpener_PageOpensHostWindow()
    {
        var (host, page) = Pair();
        OpenFormRequest? opened = null;
        BridgeFormOpener.Register(host, (request, _) => { opened = request; return Task.CompletedTask; });

        await new BridgeFormOpener(page).OpenFormAsync("FAgreementMain", new Dictionary<string, object?> { ["agrId"] = 42 });

        Assert.Equal("FAgreementMain", opened?.FormName);
        Assert.Equal(42, opened!.Parameters["AGRID"].GetInt32());
    }

    [Fact]
    public async Task FormOpener_HostAcceptsLegacyFlatMessage()
    {
        var (host, _) = Pair();
        OpenFormRequest? opened = null;
        BridgeFormOpener.Register(host, (request, _) => { opened = request; return Task.CompletedTask; });

        Assert.True(await host.ReceiveAsync("""{"type":"openForm","formName":"FAgreementMain","parameters":{"agrId":7}}"""));

        Assert.Equal(7, opened?.Parameters["agrId"].GetInt32());
    }

    [Fact]
    public async Task FormOpener_MalformedMessage_IsReportedNotThrown()
    {
        var (host, _) = Pair();
        Exception? reported = null;
        host.ReceiveFailed += (_, e) => reported = e.Exception;
        BridgeFormOpener.Register(host, (_, _) => Task.CompletedTask);

        Assert.True(await host.ReceiveAsync("""{"type":"openForm","parameters":{}}"""));

        Assert.IsType<BridgeException>(reported);
    }

    [Fact]
    public async Task FormOpener_FireAndForgetFailure_GoesToSendFailed()
    {
        var page = new FailingEndpoint();
        var opener = new BridgeFormOpener(page);
        var failed = new TaskCompletionSource<Exception>();
        opener.SendFailed += (_, e) => failed.TrySetResult(e);

        opener.OpenForm("F", new Dictionary<string, object?>());

        Assert.IsType<InvalidOperationException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class FailingEndpoint : BridgeEndpoint
    {
        protected override async Task SendAsync(string json, CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("WebView is gone");
        }
    }

    // ---- JS client ----

    [Fact]
    public void Script_IsEmbedded_AndSpeaksTheSameProtocol()
    {
        var source = BridgeScript.Source;

        Assert.Contains("window.WebViewHostBridge", source.Replace("global.WebViewHostBridge", "window.WebViewHostBridge"));
        Assert.Contains($"'{BridgeProtocol.ReservedPrefix}'", source);
        Assert.Contains("RESERVED + 'hello'", source);
        Assert.Contains("RESERVED + 'welcome'", source);
        Assert.Equal(BridgeProtocol.ReservedPrefix + "hello", BridgeProtocol.HelloType);
        Assert.Equal(BridgeProtocol.ReservedPrefix + "welcome", BridgeProtocol.WelcomeType);
    }
}
