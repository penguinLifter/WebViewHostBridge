# Changelog

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
