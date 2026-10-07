namespace WebViewHostBridge;

/// <summary>
/// Opens host (desktop) windows from the embedded web page.
/// Implemented on the web side as a bridge to the host (e.g. <c>chrome.webview.postMessage</c>)
/// and handled by the host, which maps <paramref name="formName"/> to its own window.
/// </summary>
public interface IFormOpener
{
    /// <summary>Opens a host window by logical name.</summary>
    /// <param name="formName">Logical window name understood by the host.</param>
    /// <param name="parameters">Arbitrary JSON-serializable parameters; unknown keys are the host's to ignore.</param>
    void OpenForm(string formName, IDictionary<string, object?> parameters);
}
