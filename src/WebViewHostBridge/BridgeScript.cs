namespace WebViewHostBridge;

/// <summary>
/// The JS client of the bridge (<c>window.WebViewHostBridge</c>), wire-compatible with <see cref="BridgeEndpoint"/>.
/// The simplest way to give every page the client is to inject it from the WebView2 host:
/// <code>
/// await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript.Source);
/// </code>
/// Injecting only defines the client — nothing listens until the page calls
/// <c>WebViewHostBridge.create()</c> (plain JS) or <c>WebViewHostBridge.forward()</c> (Blazor).
/// </summary>
public static class BridgeScript
{
    /// <summary>Name of the embedded resource holding the script.</summary>
    public const string ResourceName = "WebViewHostBridge.webviewhostbridge.js";

    private static readonly Lazy<string> SourceText = new(Load);

    /// <summary>Script text.</summary>
    public static string Source => SourceText.Value;

    private static string Load()
    {
        using var stream = typeof(BridgeScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
