# WebViewHostBridge

Zero-dependency contracts between a **desktop shell** (WinForms / WPF hosting WebView2 or CefSharp)
and the **web app** it renders (Blazor, ASP.NET Core, anything behind the browser control).

| Contract | Direction | Purpose |
|---|---|---|
| `IBridgeEndpoint` / `BridgeEndpoint` | both | Typed request/reply and notifications over any transport |
| `IFormOpener` | page → host | Open a native host window from the page |
| `IHostCloseGuard` | host → page | Ask the page about unsaved changes before closing |
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
bridge.On("getContext", (_, _) => Task.FromResult<object?>(new { caseId = 42 }));
bridge.On("orderCompleted", (m, _) => { RefreshCase(m.PayloadAs<OrderResult>()); return Task.FromResult<object?>(null); });

// page
var context = await bridge.RequestAsync<CaseContext>("getContext");
await bridge.PostAsync("orderCompleted", new OrderResult(orderId, codes));
```

- A handler's return value becomes the reply; a thrown exception becomes an error reply, raised on the
  requesting side as `BridgeRequestException`. A request without a handler fails fast instead of timing out.
- No reply within `RequestTimeout` (default 30 s) → `TimeoutException`.
- Wire format: `{ "type", "payload", "id", "replyTo", "error" }`, camelCase JSON (`BridgeProtocol`).

### Plugging in a transport

`BridgeEndpoint` handles correlation, timeouts and dispatch; a transport only sends strings and feeds
incoming ones to `ReceiveAsync`.

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
            if (!await ReceiveAsync(e.TryGetWebMessageAsString()))
                HandleLegacyMessage(e);                   // e.g. 1.0 openForm messages
        };
    }

    protected override Task SendAsync(string json, CancellationToken ct)
    {
        _view.Invoke(() => _view.CoreWebView2.PostWebMessageAsString(json));
        return Task.CompletedTask;
    }
}
```

**Page (Blazor Server)** — a small JS shim forwards both ways:

```js
window.hostBridge = {
  connect(dotnet) {
    window.chrome?.webview?.addEventListener('message', e => dotnet.invokeMethodAsync('Receive', e.data));
  },
  send(json) { window.chrome?.webview?.postMessage(json); }
};
```

```csharp
public sealed class JsBridge(IJSRuntime js) : BridgeEndpoint
{
    public ValueTask ConnectAsync() => js.InvokeVoidAsync("hostBridge.connect", DotNetObjectReference.Create(this));

    [JSInvokable] public Task Receive(string json) => ReceiveAsync(json);

    protected override Task SendAsync(string json, CancellationToken ct) => js.InvokeVoidAsync("hostBridge.send", ct, json).AsTask();
}
```

Handlers run on the thread that called `ReceiveAsync` — marshal to the UI thread in the host if needed.

## `IFormOpener` — page → host

```csharp
public interface IFormOpener
{
    void OpenForm(string formName, IDictionary<string, object?> parameters);
}
```

**Web side** — post a message to the host:

```csharp
public sealed class WebViewFormOpener(IJSRuntime js) : IFormOpener
{
    public void OpenForm(string formName, IDictionary<string, object?> parameters)
        => _ = js.InvokeVoidAsync("chrome.webview.postMessage",
               new { type = "openForm", formName, parameters }).AsTask();
}
```

**Host side** (WebView2) — handle the message:

```csharp
webView.CoreWebView2.WebMessageReceived += (_, e) =>
{
    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
    var root = doc.RootElement;
    if (root.GetProperty("type").GetString() != "openForm") return;

    var formName = root.GetProperty("formName").GetString();
    BeginInvoke(() => OpenWindow(formName, root.GetProperty("parameters")));
};
```

Validate `e.Source` and whitelist the accepted parameters — the page is untrusted input for the host.

## `IHostCloseGuard` — host → page

```csharp
public interface IHostCloseGuard
{
    Task<bool> HasUnsavedChangesAsync(CancellationToken ct = default);
    Task<SaveAttemptResult> RequestSaveAsync(CancellationToken ct = default);
}

public sealed record SaveAttemptResult(bool Success, string? ErrorMessage = null);
```

The page exposes two functions, e.g. `window.hasUnsavedChanges()` and `window.requestSave()`
(the latter returns a `Promise<{ ok: boolean, error?: string }>`), and the host implements the guard over them.

> **WebView2 note:** `ExecuteScriptAsync` does not await a returned `Promise`. For `requestSave` use
> `CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"window.requestSave()\",\"awaitPromise\":true,\"returnByValue\":true}")`.

Typical WinForms flow: in `OnFormClosing` set `e.Cancel = true`, run the async check, ask the user
Save / Discard / Cancel, and call `Close()` again once approved.

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
- Semantic versioning; breaking changes only in a major version.

## License

[MIT](LICENSE)
