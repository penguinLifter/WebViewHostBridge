namespace WebViewHostBridge;

/// <summary>Typed handler registration on top of <see cref="IBridgeEndpoint.On"/>.</summary>
public static class BridgeEndpointExtensions
{
    /// <summary>Handles notifications of <paramref name="type"/> with a typed payload.</summary>
    public static IDisposable OnNotification<TPayload>(
        this IBridgeEndpoint endpoint, string type, Func<TPayload?, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);
        return endpoint.On(type, async (message, ct) =>
        {
            await handler(message.PayloadAs<TPayload>(), ct).ConfigureAwait(false);
            return null;
        });
    }

    /// <summary>Handles notifications of <paramref name="type"/> with a typed payload, synchronously.</summary>
    public static IDisposable OnNotification<TPayload>(
        this IBridgeEndpoint endpoint, string type, Action<TPayload?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoint.OnNotification<TPayload>(type, (payload, _) =>
        {
            handler(payload);
            return Task.CompletedTask;
        });
    }

    /// <summary>Answers requests of <paramref name="type"/>: typed payload in, typed reply out.</summary>
    public static IDisposable OnRequest<TRequest, TResponse>(
        this IBridgeEndpoint endpoint, string type, Func<TRequest?, CancellationToken, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);
        return endpoint.On(type, async (message, ct) =>
            await handler(message.PayloadAs<TRequest>(), ct).ConfigureAwait(false));
    }

    /// <summary>Answers requests of <paramref name="type"/> that carry no payload.</summary>
    public static IDisposable OnRequest<TResponse>(
        this IBridgeEndpoint endpoint, string type, Func<CancellationToken, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);
        return endpoint.On(type, async (_, ct) => await handler(ct).ConfigureAwait(false));
    }
}
