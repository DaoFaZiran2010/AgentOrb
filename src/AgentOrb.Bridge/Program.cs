using AgentOrb.Anode;
using AgentOrb.Core;

namespace AgentOrb.Bridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        BridgeOptions options;
        try
        {
            options = BridgeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using var statusSink = new NamedPipeStatusSink(options.PipeName);
            if (options.IsDemo)
            {
                await RunDemoAsync(statusSink, options, shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            var anodePath = AnodeExecutableLocator.Resolve(options.AnodePath);
            if (anodePath is null)
            {
                await Console.Error.WriteLineAsync(
                    "未找到 Anode，可使用 --anode <path> 或设置 ANODE_EXE。").ConfigureAwait(false);
                return 2;
            }

            var proxy = new AnodeMcpProxy(
                anodePath,
                options.AgentId,
                options.AgentName,
                statusSink);
            return await proxy.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            var message = exception is InvalidOperationException or IOException or TimeoutException
                ? exception.Message
                : "发生未处理错误。";
            await Console.Error.WriteLineAsync(
                $"AgentOrb Bridge 失败（{exception.GetType().Name}）：{message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            shutdown.Cancel();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunDemoAsync(
        IAgentStatusSink statusSink,
        BridgeOptions options,
        CancellationToken cancellationToken)
    {
        var states = new[]
        {
            (AgentState.Idle, "等待 Agent 指令"),
            (AgentState.Observing, "正在读取 AI 桌面"),
            (AgentState.Clicking, "正在点击 AI 桌面"),
            (AgentState.Typing, "正在输入文字"),
            (AgentState.Scrolling, "正在滚动页面"),
            (AgentState.Launching, "正在打开应用或运行命令"),
            (AgentState.Waiting, "Agent 正在执行桌面任务"),
            (AgentState.Completed, "桌面任务已完成"),
            (AgentState.Error, "桌面工具执行失败")
        };

        var index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (state, message) = states[index];
            var status = AgentStatusEvent.Create(
                state,
                message,
                toolName: null,
                agentId: options.AgentId,
                agentName: options.AgentName);
            await statusSink.PublishAsync(status, cancellationToken).ConfigureAwait(false);
            index = (index + 1) % states.Length;
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record BridgeOptions
    {
        public string? AnodePath { get; private init; }
        public string AgentId { get; private init; } = "unknown";
        public string AgentName { get; private init; } = "Agent";
        public string PipeName { get; private init; } = AgentOrbConstants.DefaultPipeName;
        public bool IsDemo { get; private init; }

        public static BridgeOptions Parse(string[] args)
        {
            var options = new BridgeOptions();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--anode":
                        options = options with { AnodePath = ReadValue(args, ref index) };
                        break;
                    case "--agent-id":
                        options = options with { AgentId = ReadValue(args, ref index) };
                        break;
                    case "--agent-name":
                        options = options with { AgentName = ReadValue(args, ref index) };
                        break;
                    case "--pipe":
                        options = options with { PipeName = ReadValue(args, ref index) };
                        break;
                    case "--demo":
                        options = options with { IsDemo = true };
                        break;
                    default:
                        throw new ArgumentException("存在未知命令行参数。");
                }
            }

            return options;
        }

        private static string ReadValue(string[] args, ref int index)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("命令行参数缺少有效值。");
            }

            return args[index];
        }
    }
}
