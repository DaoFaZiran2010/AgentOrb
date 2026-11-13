using AgentOrb.Core;

namespace AgentOrb.Desktop;

public sealed class DemoDesktopController : IDesktopController, IDisposable
{
    private MockDesktopWindow? _window;
    private bool _isRunning;
    private bool _disposed;

    public Task<DesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DesktopStatus
        {
            IsInstalled = true,
            IsRunning = _isRunning,
            IsViewerVisible = _window?.IsVisible == true,
            IsViewOnly = true,
            State = _isRunning ? "demo" : "stopped",
            Summary = _isRunning
                ? "演示模式 · 虚拟桌面 · 非真实 Anode"
                : "演示桌面已停止 · 右键选择启动桌面"
        });
    }

    public Task StartHiddenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        var window = EnsureWindow();
        _isRunning = true;
        if (window.IsVisible)
        {
            window.Hide();
        }

        return Task.CompletedTask;
    }

    public Task ShowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        var window = EnsureWindow();
        _isRunning = true;
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Activate();
        return Task.CompletedTask;
    }

    public Task HideAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        _window?.Hide();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        _window?.Hide();
        _isRunning = false;
        return Task.CompletedTask;
    }

    public void UpdateStatus(AgentStatusEvent status) => _window?.UpdateStatus(status);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isRunning = false;
        _window?.CloseForShutdown();
        _window = null;
    }

    private MockDesktopWindow EnsureWindow() => _window ??= new MockDesktopWindow();

    private void ThrowIfUnavailable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class UnavailableDesktopController : IDesktopController
{
    private const string UnavailableSummary =
        "Anode 尚未接入，当前无法启动、查看或停止真实 AI 桌面。此实现只是占位控制器，主代理稍后将以真实 IDesktopController 替换它；状态悬浮球仍会监听 Core 管道。";

    public Task<DesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DesktopStatus.Unavailable(UnavailableSummary));
    }

    public Task StartHiddenAsync(CancellationToken cancellationToken = default) => ReportUnavailable(cancellationToken);

    public Task ShowAsync(CancellationToken cancellationToken = default) => ReportUnavailable(cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) => ReportUnavailable(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => ReportUnavailable(cancellationToken);

    private static Task ReportUnavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
