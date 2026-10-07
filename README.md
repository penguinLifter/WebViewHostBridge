# WebViewHostBridge

Zero-dependency two-way bridge between a **desktop shell** (WinForms / WPF hosting WebView2 or CefSharp)
and the **web app** it renders (Blazor, ASP.NET Core, plain JS — anything behind the browser control).

| Piece | Direction | Purpose |
|---|---|---|
| `IBridgeEndpoint` / `BridgeEndpoint` | both | Typed request/reply and notifications over any transport, connection handshake |
| `BridgeScript` (`window.WebViewHostBridge`) | page | The same endpoint for plain JS pages, plus Blazor forwarding helpers |
| `BridgeCloseGuard` (`IHostCloseGuard`) | host → page | Ask the page about unsaved changes before closing |
| `BridgeFormOpener` (`IFormOpener`) | page → host | Open a native host window from the page |
| `HostUserAgent` | host → server | Tell the server the request comes from the embedded shell |

```
dotnet add package WebViewHostBridge
```

## Two-way messaging — `IBridgeEndpoint`

Both sides get the same API:

```csharp
public interface IBridgeEndpoint
{
    Task PostAsync(string type, object? payload = null, CancellationToken ct = default);          // notification
    Task<TResponse?> RequestAsync<TResponse>(string type, object? payload = null, CancellationToken ct = default);
    IDisposable On(string type, Func<BridgeMessage, CancellationToken, Task<object?>> handler);    // handle incoming
}
```

A typical flow — the page asks the host for its launch context, does its work and reports the result:

```csharp
// host
bridge.OnRequest("getContext", _ => Task.FromResult(new CaseContext(42)));
bridge.OnNotification<OrderResult>("orderCompleted", RefreshCase);

// page
var context = await bridge.RequestAsync<CaseContext>("getContext");
await bridge.PostAsync("orderCompleted", new OrderResult(orderId, codes));
```

Typed registration helpers (`BridgeEndpointExtensions`): `OnNotification<TPayload>`, `OnRequest<TRequest, TResponse>`,
`OnRequest<TResponse>` (no payload). The raw `On` gives the whole `BridgeMessage`.

- A handler's return value becomes the reply; a thrown exception becomes an error reply, raised on the
  requesting side as `BridgeRequestException`. A request without a handler fails fast instead of timing out.
- Only a `BridgeException` sends its message to the other side — any other exception becomes a generic
  `Request '<type>' failed.`, so internals do not leak to the page. Override `FormatError` to change that.
- `ReceiveAsync` never throws for a failing handler or reply; those are raised as `ReceiveFailed` —
  subscribe to log them.
- No reply within `RequestTimeout` (default 30 s) → `TimeoutException`.
- Wire format: `{ "type", "payload", "id", "replyTo", "error" }`, camelCase JSON (`BridgeProtocol`).
  Types starting with `$bridge.` are reserved for the bridge itself.

### Connection handshake

A message sent before the other side listens is lost — e.g. the host asks right after navigation while the
Blazor circuit is still starting. Each side calls `AnnounceAsync()` once it can receive; the side that hears
the announcement answers it, so both become connected whichever starts first.

```csharp
var bridge = new WebView2Bridge(webView, origin) { AwaitConnection = true };
await bridge.AnnounceAsync();
bridge.Connected += (_, _) => Log("page is ready");      // also after every page reload

await bridge.WhenConnectedAsync();                       // or just send: with AwaitConnection = true
var context = await bridge.RequestAsync<CaseContext>("getContext");   // waits for the page within RequestTimeout
```

- `AwaitConnection = true` — `PostAsync` / `RequestAsync` wait for the connection (within `RequestTimeout`)
  instead of sending into the void. Off by default.
- A reloaded page announces itself as a new session: requests still waiting on the old page fail at once
  with `BridgeException`, `Connected` fires again.
- `ResetConnection()` — forget the page (e.g. on `NavigationStarting`); pending requests fail at once.
- Announcing again from the same endpoint is harmless.

### Plugging in a transport

`BridgeEndpoint` handles correlation, timeouts, dispatch and the handshake; a transport only sends strings
and feeds incoming ones to `ReceiveAsync`.

**Host (WinForms + WebView2):**

```csharp
public sealed class WebView2Bridge : BridgeEndpoint
{
    private readonly WebView2 _view;

    public WebView2Bridge(WebView2 view, Uri trustedOrigin)
    {
        _view = view;
        _view.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src) ||
                src.Scheme != trustedOrigin.Scheme || src.Authority != trustedOrigin.Authority)
                return;                                   // the page is untrusted input
            // WebMessageAsJson works whether the page posted a string or an object.
            if (!await ReceiveAsync(e.WebMessageAsJson))
                HandleLegacyMessage(e);
        };
    }

    protected override Task SendAsync(string json, CancellationToken ct)
    {
        _view.Invoke(() => _view.CoreWebView2.PostWebMessageAsString(json));
        return Task.CompletedTask;
    }
}
```

