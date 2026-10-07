using System.Collections.Concurrent;

namespace WebViewHostBridge;

/// <summary>
/// Transport-agnostic <see cref="IBridgeEndpoint"/>: request/reply correlation, timeouts and handler dispatch.
/// A transport derives from it, implements <see cref="SendAsync"/> and feeds every incoming raw message
/// to <see cref="ReceiveAsync"/>:
/// <list type="bullet">
///   <item>WebView2 host — <c>CoreWebView2.PostWebMessageAsString</c> / <c>WebMessageReceived</c>.</item>
///   <item>Blazor page — JS interop over <c>chrome.webview.postMessage</c> / <c>chrome.webview.addEventListener("message")</c>.</item>
/// </list>
/// </summary>
public abstract class BridgeEndpoint : IBridgeEndpoint, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<BridgeMessage, CancellationToken, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <summary>How long <see cref="RequestAsync{TResponse}"/> waits for a reply.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An incoming message could not be fully processed: its handler threw, or the reply could not be sent.
    /// <see cref="ReceiveAsync"/> never throws for these, so this is the place to log them.
    /// </summary>
    public event EventHandler<BridgeReceiveFailedEventArgs>? ReceiveFailed;

    /// <summary>Delivers a serialized message to the other side.</summary>
    protected abstract Task SendAsync(string json, CancellationToken ct);

    /// <inheritdoc/>
    public Task PostAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return SendMessageAsync(BridgeProtocol.Notification(type, payload), ct);
    }

    /// <inheritdoc/>
    public async Task<TResponse?> RequestAsync<TResponse>(string type, object? payload = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var request = BridgeProtocol.Request(type, payload);
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
                answer = await reply.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Message type must not be empty.", nameof(type));
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[type] = handler;
        return new Registration(_handlers, type, handler);
    }

    /// <summary>
    /// Processes one incoming raw message. Returns <c>false</c> when the message is not for this endpoint
    /// (not a bridge message, or a notification without a handler) — the transport may handle it itself,
    /// e.g. legacy 1.0 <c>openForm</c> messages. Requests without a handler get an error reply.
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
        if (_disposed)
            return;
        _disposed = true;
        foreach (var reply in _pending.Values)
            reply.TrySetException(new ObjectDisposedException(GetType().Name));
        _pending.Clear();
        _handlers.Clear();
    }

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Registration(
        ConcurrentDictionary<string, Func<BridgeMessage, CancellationToken, Task<object?>>> handlers,
        string type,
        Func<BridgeMessage, CancellationToken, Task<object?>> handler) : IDisposable
    {
        // Removes only its own handler: a newer registration of the same type stays.
        public void Dispose() => handlers.TryRemove(new KeyValuePair<string, Func<BridgeMessage, CancellationToken, Task<object?>>>(type, handler));
    }
}
