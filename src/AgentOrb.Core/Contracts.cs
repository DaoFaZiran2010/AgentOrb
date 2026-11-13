using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrb.Core;

public static class AgentOrbConstants
{
    public const string DefaultPipeName = "agentorb-status-v1";
}

public static class StatusEventJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(AgentStatusEvent value) => JsonSerializer.Serialize(value, Options);

    public static AgentStatusEvent? Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentStatusEvent>(json, Options);
}

public sealed record DesktopStatus
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public bool IsViewerVisible { get; init; }
    public bool IsViewOnly { get; init; } = true;
    public string State { get; init; } = "unknown";
    public string Summary { get; init; } = string.Empty;

    public static DesktopStatus Unavailable(string summary) => new()
    {
        IsInstalled = false,
        State = "unavailable",
        Summary = summary
    };
}

public interface IDesktopController
{
    Task<DesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task StartHiddenAsync(CancellationToken cancellationToken = default);
    Task ShowAsync(CancellationToken cancellationToken = default);
    Task HideAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IAgentStatusSink
{
    ValueTask PublishAsync(AgentStatusEvent status, CancellationToken cancellationToken = default);
}
