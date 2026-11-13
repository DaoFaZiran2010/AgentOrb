using AgentOrb.Core;

namespace AgentOrb.Desktop;

public sealed class DemoStatusPublisher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(5000);
    private readonly IAgentStatusSink _sink;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _runTask;
    private bool _disposed;

    public DemoStatusPublisher(IAgentStatusSink sink)
    {
        _sink = sink;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runTask ??= RunAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();
        if (_runTask is not null)
        {
            await _runTask.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        var runTask = _runTask;
        if (runTask is null || runTask.IsCompleted)
        {
            runTask?.Dispose();
            _cancellation.Dispose();
        }
        else
        {
            _ = runTask.ContinueWith(
                _ =>
                {
                    runTask.Dispose();
                    _cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var states = new[]
        {
            AgentStatusEvent.Create(AgentState.Observing, "正在观察虚拟桌面", "demo.observe", "demo-agent", "演示 Agent（非 Anode）", "demo-session"),
            AgentStatusEvent.Create(AgentState.Clicking, "正在演示点击记事本", "demo.click", "demo-agent", "演示 Agent（非 Anode）", "demo-session"),
            AgentStatusEvent.Create(AgentState.Typing, "正在演示输入中文内容", "demo.type", "demo-agent", "演示 Agent（非 Anode）", "demo-session"),
            AgentStatusEvent.Create(AgentState.Scrolling, "正在演示滚动文档", "demo.scroll", "demo-agent", "演示 Agent（非 Anode）", "demo-session"),
            AgentStatusEvent.Create(AgentState.Waiting, "正在等待下一步演示指令", "demo.wait", "demo-agent", "演示 Agent（非 Anode）", "demo-session")
        };

        try
        {
            using var timer = new PeriodicTimer(Interval);
            var index = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var status = states[index] with { Timestamp = DateTimeOffset.UtcNow };
                await _sink.PublishAsync(status, cancellationToken).ConfigureAwait(false);
                index = (index + 1) % states.Length;
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                await _sink.PublishAsync(
                    AgentStatusEvent.Create(
                        AgentState.Error,
                        $"演示状态发布失败：{ex.Message}",
                        "demo.publisher",
                        "demo-agent",
                        "演示 Agent（非 Anode）",
                        "demo-session"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