`ReceiveAsync` never throws for handler failures, so awaiting it from the `async void` event handler is safe.
Handlers run on the thread that called `ReceiveAsync` — marshal to the UI thread in the host if needed.

**Page (Blazor Server)** — the bundled script forwards both ways:

```csharp
public sealed class JsBridge(IJSRuntime js) : BridgeEndpoint
{
    public async Task StartAsync()
    {
        await js.InvokeVoidAsync("WebViewHostBridge.forward", DotNetObjectReference.Create(this));
        await AnnounceAsync();
    }

    [JSInvokable] public Task Receive(string json) => ReceiveAsync(json);

    protected override Task SendAsync(string json, CancellationToken ct) =>
        js.InvokeVoidAsync("WebViewHostBridge.send", ct, json).AsTask();
}
```

## JS client — `BridgeScript`

The package embeds `webviewhostbridge.js`, wire-compatible with `BridgeEndpoint`. The host can give it to
every page:

```csharp
await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript.Source);
```

or serve it yourself (the file is also in the repository: `src/WebViewHostBridge/js/webviewhostbridge.js`).
Injecting only defines `window.WebViewHostBridge`; nothing listens until the page asks.

**Plain JS page:**

```js
const bridge = WebViewHostBridge.create({ awaitConnection: true });   // subscribes and announces

const context = await bridge.request('getContext');
await bridge.post('orderCompleted', { orderId: 7 });

bridge.on('hasUnsavedChanges', () => form.isDirty);
bridge.on('requestSave', async () => {
  if (!form.valid) throw new WebViewHostBridge.BridgeError('Fill in the policy number');
  await form.save();
  return { success: true };
});
```

Options: `transport` (`{ send(json), subscribe(onMessage) → unsubscribe }`, WebView2 by default),
`requestTimeout` (ms, 30000), `awaitConnection`, `announce` (true), `onError(error, message)`.
The endpoint also has `onConnected(listener)`, `whenConnected()`, `isConnected`, `receive(raw)`, `dispose()`.

**Blazor page:** `WebViewHostBridge.forward(dotNetRef, method = 'Receive')` and `WebViewHostBridge.send(json)`
(see `JsBridge` above).

## `BridgeCloseGuard` — host → page

```csharp
// page (C#) — or answer 'hasUnsavedChanges' / 'requestSave' from JS as above
BridgeCloseGuard.Register(pageBridge,
    ct => Task.FromResult(form.IsDirty),
    async ct => { await form.SaveAsync(ct); return new SaveAttemptResult(true); });

// host, in OnFormClosing: e.Cancel = true, then
var guard = new BridgeCloseGuard(hostBridge);
if (await guard.HasUnsavedChangesAsync())
{
    // ask the user: Save / Discard / Cancel
    var saved = await guard.RequestSaveAsync();          // an error reply → Success = false, ErrorMessage = its text
}
```

`BridgeCloseGuard` implements `IHostCloseGuard`:

```csharp
public interface IHostCloseGuard
{
    Task<bool> HasUnsavedChangesAsync(CancellationToken ct = default);
    Task<SaveAttemptResult> RequestSaveAsync(CancellationToken ct = default);
}

public sealed record SaveAttemptResult(bool Success, string? ErrorMessage = null);
```

Without the bridge, a host can still implement the guard by script (`window.hasUnsavedChanges()` /
`window.requestSave()`); note that WebView2 `ExecuteScriptAsync` does not await a returned `Promise` — use
`CallDevToolsProtocolMethodAsync("Runtime.evaluate", ...)` with `awaitPromise: true` for that.

## `BridgeFormOpener` — page → host

```csharp
// page
IFormOpener opener = new BridgeFormOpener(pageBridge);
opener.OpenForm("AgreementCard", new Dictionary<string, object?> { ["id"] = 42 });

// host
BridgeFormOpener.Register(hostBridge, (request, ct) =>
{
    if (request.FormName != "AgreementCard") return Task.CompletedTask;     // whitelist: the page is untrusted
    BeginInvoke(() => OpenAgreement(request.Parameters["id"].GetInt32()));
    return Task.CompletedTask;
});
```

`Register` also accepts the flat 1.0 message `{ "type": "openForm", "formName": ..., "parameters": ... }`,
so old pages keep working. Parameter names are matched case-insensitively.

## `HostUserAgent` — host → server

```csharp
// host, after EnsureCoreWebView2Async
webView.CoreWebView2.Settings.UserAgent += " " + HostUserAgent.Marker("MyApp-Shell");

// server
bool embedded = HostUserAgent.IsHost(httpContext.Request.Headers.UserAgent, "MyApp-Shell");
```

The user agent rides on every request, so it survives in-app navigation, reloads and login redirects —
unlike a `?embedded=1` query flag. Matching ignores the marker version and case.

## Compatibility

- `net8.0`, no dependencies.
- Semantic versioning; breaking changes only in a major version. See [CHANGELOG](CHANGELOG.md).
- Messages are compatible across 1.x: a 1.2 endpoint talks to a 1.1 one (without the handshake).

## License

[MIT](LICENSE)
