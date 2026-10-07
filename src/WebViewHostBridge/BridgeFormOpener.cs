using System.Text.Json;

namespace WebViewHostBridge;

/// <summary>
/// <see cref="IFormOpener"/> over the bridge: the page posts an <c>openForm</c> notification,
/// the host opens its window.
/// <code>
/// // page
/// IFormOpener opener = new BridgeFormOpener(pageBridge);
/// opener.OpenForm("AgreementCard", new Dictionary&lt;string, object?&gt; { ["id"] = 42 });
///
/// // host
/// BridgeFormOpener.Register(hostBridge, (request, ct) => { OpenWindow(request.FormName, request.Parameters); return Task.CompletedTask; });
/// </code>
/// </summary>
public sealed class BridgeFormOpener(IBridgeEndpoint host) : IFormOpener
{
    /// <summary>Notification type; payload <c>{ "formName": "...", "parameters": { ... } }</c>.</summary>
    public const string OpenFormType = "openForm";

    private readonly IBridgeEndpoint _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>Raised when a fire-and-forget <see cref="OpenForm"/> could not be sent.</summary>
    public event EventHandler<Exception>? SendFailed;

    /// <summary>Fire-and-forget; a send failure goes to <see cref="SendFailed"/>.</summary>
    public void OpenForm(string formName, IDictionary<string, object?> parameters)
    {
        var sending = OpenFormAsync(formName, parameters);
        if (!sending.IsCompletedSuccessfully)
            sending.ContinueWith(
                t => SendFailed?.Invoke(this, t.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>Asks the host to open <paramref name="formName"/>.</summary>
    public Task OpenFormAsync(string formName, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(formName))
            throw new ArgumentException("Form name must not be empty.", nameof(formName));
        return _host.PostAsync(OpenFormType, new { formName, parameters = parameters ?? new Dictionary<string, object?>() }, ct);
    }

    /// <summary>
    /// Host side: handles <c>openForm</c> — both this format and the flat 1.0 one
    /// (<c>{ "type": "openForm", "formName": ..., "parameters": ... }</c>). Dispose to stop.
    /// The page is untrusted: whitelist the forms and parameters <paramref name="open"/> accepts.
    /// </summary>
    public static IDisposable Register(IBridgeEndpoint host, Func<OpenFormRequest, CancellationToken, Task> open)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(open);
        return host.On(OpenFormType, async (message, ct) =>
        {
            var request = OpenFormRequest.From(message) ?? throw new BridgeException("Malformed openForm message.");
            await open(request, ct).ConfigureAwait(false);
            return null;
        });
    }
}

/// <summary>A page's request to open a host window.</summary>
/// <param name="FormName">Logical window name.</param>
/// <param name="Parameters">Raw JSON parameters as sent by the page.</param>
public sealed record OpenFormRequest(string FormName, IReadOnlyDictionary<string, JsonElement> Parameters)
{
    /// <summary>Reads an <c>openForm</c> message of either format; null when it has no form name.</summary>
    public static OpenFormRequest? From(BridgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        JsonElement? formName = null, parameters = null;

        if (message.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            if (payload.TryGetProperty("formName", out var name))
                formName = name;
            if (payload.TryGetProperty("parameters", out var args))
                parameters = args;
        }
        else if (message.ExtensionData is { } flat)
        {
            if (flat.TryGetValue("formName", out var name))
                formName = name;
            if (flat.TryGetValue("parameters", out var args))
                parameters = args;
        }

        if (formName is not { ValueKind: JsonValueKind.String } nameElement || string.IsNullOrWhiteSpace(nameElement.GetString()))
            return null;

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (parameters is { ValueKind: JsonValueKind.Object } obj)
            foreach (var property in obj.EnumerateObject())
                values[property.Name] = property.Value.Clone();

        return new OpenFormRequest(nameElement.GetString()!, values);
    }
}
