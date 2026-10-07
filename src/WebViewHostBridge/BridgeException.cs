namespace WebViewHostBridge;

/// <summary>
/// An error whose message is meant for the other side of the bridge. A request handler throws it to send
/// <see cref="Exception.Message"/> back as the error reply. Any other exception reaches the requester only
/// as a generic failure, so host internals (SQL errors, paths, stack details) do not leak to the page.
/// </summary>
public class BridgeException : Exception
{
    /// <summary>Creates the error with a message that is safe to show to the other side.</summary>
    public BridgeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the error with a message that is safe to show to the other side.</summary>
    public BridgeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The other side answered a request with an error.</summary>
public sealed class BridgeRequestException(string type, string error)
    : BridgeException($"Bridge request '{type}' failed: {error}")
{
    /// <summary>Type of the failed request.</summary>
    public string Type { get; } = type;

    /// <summary>Error reported by the other side.</summary>
    public string Error { get; } = error;
}

/// <summary>Data of <see cref="BridgeEndpoint.ReceiveFailed"/>.</summary>
/// <param name="message">The incoming message that could not be fully processed.</param>
/// <param name="exception">What went wrong: the handler's exception or the failure to send the reply.</param>
public sealed class BridgeReceiveFailedEventArgs(BridgeMessage message, Exception exception) : EventArgs
{
    /// <summary>The incoming message that could not be fully processed.</summary>
    public BridgeMessage Message { get; } = message;

    /// <summary>The handler's exception or the failure to send the reply.</summary>
    public Exception Exception { get; } = exception;
}
