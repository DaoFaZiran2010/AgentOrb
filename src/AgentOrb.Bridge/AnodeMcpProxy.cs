using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentOrb.Core;

namespace AgentOrb.Bridge;

public sealed class AnodeMcpProxy
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(2);

    private readonly string _anodePath;
    private readonly string _agentId;
    private readonly string _agentName;
    private readonly McpToolCallTracker _toolCallTracker;

    public AnodeMcpProxy(
        string anodePath,
        string agentId,
        string agentName,
        IAgentStatusSink statusSink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anodePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        _anodePath = anodePath;
        _agentId = agentId;
        _agentName = agentName;
        _toolCallTracker = new McpToolCallTracker(
            agentId,
            agentName,
            statusSink ?? throw new ArgumentNullException(nameof(statusSink)));
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _toolCallTracker.Dispose();
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _anodePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.Environment["ANODE_AGENT_ID"] = _agentId;
        startInfo.Environment["ANODE_AGENT_NAME"] = _agentName;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Anode MCP 进程。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动 Anode MCP 进程。", exception);
        }

        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        using var standardInput = Console.OpenStandardInput();
        using var standardOutput = Console.OpenStandardOutput();

        var inputTask = ForwardLinesAsync(
            standardInput,
            process.StandardInput.BaseStream,
            OnClientRequest,
            pumpStop.Token);
        var outputTask = ForwardLinesAsync(
            process.StandardOutput.BaseStream,
            standardOutput,
            OnAnodeOutput,
            pumpStop.Token);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, pumpStop.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        ObserveFaults(inputTask);
        ObserveFaults(outputTask);
        ObserveFaults(stderrTask);
        ObserveFaults(exitTask);

        var completed = await Task.WhenAny(inputTask, outputTask, exitTask).ConfigureAwait(false);

        if (completed == inputTask)
        {
            TryCloseInput(process);
            var gracefulCompletion = Task.WhenAny(outputTask, exitTask);
            await Task.WhenAny(
                gracefulCompletion,
                Task.Delay(ShutdownGracePeriod, CancellationToken.None)).ConfigureAwait(false);
        }
        else if (completed == exitTask)
        {
            // Allow the output pump to forward any final response already buffered by the OS.
            await Task.WhenAny(
                outputTask,
                Task.Delay(ShutdownGracePeriod, CancellationToken.None)).ConfigureAwait(false);
        }

        pumpStop.Cancel();
        TryCloseInput(process);
        if (!exitTask.IsCompleted)
        {
            // Stop only the MCP host. Do not invoke "anode kill" or terminate the seat.
            TryKill(process);
        }

        try
        {
            await exitTask.WaitAsync(ShutdownGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Process disposal still releases all redirected streams owned by this proxy.
        }

        if (inputTask.IsFaulted)
        {
            throw new IOException("MCP 输入流转发失败。", inputTask.Exception?.InnerException);
        }

        if (outputTask.IsFaulted)
        {
            throw new IOException("MCP 输出流转发失败。", outputTask.Exception?.InnerException);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        return exitTask.IsCompletedSuccessfully ? process.ExitCode : 1;
    }

    private void OnClientRequest(string line) => _toolCallTracker.OnClientMessage(line);

    private void OnAnodeOutput(string line) => _toolCallTracker.OnServerMessage(line);

    private static async Task ForwardLinesAsync(
        Stream source,
        Stream destination,
        Action<string> inspect,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var pendingLine = new MemoryStream();

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                if (pendingLine.Length > 0)
                {
                    var length = checked((int)pendingLine.Length);
                    await destination.WriteAsync(
                        pendingLine.GetBuffer().AsMemory(0, length),
                        cancellationToken).ConfigureAwait(false);
                    inspect(Utf8.GetString(pendingLine.GetBuffer(), 0, length));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var segmentStart = 0;
            var forwardedLine = false;
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                var contentLength = index - segmentStart;
                if (pendingLine.Length > 0)
                {
                    await destination.WriteAsync(
                        pendingLine.GetBuffer().AsMemory(0, checked((int)pendingLine.Length)),
                        cancellationToken).ConfigureAwait(false);
                }

                await destination.WriteAsync(
                    buffer.AsMemory(segmentStart, contentLength + 1),
                    cancellationToken).ConfigureAwait(false);
                forwardedLine = true;

                // Inspection excludes LF and its optional preceding CR, while forwarding above
                // preserves every original byte, including CRLF and a missing final newline.
                var inspectionLength = contentLength;
                if (inspectionLength > 0 && buffer[segmentStart + inspectionLength - 1] == (byte)'\r')
                {
                    inspectionLength--;
                }

                if (pendingLine.Length == 0)
                {
                    inspect(Utf8.GetString(buffer, segmentStart, inspectionLength));
                }
                else
                {
                    pendingLine.Write(buffer, segmentStart, inspectionLength);
                    var length = checked((int)pendingLine.Length);
                    inspect(Utf8.GetString(pendingLine.GetBuffer(), 0, length));
                }

                pendingLine.SetLength(0);
                segmentStart = index + 1;
            }

            if (segmentStart < bytesRead)
            {
                pendingLine.Write(buffer, segmentStart, bytesRead - segmentStart);
            }

            if (forwardedLine)
            {
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DrainAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
            // Anode stderr is intentionally drained and discarded: it may contain sensitive
            // tool data, and it must never be copied to protocol stdout.
        }
    }

    private static void TryCloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The child may already have closed its input.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process may have exited concurrently.
        }
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
