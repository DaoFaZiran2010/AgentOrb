using System.Text.Json;
using AgentOrb.Core;

namespace AgentOrb.Anode;

public sealed class AnodeDesktopController : IDesktopController
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessExecutor _processExecutor;
    private readonly string? _configuredAnodePath;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _startTimeout;

    public AnodeDesktopController()
        : this(new SystemProcessExecutor())
    {
    }

    public AnodeDesktopController(
        IProcessExecutor processExecutor,
        string? anodePath = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? startTimeout = null)
    {
        _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
        _configuredAnodePath = anodePath;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _startTimeout = startTimeout ?? DefaultStartTimeout;
        if (_commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }
        if (_startTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startTimeout));
        }
    }

    public async Task<DesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var executable = AnodeExecutableLocator.Resolve(_configuredAnodePath);
        if (executable is null)
        {
            return DesktopStatus.Unavailable("未找到 Anode，请设置 ANODE_EXE 或安装到默认位置。");
        }

        var result = await _processExecutor.ExecuteAsync(
            executable,
            new[] { "status", "--json" },
            _commandTimeout,
            cancellationToken).ConfigureAwait(false);

        var hasValidJson = TryParseStatus(result.StandardOutput, out var status);
        var isStoppedExit = result.ExitCode == 1
            && hasValidJson
            && string.Equals(status.State, "stopped", StringComparison.OrdinalIgnoreCase);

        if ((result.ExitCode == 0 || isStoppedExit) && hasValidJson)
        {
            return status;
        }

        // Never include raw stdout/stderr: CLI output can contain machine or user details.
        return new DesktopStatus
        {
            IsInstalled = true,
            State = "error",
            Summary = result.ExitCode == 0 || isStoppedExit
                ? "Anode status 返回了无法解析的 JSON。"
                : $"Anode status 执行失败（退出码 {result.ExitCode}）。"
        };
    }

    public Task StartHiddenAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync("start", new[] { "start", "--hidden" }, _startTimeout, cancellationToken);

    public Task ShowAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync("show", new[] { "show" }, _commandTimeout, cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync("hide", new[] { "hide" }, _commandTimeout, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync("kill", new[] { "kill" }, _commandTimeout, cancellationToken);

    private async Task RunCommandAsync(
        string operation,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var executable = AnodeExecutableLocator.Resolve(_configuredAnodePath)
            ?? throw new FileNotFoundException("未找到 Anode 可执行文件。");

        var result = await _processExecutor.ExecuteAsync(
            executable,
            arguments,
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Anode {operation} 执行失败（退出码 {result.ExitCode}）。");
        }
    }

    private static bool TryParseStatus(string json, out DesktopStatus status)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                status = null!;
                return false;
            }

            status = new DesktopStatus
            {
                IsInstalled = true,
                IsRunning = ReadBoolean(root, "daemonRunning") ?? false,
                IsViewerVisible = ReadBoolean(root, "viewerVisible") ?? false,
                IsViewOnly = ReadBoolean(root, "viewOnly") ?? true,
                State = ReadString(root, "state") ?? "unknown",
                Summary = ReadString(root, "summary") ?? string.Empty
            };
            return true;
        }
        catch (JsonException)
        {
            status = null!;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
