using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shima.AgentBridge;

/// <summary>
/// Standalone Claude Code lifecycle hook bridge communicating over user-ACL Named Pipe
/// (\\.\pipe\shima-agent-bridge) with mandatory fail-open empty-stdout exit 0 fallback.
/// </summary>
public static class AgentBridgeRunner
{
    public const string DefaultPipeName = "shima-agent-bridge";
    public const int DefaultConnectTimeoutMs = 15;
    public const int DefaultResponseTimeoutMs = 120_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static AgentBridgeOptions ParseArgs(string[] args)
    {
        var pipeName = Environment.GetEnvironmentVariable("SHIMA_AGENT_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            pipeName = DefaultPipeName;
        }

        var connectTimeoutMs = DefaultConnectTimeoutMs;
        var responseTimeoutMs = DefaultResponseTimeoutMs;
        var rawTimeoutEnv = Environment.GetEnvironmentVariable("SHIMA_AGENT_TIMEOUT_MS");
        if (int.TryParse(rawTimeoutEnv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEnvTimeout))
        {
            responseTimeoutMs = Math.Max(0, parsedEnvTimeout);
        }

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (string.Equals(token, "--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var next = args[++i];
                if (!string.IsNullOrWhiteSpace(next))
                {
                    pipeName = next.Trim();
                }
            }
            else if (string.Equals(token, "--timeout-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    responseTimeoutMs = Math.Max(0, ms);
                }
            }
            else if (string.Equals(token, "--connect-timeout-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    connectTimeoutMs = Math.Clamp(ms, 1, 10_000);
                }
            }
        }

        return new AgentBridgeOptions(pipeName, connectTimeoutMs, responseTimeoutMs);
    }

    public static async Task<int> RunAsync(
        TextReader stdin,
        TextWriter stdout,
        string pipeName = DefaultPipeName,
        int connectTimeoutMs = DefaultConnectTimeoutMs,
        int responseTimeoutMs = DefaultResponseTimeoutMs,
        int? processIdOverride = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);

        try
        {
            var rawInput = await stdin.ReadToEndAsync(ct).ConfigureAwait(false);
            if (!TryBuildRequest(rawInput, processIdOverride ?? Environment.ProcessId, out var request)
                || request is null)
            {
                return 0;
            }

            if (request.ExpectsDecision && responseTimeoutMs <= 0)
            {
                return 0;
            }

            using var pipe = new NamedPipeClientStream(
                ".",
                string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            var clampedConnectMs = Math.Clamp(connectTimeoutMs, 1, 10_000);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(clampedConnectMs);
                await pipe.ConnectAsync(clampedConnectMs, connectCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Fail-open when Shima is closed or unreachable.
                return 0;
            }

            using var writer = new StreamWriter(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await writer.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);

            if (!request.ExpectsDecision)
            {
                using var ackWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ackWaitCts.CancelAfter(500);
                try
                {
                    await reader.ReadLineAsync(ackWaitCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Telemetry hooks always fail open.
                }

                return 0;
            }

            string? responseLine;
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(responseTimeoutMs);
                responseLine = await reader.ReadLineAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Fail-open when timed out or interrupted.
                return 0;
            }

            if (string.IsNullOrWhiteSpace(responseLine))
            {
                // Pipe closed mid-prompt -> fail-open passthrough.
                return 0;
            }

            AgentBridgeResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<AgentBridgeResponse>(responseLine, JsonOptions);
            }
            catch
            {
                return 0;
            }

            if (response is null
                || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                return 0;
            }

            var normalizedDecision = response.Decision?.Trim().ToLowerInvariant();
            if (normalizedDecision is not ("allow" or "deny"))
            {
                return 0;
            }

            var claudeOutputJson = BuildClaudeHookOutputJson(
                request.HookEventName,
                normalizedDecision,
                response.Reason);

            await stdout.WriteLineAsync(claudeOutputJson.AsMemory(), ct).ConfigureAwait(false);
            await stdout.FlushAsync(ct).ConfigureAwait(false);

            try
            {
                var ack = new AgentBridgeAck
                {
                    RequestId = request.RequestId,
                    Decision = normalizedDecision
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(ack, JsonOptions).AsMemory(), ct).ConfigureAwait(false);
            }
            catch
            {
                // stdout has already emitted decision cleanly.
            }

            return 0;
        }
        catch
        {
            // Strict fail-open invariant: never crash or block terminal prompt.
            return 0;
        }
    }

    public static bool TryBuildRequest(string? rawInput, int processId, out AgentBridgeRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawInput);
        }
        catch
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            var hookEventName = NormalizeHookEventName(GetStringProperty(root, "hook_event_name"));
            if (string.IsNullOrWhiteSpace(hookEventName))
            {
                return false;
            }

            var sessionId = GetStringProperty(root, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = "default";
            }

            var cwd = GetStringProperty(root, "cwd") ?? string.Empty;
            var toolName = GetStringProperty(root, "tool_name");
            var message = GetStringProperty(root, "message");
            var notificationType = GetStringProperty(root, "notification_type");
            var toolInputSummary = SummarizeToolInput(root);
            var expectsDecision = string.Equals(hookEventName, "PermissionRequest", StringComparison.Ordinal)
                || string.Equals(hookEventName, "PreToolUse", StringComparison.Ordinal);

            request = new AgentBridgeRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId.Trim(),
                Cwd = cwd,
                HookEventName = hookEventName,
                ToolName = toolName,
                ToolInputSummary = toolInputSummary,
                Message = message,
                NotificationType = notificationType,
                ProcessId = processId,
                ExpectsDecision = expectsDecision,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    public static string BuildClaudeHookOutputJson(string hookEventName, string decision, string? reason)
    {
        var hookSpecific = new JsonObject
        {
            ["hookEventName"] = hookEventName,
            ["permissionDecision"] = decision
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            hookSpecific["permissionDecisionReason"] = reason.Trim();
        }

        var root = new JsonObject
        {
            ["hookSpecificOutput"] = hookSpecific
        };

        return root.ToJsonString();
    }

    private static string? NormalizeHookEventName(string? rawEventName)
    {
        if (string.IsNullOrWhiteSpace(rawEventName))
        {
            return null;
        }

        return rawEventName.Trim() switch
        {
            var s when string.Equals(s, "PermissionRequest", StringComparison.OrdinalIgnoreCase) => "PermissionRequest",
            var s when string.Equals(s, "PreToolUse", StringComparison.OrdinalIgnoreCase) => "PreToolUse",
            var s when string.Equals(s, "Notification", StringComparison.OrdinalIgnoreCase) => "Notification",
            var s when string.Equals(s, "Stop", StringComparison.OrdinalIgnoreCase) => "Stop",
            var s when string.Equals(s, "SubagentStop", StringComparison.OrdinalIgnoreCase) => "SubagentStop",
            var s => s
        };
    }

    private static string? GetStringProperty(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return prop.GetString();
    }

    private static string? SummarizeToolInput(JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string? summary = null;
        if (input.ValueKind == JsonValueKind.Object)
        {
            summary = GetStringProperty(input, "command")
                ?? GetStringProperty(input, "file_path")
                ?? GetStringProperty(input, "path")
                ?? GetStringProperty(input, "description")
                ?? GetStringProperty(input, "url")
                ?? GetStringProperty(input, "pattern")
                ?? input.GetRawText();
        }
        else if (input.ValueKind == JsonValueKind.String)
        {
            summary = input.GetString();
        }
        else
        {
            summary = input.GetRawText();
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var compact = summary.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 240 ? compact : compact[..237] + "...";
    }
}
