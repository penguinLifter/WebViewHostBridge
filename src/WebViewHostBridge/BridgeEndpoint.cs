using System.Collections.Concurrent;
using System.Diagnostics;

namespace WebViewHostBridge;

/// <summary>
/// Transport-agnostic <see cref="IBridgeEndpoint"/>: request/reply correlation, timeouts, handler dispatch
/// and the connection handshake. A transport derives from it, implements <see cref="SendAsync"/> and feeds
/// every incoming raw message to <see cref="ReceiveAsync"/>:
/// <list type="bullet">
///   <item>WebView2 host — <c>CoreWebView2.PostWebMessageAsString</c> / <c>WebMessageReceived</c>.</item>
///   <item>Blazor page — JS interop over <c>chrome.webview.postMessage</c> / <c>chrome.webview.addEventListener("message")</c>.</item>
/// </list>
/// <para>Connection: each side calls <see cref="AnnounceAsync"/> once it can receive. The side that hears the
/// announcement answers it, so both become <see cref="IsConnected"/> whichever starts first. A reloaded page
/// announces itself as a new session: requests still waiting on the old one fail at once.</para>
/// </summary>
public abstract class BridgeEndpoint : IBridgeEndpoint, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<BridgeMessage, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly object _connectionGate = new();
    private TaskCompletionSource _connected = NewConnectionSource();
    private string? _peerSession;
    private volatile bool _disposed;

    /// <summary>
    /// How long <see cref="RequestAsync{TResponse}"/> waits for a reply — including the wait for the
    /// connection when <see cref="AwaitConnection"/> is on.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, <see cref="PostAsync"/> and <see cref="RequestAsync{TResponse}"/> wait until the other side
    /// has connected (at most <see cref="RequestTimeout"/>) instead of sending into the void — e.g. while the
    /// Blazor circuit is still starting. Requires both sides to call <see cref="AnnounceAsync"/>.
    /// </summary>
    public bool AwaitConnection { get; init; }

    /// <summary>True once the other side has announced itself, until <see cref="ResetConnection"/>.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_connectionGate)
                return _connected.Task.IsCompletedSuccessfully;
        }
    }

    /// <summary>
    /// An incoming message could not be fully processed: its handler threw, or the reply could not be sent.
    /// <see cref="ReceiveAsync"/> never throws for these, so this is the place to log them.
    /// </summary>
    public event EventHandler<BridgeReceiveFailedEventArgs>? ReceiveFailed;

    /// <summary>The other side connected — the first time, or again after a page reload.</summary>
    public event EventHandler? Connected;

    /// <summary>Delivers a serialized message to the other side.</summary>
    protected abstract Task SendAsync(string json, CancellationToken ct);

    /// <summary>
    /// Tells the other side this endpoint is ready to receive. Call once the transport is listening
    /// (host — after the WebView is initialized; page — after subscribing to host messages).
    /// Safe to call again: the other side answers without treating it as a restart.
    /// </summary>
    public Task AnnounceAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return SendMessageAsync(SessionMessage(BridgeProtocol.HelloType), ct);
    }

    /// <summary>Completes once the other side has connected.</summary>
    public Task WhenConnectedAsync(CancellationToken ct = default)
    {
        Task connected;
        lock (_connectionGate)
            connected = _connected.Task;
        return connected.WaitAsync(ct);
    }

    /// <summary>
    /// Forgets the other side, e.g. when the host starts navigating away. Pending requests fail at once
    /// instead of waiting for replies that will never come; the next announcement connects again.
    /// </summary>
    public void ResetConnection()
    {
        lock (_connectionGate)
        {
            if (_disposed)
                return;
            _peerSession = null;
            if (_connected.Task.IsCompleted)
                _connected = NewConnectionSource();
        }
        FailPending(new BridgeException("The other side disconnected."));
    }

    /// <inheritdoc/>
    public Task PostAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var message = BridgeProtocol.Notification(RequireUserType(type), payload);
        return AwaitConnection ? PostWhenConnectedAsync(message, ct) : SendMessageAsync(message, ct);
    }

    /// <inheritdoc/>
    public async Task<TResponse?> RequestAsync<TResponse>(string type, object? payload = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var request = BridgeProtocol.Request(RequireUserType(type), payload);
        var started = Stopwatch.GetTimestamp();
        if (AwaitConnection)
            await WaitForConnectionAsync(type, RequestTimeout, ct).ConfigureAwait(false);

        var reply = new TaskCompletionSource<BridgeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id!] = reply;
        try
        {
            // Dispose may have run between the first check and the registration — the request would hang otherwise.
            ThrowIfDisposed();
            await SendMessageAsync(request, ct).ConfigureAwait(false);
            BridgeMessage answer;
            try
            {
                answer = await reply.Task.WaitAsync(Remaining(started), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Bridge request '{type}' got no reply within {RequestTimeout}.");
            }
            if (answer.Error is not null)
                throw new BridgeRequestException(type, answer.Error);
            return answer.PayloadAs<TResponse>();
        }
        finally
        {
            _pending.TryRemove(request.Id!, out _);
        }
    }

    /// <inheritdoc/>
    public IDisposable On(string type, Func<BridgeMessage, CancellationToken, Task<object?>> handler)
    {
        ThrowIfDisposed();
        RequireUserType(type);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[type] = handler;
        return new Registration(_handlers, type, handler);
    }

    /// <summary>
    /// Processes one incoming raw message. Returns <c>false</c> when the message is not for this endpoint
    /// (not a bridge message, or a notification without a handler) — the transport may handle it itself,
    /// e.g. legacy 1.0 <c>openForm</c> messages. Requests without a handler get an error reply.
    /// Accepts both a JSON object and a JSON string holding one, so a WebView2 host can pass
    /// <c>WebMessageAsJson</c> whether the page posted an object or a string.
    /// The host should verify the message origin before calling this.
    /// <para>Never throws for a failing handler or reply — those go to <see cref="ReceiveFailed"/>, so it is
    /// safe to await from an <c>async void</c> event handler such as WebView2 <c>WebMessageReceived</c>.</para>
    /// </summary>
    public async Task<bool> ReceiveAsync(string json, CancellationToken ct = default)
    {
        if (_disposed)
            return false;
        var message = BridgeProtocol.TryParse(json);
        if (message is null)
            return false;

        if (message.IsReply)
        {
            if (_pending.TryGetValue(message.ReplyTo!, out var reply))
                reply.TrySetResult(message);
            return true;
        }

        if (message.Type is not null && BridgeProtocol.IsReserved(message.Type))
        {
            await HandleControlAsync(message, ct).ConfigureAwait(false);
            return true;
        }

        if (message.Type is null || !_handlers.TryGetValue(message.Type, out var handler))
        {
            if (!message.IsRequest)
                return false;
            await TrySendReplyAsync(message, BridgeProtocol.Failure(message, $"No handler for '{message.Type}'."), ct).ConfigureAwait(false);
            return true;
        }

        if (!message.IsRequest)
        {
            try
            {
                await handler(message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnReceiveFailed(message, ex);
            }
            return true;
        }

        BridgeMessage answer;
        try
        {
            answer = BridgeProtocol.Reply(message, await handler(message, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            OnReceiveFailed(message, ex);
            answer = BridgeProtocol.Failure(message, FormatError(message, ex));
        }
        await TrySendReplyAsync(message, answer, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Text of the error reply when a request handler throws. By default only a <see cref="BridgeException"/>
    /// passes its message through; anything else becomes a generic failure, because the other side is untrusted.
    /// </summary>
    protected virtual string FormatError(BridgeMessage request, Exception exception) =>
        exception is BridgeException ? exception.Message : $"Request '{request.Type}' failed.";

    /// <summary>Fails pending requests and drops handlers.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Fails pending requests and drops handlers.</summary>
    protected virtual void Dispose(bool disposing)
    {
        lock (_connectionGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connected.TrySetException(new ObjectDisposedException(GetType().Name)))
                _ = _connected.Task.Exception;
        }
        FailPending(new ObjectDisposedException(GetType().Name));
        _pending.Clear();
        _handlers.Clear();
    }

    private async Task HandleControlAsync(BridgeMessage message, CancellationToken ct)
    {
        var isHello = message.Type == BridgeProtocol.HelloType;
        if (!isHello && message.Type != BridgeProtocol.WelcomeType)
            return;

        var peer = ReadSession(message);
        bool changed, restarted;
        lock (_connectionGate)
        {
            if (_disposed)
                return;
            var wasConnected = _connected.Task.IsCompletedSuccessfully;
            changed = !wasConnected || !string.Equals(_peerSession, peer, StringComparison.Ordinal);
            restarted = wasConnected && changed;
            _peerSession = peer;
            _connected.TrySetResult();
        }

        if (restarted)
            FailPending(new BridgeException("The other side restarted."));
        if (changed)
            OnConnected();
        if (isHello)
            await TrySendReplyAsync(message, SessionMessage(BridgeProtocol.WelcomeType), ct).ConfigureAwait(false);
    }

    private async Task PostWhenConnectedAsync(BridgeMessage message, CancellationToken ct)
    {
        await WaitForConnectionAsync(message.Type!, RequestTimeout, ct).ConfigureAwait(false);
        await SendMessageAsync(message, ct).ConfigureAwait(false);
    }

    private async Task WaitForConnectionAsync(string type, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await WhenConnectedAsync(ct).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Bridge message '{type}': the other side did not connect within {timeout}.");
        }
    }

    private TimeSpan Remaining(long started)
    {
        if (RequestTimeout == Timeout.InfiniteTimeSpan)
            return RequestTimeout;
        var left = RequestTimeout - Stopwatch.GetElapsedTime(started);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void FailPending(Exception exception)
    {
        foreach (var reply in _pending.Values)
            reply.TrySetException(exception);
    }

    private BridgeMessage SessionMessage(string type) =>
        BridgeProtocol.Notification(type, new SessionPayload(_session));

    private static string? ReadSession(BridgeMessage message)
    {
        try
        {
            return message.PayloadAs<SessionPayload>()?.Session;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string RequireUserType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Message type must not be empty.", nameof(type));
        if (BridgeProtocol.IsReserved(type))
            throw new ArgumentException($"Message types starting with '{BridgeProtocol.ReservedPrefix}' are reserved.", nameof(type));
        return type;
    }

    private static TaskCompletionSource NewConnectionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task SendMessageAsync(BridgeMessage message, CancellationToken ct) =>
        SendAsync(BridgeProtocol.Serialize(message), ct);

    private async Task TrySendReplyAsync(BridgeMessage request, BridgeMessage answer, CancellationToken ct)
    {
        try
        {
            await SendMessageAsync(answer, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnReceiveFailed(request, ex);
        }
    }

    private void OnReceiveFailed(BridgeMessage message, Exception exception)
    {
        try
        {
            ReceiveFailed?.Invoke(this, new BridgeReceiveFailedEventArgs(message, exception));
        }
        catch
        {
            // A faulty subscriber must not break the transport.
        }
    }

    private void OnConnected()
    {
        try
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A faulty subscriber must not break the transport.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal sealed record SessionPayload(string? Session);

    private sealed class Registration(
        ConcurrentDictionary<string, Func<BridgeMessage, CancellationToken, Task<object?>>> handlers,
        string type,
        Func<BridgeMessage, CancellationToken, Task<object?>> handler) : IDisposable
    {
        // Removes only its own handler: a newer registration of the same type stays.
        public void Dispose() => handlers.TryRemove(new KeyValuePair<string, Func<BridgeMessage, CancellationToken, Task<object?>>>(type, handler));
    }
}
