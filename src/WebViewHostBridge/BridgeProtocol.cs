using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebViewHostBridge;

/// <summary>Builds, serializes and parses <see cref="BridgeMessage"/>s.</summary>
public static class BridgeProtocol
{
    /// <summary>Prefix of the bridge's own message types; applications cannot use it.</summary>
    public const string ReservedPrefix = "$bridge.";

    /// <summary>Announcement of an endpoint that is ready to receive; payload <c>{ "session": "..." }</c>.</summary>
    public const string HelloType = ReservedPrefix + "hello";

    /// <summary>Answer to <see cref="HelloType"/>; payload <c>{ "session": "..." }</c>.</summary>
    public const string WelcomeType = ReservedPrefix + "welcome";

    /// <summary>True for the bridge's own message types.</summary>
    public static bool IsReserved(string type) => type.StartsWith(ReservedPrefix, StringComparison.Ordinal);

    /// <summary>camelCase, nulls omitted — the wire format of the bridge.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Fire-and-forget message.</summary>
    public static BridgeMessage Notification(string type, object? payload = null) =>
        new() { Type = RequireType(type), Payload = ToElement(payload) };

    /// <summary>Message with a fresh id that expects a reply.</summary>
    public static BridgeMessage Request(string type, object? payload = null) =>
        new() { Type = RequireType(type), Payload = ToElement(payload), Id = Guid.NewGuid().ToString("N") };

    /// <summary>Successful answer to <paramref name="request"/>.</summary>
    public static BridgeMessage Reply(BridgeMessage request, object? payload = null) =>
        new() { Type = request.Type, ReplyTo = RequireId(request), Payload = ToElement(payload) };

    /// <summary>Error answer to <paramref name="request"/>.</summary>
    public static BridgeMessage Failure(BridgeMessage request, string error) =>
        new() { Type = request.Type, ReplyTo = RequireId(request), Error = string.IsNullOrWhiteSpace(error) ? "Request failed." : error };

    /// <summary>Wire representation of <paramref name="message"/>.</summary>
    public static string Serialize(BridgeMessage message) => JsonSerializer.Serialize(message, JsonOptions);

    /// <summary>
    /// Parses a raw message; null when it is not a JSON object with a <c>type</c> or <c>replyTo</c>.
    /// A JSON string holding such an object is accepted too (WebView2 <c>WebMessageAsJson</c> of a posted string).
    /// Messages of the flat 1.0 shape (e.g. <c>{ "type": "openForm", "formName": ... }</c>) parse too —
    /// their extra fields land in <see cref="BridgeMessage.ExtensionData"/>.
    /// </summary>
    public static BridgeMessage? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return TryParseObject(doc.RootElement.GetString());
            return FromObject(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BridgeMessage? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromObject(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BridgeMessage? FromObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var message = root.Deserialize<BridgeMessage>(JsonOptions);
        return message is { Type: not null } or { ReplyTo: not null } ? message : null;
    }

    private static JsonElement? ToElement(object? payload) => payload switch
    {
        null => null,
        JsonElement element => element,
        _ => JsonSerializer.SerializeToElement(payload, JsonOptions)
    };

    private static string RequireType(string type) =>
        string.IsNullOrWhiteSpace(type) ? throw new ArgumentException("Message type must not be empty.", nameof(type)) : type;

    private static string RequireId(BridgeMessage request) =>
        request.Id ?? throw new ArgumentException("Only requests (messages with an id) can be answered.", nameof(request));
}
