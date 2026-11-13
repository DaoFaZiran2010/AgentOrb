using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentOrb.Anode;

public interface IProcessExecutor
{
    Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed class SystemProcessExecutor : IProcessExecutor
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument ?? throw new ArgumentException("进程参数不能为 null。", nameof(arguments)));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Anode 进程。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动 Anode 进程。", exception);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        // Start both reads before waiting so a full stdout/stderr pipe cannot deadlock the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Do not hang cancellation or timeout handling if process termination stalls.
            }

            ObserveFaults(stdoutTask);
            ObserveFaults(stderrTask);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException($"Anode 命令在 {timeout.TotalSeconds:0.###} 秒后超时。");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Only stop the short-lived CLI process. Never run "anode kill" implicitly.
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process may have exited concurrently; cleanup remains best-effort.
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
