namespace WebViewHostBridge;

/// <summary>
/// One end of a two-way bridge between the desktop host and the embedded page.
/// Both sides use the same contract: the host talks to the page, the page talks to the host.
/// </summary>
public interface IBridgeEndpoint
{
    /// <summary>Sends a fire-and-forget notification.</summary>
    Task PostAsync(string type, object? payload = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a request and awaits the other side's reply.
    /// Throws <see cref="BridgeRequestException"/> when the other side reports an error
    /// and <see cref="TimeoutException"/> when no reply arrives in time.
    /// </summary>
    Task<TResponse?> RequestAsync<TResponse>(string type, object? payload = null, CancellationToken ct = default);

    /// <summary>
    /// Handles incoming messages of <paramref name="type"/>. For requests the returned value is sent back
    /// as the reply payload; a thrown exception becomes an error reply. One handler per type — a new
    /// registration replaces the previous one. Dispose the result to unregister.
    /// </summary>
    IDisposable On(string type, Func<BridgeMessage, CancellationToken, Task<object?>> handler);
}

/// <summary>The other side answered a request with an error.</summary>
public sealed class BridgeRequestException(string type, string error)
    : Exception($"Bridge request '{type}' failed: {error}")
{
    /// <summary>Type of the failed request.</summary>
    public string Type { get; } = type;

    /// <summary>Error reported by the other side.</summary>
    public string Error { get; } = error;
}
