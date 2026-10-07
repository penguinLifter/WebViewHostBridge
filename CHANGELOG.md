# Changelog

## 1.2.0

### Added
- Connection handshake: `AnnounceAsync`, `IsConnected`, `WhenConnectedAsync`, `Connected`, `ResetConnection`;
  `AwaitConnection` makes `PostAsync` / `RequestAsync` wait for the other side instead of sending into the void.
  A reloaded page is detected as a new session and requests pending on the old one fail at once.
- `BridgeCloseGuard` — `IHostCloseGuard` over the bridge (`hasUnsavedChanges` / `requestSave` requests).
- `BridgeFormOpener` and `OpenFormRequest` — `IFormOpener` over the bridge; the host side also reads flat 1.0 messages.
- Typed handlers: `OnNotification<TPayload>`, `OnRequest<TRequest, TResponse>`, `OnRequest<TResponse>`.
- JS client `window.WebViewHostBridge` embedded as `BridgeScript.Source`: `create()` for plain JS pages,
  `forward()` / `send()` for Blazor.
- `BridgeMessage.ExtensionData` — fields outside the wire format (e.g. of flat 1.0 messages).
- `ReceiveAsync` / `BridgeProtocol.TryParse` accept a message wrapped in a JSON string, so a WebView2 host can
  always pass `WebMessageAsJson`.

### Changed
- Message types starting with `$bridge.` are reserved: `On`, `PostAsync` and `RequestAsync` reject them.

## 1.1.1

### Fixed
- A throwing notification handler no longer escapes `ReceiveAsync`. Awaited from an `async void` event
  handler (WebView2 `WebMessageReceived`), it used to crash the host process.
- A reply that cannot be sent no longer escapes `ReceiveAsync`.
- A request started while the endpoint was being disposed could hang until its timeout.

### Changed
- Error replies no longer carry arbitrary exception messages to the other side. Throw `BridgeException`
  for a message meant for the requester; anything else is reported as `Request '<type>' failed.`
  Override `BridgeEndpoint.FormatError` to restore the old behaviour.

### Added
- `BridgeEndpoint.ReceiveFailed` — raised when a handler throws or a reply cannot be sent.
- `BridgeException`, now the base of `BridgeRequestException`.

## 1.1.0

- Two-way bridge: `IBridgeEndpoint`, `BridgeEndpoint`, `BridgeProtocol`.

## 1.0.0

- `IFormOpener`, `IHostCloseGuard`, `HostUserAgent`.
