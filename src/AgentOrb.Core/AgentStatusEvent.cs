namespace AgentOrb.Core;

public sealed record AgentStatusEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public AgentState State { get; init; } = AgentState.Idle;
    public string Message { get; init; } = "等待 Agent 指令";
    public string? ToolName { get; init; }
    public string AgentId { get; init; } = "unknown";
    public string AgentName { get; init; } = "Agent";
    public string? SessionId { get; init; }

    public static AgentStatusEvent Create(
        AgentState state,
        string message,
        string? toolName = null,
        string agentId = "unknown",
        string agentName = "Agent",
        string? sessionId = null) => new()
    {
        State = state,
        Message = message,
        ToolName = toolName,
        AgentId = agentId,
        AgentName = agentName,
        SessionId = sessionId
    };
}
