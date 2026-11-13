using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using AgentOrb.Core;

namespace AgentOrb.Bridge;

public sealed class NamedPipeStatusSink : IAgentStatusSink, IAsyncDisposable
{
    private const int QueueCapacity = 128;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly Channel<AgentStatusEvent> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _connectWarningPublished;
    private int _disposed;

    public NamedPipeStatusSink(string pipeName = AgentOrbConstants.DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _queue = Channel.CreateBounded<AgentStatusEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _worker = Task.Run(() => RunAsync(pipeName, _shutdown.Token));
    }

    public ValueTask PublishAsync(AgentStatusEvent status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        // Never await a UI connection on the MCP forwarding path. If the bounded queue is
        // full, DropWrite discards the newest status while preserving proxy throughput.
        try
        {
            _queue.Writer.TryWrite(status);
        }
        catch (ChannelClosedException)
        {
            // Disposal raced with publication; the status can safely be discarded.
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    // No CurrentUserOnly here: the server already enforces same-user at the
                    // kernel ACL. The .NET 8 client-side check relies on OpenProcess against
                    // the server, which fails across sessions/containers (e.g. MCP hosts like
                    // packaged Codex), breaking status reporting while the pipe is healthy.
                    PipeOptions.Asynchronous);

                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectTimeout.CancelAfter(ConnectTimeout);
                    await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                }

                await using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var status))
                    {
                        var json = StatusEventJson.Serialize(status);
                        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsPipeFailure(exception))
            {
                // No UI, a rejected connection, and a disconnected UI are all non-fatal.
                // Events remain bounded and are discarded when no reader is available.
                if (Interlocked.Exchange(ref _connectWarningPublished, 1) == 0)
                {
                    await Console.Error.WriteLineAsync(
                        $"AgentOrb Bridge：无法连接状态管道 {pipeName}（{exception.GetType().Name}），状态汇报已暂停重试。").ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool IsPipeFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or OperationCanceledException
            or ObjectDisposedException
            or InvalidOperationException;
}
