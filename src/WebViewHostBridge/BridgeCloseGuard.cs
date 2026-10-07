namespace WebViewHostBridge;

/// <summary>
/// <see cref="IHostCloseGuard"/> over the bridge: the host asks, the page answers through its endpoint.
/// Replaces evaluating <c>window.hasUnsavedChanges()</c> / <c>window.requestSave()</c> by script, which in
/// WebView2 needs a DevTools call to await the returned Promise.
/// <code>
/// // page
/// BridgeCloseGuard.Register(pageBridge, ct => Task.FromResult(form.IsDirty), SaveAsync);
///
/// // host, in OnFormClosing
/// var guard = new BridgeCloseGuard(hostBridge);
/// if (await guard.HasUnsavedChangesAsync()) { ... }
/// </code>
/// </summary>
public sealed class BridgeCloseGuard(IBridgeEndpoint page) : IHostCloseGuard
{
    /// <summary>Request type: does the page have unsaved changes? Reply — <c>bool</c>.</summary>
    public const string HasUnsavedChangesType = "hasUnsavedChanges";

    /// <summary>Request type: save pending changes. Reply — <see cref="SaveAttemptResult"/>.</summary>
    public const string RequestSaveType = "requestSave";

    private readonly IBridgeEndpoint _page = page ?? throw new ArgumentNullException(nameof(page));

    /// <summary>
    /// Asks the page. Throws <see cref="BridgeRequestException"/> or <see cref="TimeoutException"/> when it
    /// does not answer — the host decides whether to close anyway.
    /// </summary>
    public async Task<bool> HasUnsavedChangesAsync(CancellationToken ct = default) =>
        await _page.RequestAsync<bool>(HasUnsavedChangesType, ct: ct).ConfigureAwait(false);

    /// <summary>Asks the page to save; an error reply becomes an unsuccessful result with its text.</summary>
    public async Task<SaveAttemptResult> RequestSaveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _page.RequestAsync<SaveAttemptResult>(RequestSaveType, ct: ct).ConfigureAwait(false)
                ?? new SaveAttemptResult(false, "The page returned no save result.");
        }
        catch (BridgeRequestException ex)
        {
            return new SaveAttemptResult(false, ex.Error);
        }
    }

    /// <summary>Page side: answers the host's close-guard requests. Dispose to stop.</summary>
    public static IDisposable Register(
        IBridgeEndpoint page,
        Func<CancellationToken, Task<bool>> hasUnsavedChanges,
        Func<CancellationToken, Task<SaveAttemptResult>> requestSave)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(hasUnsavedChanges);
        ArgumentNullException.ThrowIfNull(requestSave);
        var registrations = new[]
        {
            page.OnRequest(HasUnsavedChangesType, hasUnsavedChanges),
            page.OnRequest(RequestSaveType, requestSave)
        };
        return new CompositeDisposable(registrations);
    }

    private sealed class CompositeDisposable(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
