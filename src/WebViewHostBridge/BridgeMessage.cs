using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebViewHostBridge;

/// <summary>
/// One message on the bridge. Wire format (camelCase JSON):
/// <c>{ "type": "...", "payload": ..., "id": "...", "replyTo": "...", "error": "..." }</c>.
/// <list type="bullet">
///   <item>Notification — <see cref="Type"/> only (plus optional payload).</item>
///   <item>Request — has <see cref="Id"/>; the other side answers with a reply.</item>
///   <item>Reply — has <see cref="ReplyTo"/> = request id, and either a payload or <see cref="Error"/>.</item>
/// </list>
/// </summary>
public sealed record BridgeMessage
{
    /// <summary>Message type chosen by the application, e.g. <c>orderCompleted</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Arbitrary JSON payload.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Set on requests that expect a reply.</summary>
    public string? Id { get; init; }

    /// <summary>Set on replies: id of the request being answered.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Set on failed replies: why the request could not be handled.</summary>
    public string? Error { get; init; }

    /// <summary>True for a request that expects a reply.</summary>
    [JsonIgnore]
    public bool IsRequest => Id is not null && ReplyTo is null;

    /// <summary>True for a reply to an earlier request.</summary>
    [JsonIgnore]
    public bool IsReply => ReplyTo is not null;

    /// <summary>Deserializes <see cref="Payload"/>; default when there is none.</summary>
    public T? PayloadAs<T>() =>
        Payload is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } payload
            ? payload.Deserialize<T>(BridgeProtocol.JsonOptions)
            : default;
}
