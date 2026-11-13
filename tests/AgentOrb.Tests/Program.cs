using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using AgentOrb.Anode;
using AgentOrb.Bridge;
using AgentOrb.Core;
using AgentOrb.Setup;
using AgentOrb.Desktop;

namespace AgentOrb.Tests;

public static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("工具动作映射", TestToolMapping),
            ("状态事件 JSON 往返", TestStatusJsonRoundTrip),
            ("Anode stopped 状态解析", TestStoppedStatusParsing),
            ("Anode 查看器命令", TestViewerCommands),
            ("Anode 错误输出脱敏", TestCommandErrorDoesNotLeakStreams),
            ("命名管道状态传输", TestNamedPipeStatusTransport),
            ("无桥接时状态监听不重复刷屏", TestDisconnectedListenerDoesNotFloodStatus),
            ("实际监听器接收桥接状态", TestListenerReceivesBridgeStatus),
            ("多个 Bridge 同时上报状态", TestConcurrentBridgeClients),
            ("单实例互斥量释放后可重新获取", TestSingleInstanceGuardOwnership),
            ("MCP 数字 ID 调用与成功响应", TestNumericIdCallAndSuccess),
            ("MCP 字符串 ID 与工具错误响应", TestStringIdCallAndToolError),
            ("MCP JSON-RPC 错误响应", TestJsonRpcError),
            ("MCP 无匹配响应不发布", TestUnmatchedResponseDoesNotPublish),
            ("MCP 重复响应只处理一次", TestDuplicateResponseOnlyPublishesOnce),
            ("MCP 超时调用被清理", TestExpiredMcpCallIsRemoved),
            ("MCP notification 与非工具请求忽略", TestNotificationsAndNonToolRequestsAreIgnored),
            ("MCP 畸形 JSON 不影响转发检查", TestMalformedJsonDoesNotInterruptTracking),
            ("MCP 参数与结果秘密不发布", TestSensitivePayloadsAreNotPublished),
            ("Codex 配置安全追加", TestCodexConfigInstallPreservesExistingSettings),
            ("Codex 配置幂等更新", TestCodexConfigUpdateIsIdempotent),
            ("Codex 配置拒绝未托管段", TestCodexConfigRejectsUnmanagedSection),
            ("Codex 配置卸载仅移除管理块", TestCodexConfigUninstallRemovesOnlyManagedBlock),
            ("安装参数拒绝未确认 RDP 风险", TestSetupOptionsRequireRdpConsent)
        };

        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine(failed == 0 ? $"全部 {tests.Length} 项测试通过。" : $"{failed}/{tests.Length} 项测试失败。");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestToolMapping()
    {
        var click = AgentStatusMapper.FromToolCall("seat_click", "agent-1", "Codex");
        Assert(click.State == AgentState.Clicking, "seat_click 应映射为点击。 ");
        Assert(click.Message.Contains("点击", StringComparison.Ordinal), "点击状态应有中文文案。 ");

        var observe = AgentStatusMapper.FromToolCall("seat_screenshot", "agent-1", "Codex");
        Assert(observe.State == AgentState.Observing, "seat_screenshot 应映射为观察。 ");

        var result = AgentStatusMapper.FromToolResult("seat_type", isError: true, "agent-1", "Codex");
        Assert(result.State == AgentState.Error, "工具错误应映射为 Error。 ");
        Assert(result.ToolName == "seat_type", "工具名应保留。 ");
        return Task.CompletedTask;
    }

    private static Task TestStatusJsonRoundTrip()
    {
        var original = AgentStatusEvent.Create(
            AgentState.Typing,
            "正在输入文字",
            "seat_type",
            "agent-1",
            "Codex",
            "session-1");

        var json = StatusEventJson.Serialize(original);
        Assert(json.Contains("\"state\":\"Typing\"", StringComparison.Ordinal), "枚举应序列化为可读字符串。 ");

        var copy = StatusEventJson.Deserialize(json);
        Assert(copy is not null, "状态 JSON 反序列化失败。 ");
        var parsed = copy!;
        Assert(parsed.State == original.State, "状态枚举不一致。 ");
        Assert(parsed.Message == original.Message, "中文消息不一致。 ");
        Assert(parsed.AgentId == original.AgentId, "Agent ID 不一致。 ");
        Assert(parsed.SessionId == original.SessionId, "Session ID 不一致。 ");
        return Task.CompletedTask;
    }

    private static async Task TestStoppedStatusParsing()
    {
        var executor = new FakeProcessExecutor(new ProcessExecutionResult(
            1,
            "{\"state\":\"stopped\",\"daemonRunning\":false,\"viewerVisible\":false,\"viewOnly\":true,\"summary\":\"Anode is not running.\"}",
            string.Empty));

        var controller = new AnodeDesktopController(executor, "fake-anode.exe");
        var status = await controller.GetStatusAsync();

        Assert(status.IsInstalled, "Anode 可执行路径存在时应视为已安装。 ");
        Assert(!status.IsRunning, "stopped 状态不应视为运行中。 ");
        Assert(status.State == "stopped", "stopped 状态未保留。 ");
        Assert(status.Summary.Contains("not running", StringComparison.OrdinalIgnoreCase), "摘要未保留。 ");
        Assert(executor.LastArguments.SequenceEqual(new[] { "status", "--json" }), "状态命令参数不正确。 ");
    }

    private static async Task TestViewerCommands()
    {
        var executor = new FakeProcessExecutor(new ProcessExecutionResult(0, string.Empty, string.Empty));
        var controller = new AnodeDesktopController(executor, "fake-anode.exe");

        await controller.StartHiddenAsync();
        Assert(executor.LastArguments.SequenceEqual(new[] { "start", "--hidden" }), "启动命令不正确。 ");
        Assert(executor.LastTimeout == TimeSpan.FromMinutes(2), "首次启动应使用两分钟超时。 ");

        await controller.ShowAsync();
        Assert(executor.LastArguments.SequenceEqual(new[] { "show" }), "查看命令不正确。 ");
        Assert(executor.LastTimeout == TimeSpan.FromSeconds(15), "查看器命令应保持十五秒超时。 ");

        await controller.HideAsync();
        Assert(executor.LastArguments.SequenceEqual(new[] { "hide" }), "隐藏命令不正确。 ");
    }

    private static async Task TestCommandErrorDoesNotLeakStreams()
    {
        var executor = new FakeProcessExecutor(new ProcessExecutionResult(
            7,
            "sensitive stdout",
            "sensitive stderr"));

        var controller = new AnodeDesktopController(executor, "fake-anode.exe");
        try
        {
            await controller.ShowAsync();
            throw new InvalidOperationException("非零退出码没有触发异常。 ");
        }
        catch (InvalidOperationException exception)
        {
            Assert(!exception.Message.Contains("sensitive", StringComparison.OrdinalIgnoreCase), "错误信息泄露了进程输出。 ");
        }
    }

    private static async Task TestNamedPipeStatusTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = "agentorb-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var sink = new NamedPipeStatusSink(pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var connectTask = server.WaitForConnectionAsync(timeout.Token);
        var expected = AgentStatusEvent.Create(AgentState.Clicking, "正在点击 AI 桌面", "seat_click", "agent-1", "Codex");
        await sink.PublishAsync(expected, timeout.Token);
        await connectTask;

        using var reader = new StreamReader(
            server,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token);
        var actual = StatusEventJson.Deserialize(line ?? string.Empty);

        Assert(actual is not null, "管道未收到状态事件。 ");
        var received = actual!;
        Assert(received.State == expected.State, "管道状态不一致。 ");
        Assert(received.Message == expected.Message, "管道中文消息不一致。 ");
        Assert(received.ToolName == expected.ToolName, "管道工具名不一致。 ");
    }

    private static async Task TestDisconnectedListenerDoesNotFloodStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sink = new CollectingStatusSink();
        var pipeName = "agentorb-disconnected-" + Guid.NewGuid().ToString("N");
        using (var listener = new NamedPipeStatusListener(sink, pipeName))
        {
            listener.Start();
            await Task.Delay(TimeSpan.FromMilliseconds(4600));
            await listener.StopAsync();
        }

        var lifecycleEvents = sink.Events
            .Where(status => status.ToolName == "status.pipe")
            .ToArray();
        Assert(
            lifecycleEvents.Length <= 1,
            $"无桥接时 4.6 秒内不应反复发布状态，实际为 {lifecycleEvents.Length} 次。");
        Assert(
            lifecycleEvents.All(status => status.State == AgentState.Disconnected),
            "无桥接时只能发布 Disconnected，不应周期切换 Error/Idle。");
    }

    private static async Task TestListenerReceivesBridgeStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = "agentorb-listener-" + Guid.NewGuid().ToString("N");
        var received = new CollectingStatusSink();
        using var listener = new NamedPipeStatusListener(received, pipeName);
        listener.Start();
        await Task.Delay(300);

        await using var bridgeSink = new NamedPipeStatusSink(pipeName);
        var expected = AgentStatusEvent.Create(
            AgentState.Clicking,
            "正在点击 AI 桌面",
            "seat_click",
            "agent-1",
            "Codex");
        await bridgeSink.PublishAsync(expected);

        for (var attempt = 0; attempt < 40 && !received.Events.Any(item => item.ToolName == expected.ToolName); attempt++)
        {
            await Task.Delay(50);
        }

        await listener.StopAsync();
        var actual = received.Events.FirstOrDefault(item => item.ToolName == expected.ToolName);
        Assert(actual is not null, "真实监听器未收到桥接状态事件。");
        Assert(actual!.State == expected.State, "真实监听器收到的状态不一致。");
        Assert(actual.Message == expected.Message, "真实监听器收到的中文消息不一致。");
    }

    private static async Task TestConcurrentBridgeClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pipeName = "agentorb-concurrent-" + Guid.NewGuid().ToString("N");
        var received = new CollectingStatusSink();
        using var listener = new NamedPipeStatusListener(received, pipeName);
        listener.Start();
        await Task.Delay(300);

        await using var firstBridge = new NamedPipeStatusSink(pipeName);
        await using var secondBridge = new NamedPipeStatusSink(pipeName);
        var firstStatus = AgentStatusEvent.Create(AgentState.Clicking, "第一个 Agent 正在点击", "seat_click", "agent-one", "Codex");
        var secondStatus = AgentStatusEvent.Create(AgentState.Typing, "第二个 Agent 正在输入", "seat_type", "agent-two", "Other Agent");

        await Task.WhenAll(
            firstBridge.PublishAsync(firstStatus).AsTask(),
            secondBridge.PublishAsync(secondStatus).AsTask());
        for (var attempt = 0; attempt < 40 && received.Events.Count < 3; attempt++)
        {
            await Task.Delay(50);
        }

        await listener.StopAsync();
        Assert(received.Events.Any(item => item.AgentId == "agent-one"), "未收到第一个保持连接的 Bridge 状态。");
        Assert(received.Events.Any(item => item.AgentId == "agent-two"), "第二个 Bridge 被第一个连接阻塞。");
        Assert(
            received.Events.First(item => item.AgentId == "agent-two").Message == secondStatus.Message,
            "第二个 Bridge 的状态内容不一致。");
    }

    private static Task TestSingleInstanceGuardOwnership()
    {
        var mutexName = @"Local\AgentOrb.Tests." + Guid.NewGuid().ToString("N");
        var first = SingleInstanceGuard.TryAcquire(mutexName);
        try
        {
            Assert(first.IsOwner, "首个实例应取得互斥量。");
            var secondOwned = true;
            var secondThread = new Thread(() =>
            {
                var second = SingleInstanceGuard.TryAcquire(mutexName);
                try
                {
                    secondOwned = second.IsOwner;
                }
                finally
                {
                    second.Dispose();
                }
            });
            secondThread.Start();
            secondThread.Join();
            Assert(!secondOwned, "另一个线程不应取得同一互斥量。");
        }
        finally
        {
            first.Dispose();
        }

        var third = SingleInstanceGuard.TryAcquire(mutexName);
        try
        {
            Assert(third.IsOwner, "旧实例释放后应允许新实例取得互斥量。");
        }
        finally
        {
            third.Dispose();
        }

        return Task.CompletedTask;
    }

    private static Task TestNumericIdCallAndSuccess()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"seat_click","arguments":{"x":10,"y":20}}}""");
        tracker.OnServerMessage("""{"jsonrpc":"2.0","id":42,"result":{"content":[{"type":"text","text":"clicked"}],"isError":false}}""");

        Assert(sink.Events.Count == 2, "数字 ID 调用和响应应各发布一个事件。");
        Assert(sink.Events[0].State == AgentState.Clicking, "seat_click 调用应发布点击状态。");
        Assert(sink.Events[0].ToolName == "seat_click", "调用事件应保留工具名。");
        Assert(sink.Events[0].AgentId == "agent-1", "调用事件应保留 Agent ID。");
        Assert(sink.Events[0].AgentName == "Codex", "调用事件应保留 Agent 名称。");
        Assert(sink.Events[1].State == AgentState.Idle, "成功响应应发布 Idle 状态。");
        Assert(sink.Events[1].ToolName == "seat_click", "响应事件应使用已跟踪的工具名。");
        return Task.CompletedTask;
    }

    private static Task TestStringIdCallAndToolError()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":"call-abc","method":"tools/call","params":{"name":"seat_type","arguments":{"text":"input"}}}""");
        tracker.OnServerMessage("""{"jsonrpc":"2.0","id":"call-abc","result":{"content":[{"type":"text","text":"failed"}],"isError":true}}""");

        Assert(sink.Events.Count == 2, "字符串 ID 调用和响应应各发布一个事件。");
        Assert(sink.Events[0].State == AgentState.Typing, "seat_type 调用应发布输入状态。");
        Assert(sink.Events[1].State == AgentState.Error, "isError=true 应发布 Error 状态。");
        Assert(sink.Events[1].ToolName == "seat_type", "字符串 ID 应匹配原工具调用。");
        return Task.CompletedTask;
    }

    private static Task TestJsonRpcError()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"seat_run","arguments":{"command":"app"}}}""");
        tracker.OnServerMessage("""{"jsonrpc":"2.0","id":7,"error":{"code":-32603,"message":"backend failed"}}""");

        Assert(sink.Events.Count == 2, "JSON-RPC 错误应对应一个调用事件和一个结果事件。");
        Assert(sink.Events[1].State == AgentState.Error, "JSON-RPC error 应发布 Error 状态。");
        Assert(sink.Events[1].ToolName == "seat_run", "JSON-RPC 错误应保留原工具名。");
        Assert(sink.Events[1].Message == "桌面工具执行失败", "错误事件只能使用固定 Core 文案。");
        return Task.CompletedTask;
    }

    private static Task TestUnmatchedResponseDoesNotPublish()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnServerMessage("""{"jsonrpc":"2.0","id":999,"result":{"isError":false}}""");

        Assert(sink.Events.Count == 0, "没有匹配调用的响应不应发布事件。");
        return Task.CompletedTask;
    }

    private static Task TestDuplicateResponseOnlyPublishesOnce()
    {
        var (tracker, sink) = CreateTracker();
        const string response = """{"jsonrpc":"2.0","id":8,"result":{"isError":false}}""";

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"seat_screenshot","arguments":{}}}""");
        tracker.OnServerMessage(response);
        tracker.OnServerMessage(response);

        Assert(sink.Events.Count == 2, "重复响应只能产生一个结果事件。");
        Assert(sink.Events[0].State == AgentState.Observing, "截图调用应发布观察状态。");
        Assert(sink.Events[1].State == AgentState.Idle, "首个匹配响应应发布成功状态。");
        return Task.CompletedTask;
    }

    private static Task TestExpiredMcpCallIsRemoved()
    {
        var sink = new CollectingStatusSink();
        using var tracker = new McpToolCallTracker(
            "agent-1",
            "Codex",
            sink,
            TimeSpan.FromMilliseconds(50),
            enableCleanupTimer: false);
        const string response = """{"jsonrpc":"2.0","id":55,"result":{"isError":false}}""";

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":55,"method":"tools/call","params":{"name":"seat_screenshot","arguments":{}}}""");
        Assert(tracker.PendingCallCount == 1, "调用后应保留一条待响应记录。");
        tracker.ExpirePendingCalls(DateTimeOffset.UtcNow.AddSeconds(1));

        Assert(tracker.PendingCallCount == 0, "超时调用未被清理。");
        Assert(sink.Events.Count == 2, "超时只应新增一个状态事件。");
        Assert(sink.Events[1].State == AgentState.Error, "超时调用应发布 Error。");
        Assert(sink.Events[1].Message == "桌面工具执行超时", "超时提示不一致。");
        tracker.OnServerMessage(response);
        Assert(sink.Events.Count == 2, "超时后的迟到响应不应再次发布状态。");
        return Task.CompletedTask;
    }

    private static Task TestNotificationsAndNonToolRequestsAreIgnored()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnClientMessage("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"name":"seat_click"}}""");
        tracker.OnServerMessage("""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}""");

        Assert(sink.Events.Count == 0, "notification 和非 tools/call 请求不应发布事件。");
        return Task.CompletedTask;
    }

    private static Task TestMalformedJsonDoesNotInterruptTracking()
    {
        var (tracker, sink) = CreateTracker();

        tracker.OnClientMessage("""{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"seat_click","arguments":{}}}""");
        tracker.OnClientMessage("{malformed-client-json");
        tracker.OnServerMessage("{malformed-server-json");
        tracker.OnServerMessage("""{"jsonrpc":"2.0","id":31,"result":{"isError":false}}""");

        Assert(sink.Events.Count == 2, "畸形 JSON 不应抛出异常或阻断后续匹配响应。");
        Assert(sink.Events[1].State == AgentState.Idle, "畸形 JSON 后仍应处理原调用的响应。");
        return Task.CompletedTask;
    }

    private static Task TestSensitivePayloadsAreNotPublished()
    {
        const string secret = "agentorb-secret-do-not-publish-9f27";
        var (tracker, sink) = CreateTracker();
        var request = "{\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"tools/call\",\"params\":{\"name\":\"seat_type\",\"arguments\":{\"text\":\"" + secret + "\"}}}";
        var response = "{\"jsonrpc\":\"2.0\",\"id\":77,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + secret + "\"}],\"isError\":false}}";

        Assert(request.Contains(secret, StringComparison.Ordinal), "测试输入必须包含 arguments 秘密。");
        Assert(response.Contains(secret, StringComparison.Ordinal), "测试输入必须包含 text 秘密。");
        tracker.OnClientMessage(request);
        tracker.OnServerMessage(response);

        Assert(sink.Events.Count == 2, "秘密测试应产生调用和结果两个事件。");
        foreach (var status in sink.Events)
        {
            var json = StatusEventJson.Serialize(status);
            Assert(!json.Contains(secret, StringComparison.Ordinal), "发布的状态事件不得包含 arguments 或 text 秘密。");
        }

        return Task.CompletedTask;
    }

    private static (McpToolCallTracker Tracker, CollectingStatusSink Sink) CreateTracker()
    {
        var sink = new CollectingStatusSink();
        return (new McpToolCallTracker("agent-1", "Codex", sink, enableCleanupTimer: false), sink);
    }

    private static Task TestCodexConfigInstallPreservesExistingSettings()
    {
        return WithTempConfig(
            "model = \"gpt-5\"\r\n\r\n[model_providers.cc-switch]\r\nname = \"CC Switch\"\r\n",
            (configPath, directory) =>
            {
                var backupPath = string.Empty;
                var bridgePath = @"C:\Program Files\AgentOrb\AgentOrb.Bridge.exe";
                var result = CodexConfigManager.Install(configPath, bridgePath, "codex", "Codex");
                backupPath = result.BackupPath;
                var updated = File.ReadAllText(configPath);

                Assert(updated.Contains("model = \"gpt-5\"", StringComparison.Ordinal), "模型配置被修改。");
                Assert(updated.Contains("[model_providers.cc-switch]", StringComparison.Ordinal), "CC Switch 配置被修改。");
                Assert(updated.Contains("[mcp_servers.agentorb]", StringComparison.Ordinal), "未写入 AgentOrb MCP 段。");
                Assert(updated.Contains("C:\\\\Program Files\\\\AgentOrb\\\\AgentOrb.Bridge.exe", StringComparison.Ordinal), "Bridge 路径未正确转义。");
                Assert(File.Exists(backupPath), "未创建 Codex 配置备份。");
                Assert(File.ReadAllText(backupPath).Contains("gpt-5", StringComparison.Ordinal), "备份内容不正确。");
                return Task.CompletedTask;
            });
    }

    private static Task TestCodexConfigUpdateIsIdempotent()
    {
        return WithTempConfig(
            "model = \"gpt-5\"\n",
            (configPath, directory) =>
            {
                CodexConfigManager.Install(configPath, @"C:\Old\AgentOrb.Bridge.exe", "codex", "Codex");
                var second = CodexConfigManager.Install(configPath, @"C:\New\AgentOrb.Bridge.exe", "codex", "Codex");
                var updated = File.ReadAllText(configPath);

                Assert(second.ReplacedExistingBlock, "第二次安装未识别已有的管理块。");
                Assert(
                    updated.Split("[mcp_servers.agentorb]", StringSplitOptions.None).Length == 2,
                    "重复安装产生了多个 AgentOrb MCP 段。");
                Assert(updated.Contains("AgentOrb.Bridge.exe", StringComparison.Ordinal), "更新后缺少 Bridge 路径。");
                Assert(!updated.Contains(@"C:\Old\AgentOrb.Bridge.exe", StringComparison.Ordinal), "旧 Bridge 路径未被替换。");
                return Task.CompletedTask;
            });
    }

    private static Task TestCodexConfigRejectsUnmanagedSection()
    {
        const string original = "[mcp_servers.agentorb]\ncommand = \"other\"\n";
        return WithTempConfig(
            original,
            (configPath, directory) =>
            {
                try
                {
                    CodexConfigManager.Install(configPath, @"C:\AgentOrb\AgentOrb.Bridge.exe", "codex", "Codex");
                    throw new InvalidOperationException("未托管的 AgentOrb 段没有被拒绝。");
                }
                catch (InvalidDataException)
                {
                    Assert(File.ReadAllText(configPath) == original, "拒绝写入时仍修改了配置。");
                }

                return Task.CompletedTask;
            });
    }

    private static Task TestCodexConfigUninstallRemovesOnlyManagedBlock()
    {
        const string original = "model = \"gpt-5\"\r\n\r\n[model_providers.cc-switch]\r\nname = \"CC Switch\"\r\n";
        return WithTempConfig(
            original,
            (configPath, directory) =>
            {
                CodexConfigManager.Install(configPath, @"C:\AgentOrb\AgentOrb.Bridge.exe", "codex", "Codex");
                var removed = CodexConfigManager.Uninstall(configPath);

                Assert(removed, "未识别并移除 AgentOrb 管理块。");
                Assert(File.ReadAllText(configPath) == original, "卸载后没有恢复原有配置。");
                return Task.CompletedTask;
            });
    }

    private static Task TestSetupOptionsRequireRdpConsent()
    {
        try
        {
            SetupOptions.Parse(new[] { "install", "--no-start" });
            throw new InvalidOperationException("未确认 RDP 风险的安装没有被拒绝。");
        }
        catch (ArgumentException)
        {
        }

        var options = SetupOptions.Parse(new[] { "install", "--accept-rdp-change", "--no-start", "--skip-codex" });
        Assert(options.Command == SetupCommand.Install, "安装命令解析错误。");
        Assert(options.AcceptRdpChange, "RDP 风险确认标志丢失。");
        Assert(options.NoStart, "启动选项丢失。");
        Assert(options.SkipCodex, "Codex 跳过选项丢失。");
        return Task.CompletedTask;
    }

    private static async Task WithTempConfig(
        string content,
        Func<string, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentorb-setup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.toml");
        File.WriteAllText(configPath, content);
        try
        {
            await action(configPath, directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class CollectingStatusSink : IAgentStatusSink
    {
        private readonly ConcurrentQueue<AgentStatusEvent> _events = new();
        public IReadOnlyList<AgentStatusEvent> Events => _events.ToArray();

        public ValueTask PublishAsync(AgentStatusEvent status, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(status);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcessExecutor(ProcessExecutionResult result) : IProcessExecutor
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = Array.Empty<string>();
        public TimeSpan LastTimeout { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments.ToArray();
            LastTimeout = timeout;
            return Task.FromResult(result);
        }
    }
}
