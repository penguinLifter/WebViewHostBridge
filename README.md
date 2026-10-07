# WebViewHostBridge

Zero-dependency contracts between a **desktop shell** (WinForms / WPF hosting WebView2 or CefSharp)
and the **web app** it renders (Blazor, ASP.NET Core, anything behind the browser control).

The package ships only interfaces and a tiny helper — both sides implement their half:

| Contract | Direction | Purpose |
|---|---|---|
| `IFormOpener` | page → host | Open a native host window from the page |
| `IHostCloseGuard` | host → page | Ask the page about unsaved changes before closing |
| `HostUserAgent` | host → server | Tell the server the request comes from the embedded shell |

```
dotnet add package WebViewHostBridge
```

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
