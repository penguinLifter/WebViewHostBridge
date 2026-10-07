namespace WebViewHostBridge;

/// <summary>
/// Lets the host ask the embedded page about unsaved changes before it closes the browser view.
/// Implemented on the host over the browser's script API (WebView2 <c>ExecuteScriptAsync</c> /
/// DevTools <c>Runtime.evaluate</c>, CefSharp <c>EvaluateScriptAsync</c>).
///
/// <para>Typical host flow on window closing:</para>
/// <list type="number">
///   <item><c>await guard.HasUnsavedChangesAsync()</c></item>
///   <item>If <c>true</c> — ask the user: Save / Discard / Cancel.</item>
///   <item>On Save → <c>await guard.RequestSaveAsync()</c>; keep the window open if <c>!Success</c>.</item>
///   <item>On Cancel → cancel the close.</item>
/// </list>
/// </summary>
public interface IHostCloseGuard
{
    /// <summary>Returns true if the page reports unsaved changes.</summary>
    Task<bool> HasUnsavedChangesAsync(CancellationToken ct = default);

    /// <summary>Asks the page to save its pending changes.</summary>
    Task<SaveAttemptResult> RequestSaveAsync(CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IHostCloseGuard.RequestSaveAsync"/>.</summary>
/// <param name="Success">True if everything was saved.</param>
/// <param name="ErrorMessage">Reason of the failure, suitable as a hint in the host dialog.</param>
public sealed record SaveAttemptResult(bool Success, string? ErrorMessage = null);
