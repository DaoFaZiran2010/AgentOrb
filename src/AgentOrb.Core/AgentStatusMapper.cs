namespace AgentOrb.Core;

public static class AgentStatusMapper
{
    public static AgentStatusEvent FromToolCall(
        string toolName,
        string agentId = "unknown",
        string agentName = "Agent",
        string? sessionId = null)
    {
        var name = toolName.Trim().ToLowerInvariant();
        var (state, message) = name switch
        {
            "seat_screenshot" or "seat_observe" or "seat_windows" or "seat_window" or
            "seat_element" or "seat_wait" or "seat_capabilities" or "screenshot" or
            "computer_screenshot" or "computer_observe" =>
                (AgentState.Observing, "正在读取 AI 桌面"),

            "seat_click" or "seat_move" or "seat_drag" or "click" or "computer_click" =>
                (AgentState.Clicking, name.Contains("drag", StringComparison.Ordinal)
                    ? "正在拖动鼠标"
                    : "正在点击 AI 桌面"),

            "seat_type" or "seat_key" or "type" or "keypress" or "computer_type" =>
                (AgentState.Typing, name.Contains("key", StringComparison.Ordinal)
                    ? "正在发送按键"
                    : "正在输入文字"),

            "seat_scroll" or "scroll" or "computer_scroll" =>
                (AgentState.Scrolling, "正在滚动页面"),

            "seat_run" or "seat_browser" or "seat_exec" or "seat_job" or
            "steam_launch" or "android_emulator" or "android_studio" or
            "launch" or "app_launch" =>
                (AgentState.Launching, "正在打开应用或运行命令"),

            "seat_start" => (AgentState.Starting, "正在启动 AI 桌面"),
            "seat_stop" => (AgentState.Stopping, "正在停止 AI 桌面"),
            "seat_show" => (AgentState.Idle, "正在打开 AI 桌面查看器"),
            "seat_hide" => (AgentState.Idle, "正在隐藏 AI 桌面查看器"),
            "seat_status" or "desktop_status" => (AgentState.Observing, "正在检查 AI 桌面状态"),

            "seat_lease" or "desktop_lease" =>
                (AgentState.Waiting, "正在协调 AI 桌面使用权"),

            "seat_audio_listen" or "seat_audio_status" =>
                (AgentState.Observing, "正在读取 AI 桌面音频"),

            _ => (AgentState.Waiting, "Agent 正在执行桌面任务")
        };

        return AgentStatusEvent.Create(state, message, toolName, agentId, agentName, sessionId);
    }

    public static AgentStatusEvent FromToolResult(
        string toolName,
        bool isError,
        string agentId = "unknown",
        string agentName = "Agent",
        string? sessionId = null) =>
        isError
            ? AgentStatusEvent.Create(AgentState.Error, "桌面工具执行失败", toolName, agentId, agentName, sessionId)
            : AgentStatusEvent.Create(AgentState.Idle, "等待 Agent 下一步", toolName, agentId, agentName, sessionId);
}
