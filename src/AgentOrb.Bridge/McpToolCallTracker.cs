using System.Collections.Concurrent;
using System.Text.Json;
using AgentOrb.Core;

namespace AgentOrb.Bridge;

internal sealed class McpToolCallTracker : IDisposable
{
    private static readonly TimeSpan DefaultPendingCallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly string _agentId;
    private readonly string _agentName;
    private readonly IAgentStatusSink _statusSink;
    private readonly TimeSpan _pendingCallTimeout;
    private readonly ConcurrentDictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);
    private readonly Timer? _cleanupTimer;
    private int _disposed;

    public McpToolCallTracker(
        string agentId,
        string agentName,
        IAgentStatusSink statusSink,
        TimeSpan? pendingCallTimeout = null,
        bool enableCleanupTimer = true)
    {
        _agentId = agentId;
        _agentName = agentName;
        _statusSink = statusSink ?? throw new ArgumentNullException(nameof(statusSink));
        _pendingCallTimeout = pendingCallTimeout ?? DefaultPendingCallTimeout;
        if (_pendingCallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingCallTimeout));
        }

        if (enableCleanupTimer)
        {
            _cleanupTimer = new Timer(
                _ => ExpirePendingCalls(DateTimeOffset.UtcNow),
                state: null,
                CleanupInterval,
                CleanupInterval);
        }
    }

    internal int PendingCallCount => _pendingCalls.Count;

    public void OnClientMessage(string line)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ExpirePendingCalls(DateTimeOffset.UtcNow);
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || method.GetString() != "tools/call"
                || !root.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var toolName = name.GetString();
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return;
            }

            if (root.TryGetProperty("id", out var id) && TryGetIdKey(id, out var idKey))
            {
                _pendingCalls[idKey] = new PendingCall(toolName, DateTimeOffset.UtcNow);
            }

            // Only the tool name crosses into status events. Arguments are never retained,
            // logged, or published.
            Publish(AgentStatusMapper.FromToolCall(toolName, _agentId, _agentName));
        }
        catch (JsonException)
        {
            // Forwarding is independent of inspection; malformed JSON is passed through as-is.
        }
    }

    public void OnServerMessage(string line)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ExpirePendingCalls(DateTimeOffset.UtcNow);
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("method", out _)
                || !root.TryGetProperty("id", out var id)
                || !TryGetIdKey(id, out var idKey)
                || !_pendingCalls.TryRemove(idKey, out var pendingCall))
            {
                return;
            }

            var isError = root.TryGetProperty("error", out var error)
                && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

            if (root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("isError", out var toolError))
            {
                if (toolError.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isError |= toolError.GetBoolean();
                }
            }

            // Result content can contain typed text, screenshots, or secrets. Only the fixed
            // Core status and the already-known tool name are published.
            Publish(AgentStatusMapper.FromToolResult(pendingCall.ToolName, isError, _agentId, _agentName));
        }
        catch (JsonException)
        {
            // Never include the malformed payload in logs.
        }
    }

    internal void ExpirePendingCalls(DateTimeOffset utcNow)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        foreach (var pair in _pendingCalls)
        {
            if (utcNow - pair.Value.CreatedAt < _pendingCallTimeout)
            {
                continue;
            }

            if (_pendingCalls.TryRemove(new KeyValuePair<string, PendingCall>(pair.Key, pair.Value)))
            {
                Publish(AgentStatusEvent.Create(
                    AgentState.Error,
                    "桌面工具执行超时",
                    pair.Value.ToolName,
                    _agentId,
                    _agentName));
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cleanupTimer?.Dispose();
        _pendingCalls.Clear();
    }

    private void Publish(AgentStatusEvent status)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ValueTask publication;
        try
        {
            publication = _statusSink.PublishAsync(status);
        }
        catch (Exception)
        {
            return;
        }

        if (!publication.IsCompletedSuccessfully)
        {
            _ = ObservePublicationAsync(publication);
        }
    }

    private static async Task ObservePublicationAsync(ValueTask publication)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Status publication must never terminate either protocol direction.
        }
    }

    private static bool TryGetIdKey(JsonElement id, out string key)
    {
        switch (id.ValueKind)
        {
            case JsonValueKind.String:
                key = "string:" + id.GetString();
                return true;
            case JsonValueKind.Number:
                key = "number:" + id.GetRawText();
                return true;
            case JsonValueKind.Null:
                key = "null";
                return true;
            default:
                key = string.Empty;
                return false;
        }
    }

    private readonly record struct PendingCall(string ToolName, DateTimeOffset CreatedAt);
}
