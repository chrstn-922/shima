using System.Text.Json.Serialization;

namespace Shima.AgentBridge;

public sealed record AgentBridgeRequest
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("cwd")]
    public string Cwd { get; init; } = string.Empty;

    [JsonPropertyName("hookEventName")]
    public required string HookEventName { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }

    [JsonPropertyName("toolInputSummary")]
    public string? ToolInputSummary { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("notificationType")]
    public string? NotificationType { get; init; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("expectsDecision")]
    public bool ExpectsDecision { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed record AgentBridgeResponse
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("decision")]
    public string? Decision { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record AgentBridgeAck
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "decision_emitted";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }
}

public sealed record AgentBridgeOptions(
    string PipeName,
    int ConnectTimeoutMs,
    int ResponseTimeoutMs);
