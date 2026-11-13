using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentOrb.Core;

namespace AgentOrb.Desktop;

public sealed class NamedPipeStatusListener : IDisposable
{
    private const int MaxConcurrentBridges = 8;
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IAgentStatusSink _sink;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<int, ClientConnection> _connections = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Task? _runTask;
    private bool _disposed;
    private string? _lastLifecycleKey;
    private int _nextConnectionId;
    private int _connectedBridges;

    public NamedPipeStatusListener(
        IAgentStatusSink sink,
        string pipeName = AgentOrbConstants.DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _sink = sink;
        _pipeName = pipeName;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runTask ??= Task.Run(() => RunAsync(_cancellation.Token));
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();
        DisposeConnectionServers();
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
        DisposeConnectionServers();

        var runTask = _runTask;
        if (runTask is null || runTask.IsCompleted)
        {
            runTask?.Dispose();
            _lifecycleLock.Dispose();
            _cancellation.Dispose();
        }
        else
        {
            _ = runTask.ContinueWith(
                _ =>
                {
                    runTask.Dispose();
                    _lifecycleLock.Dispose();
                    _cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ClientConnection? connection = null;
                try
                {
                    var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        MaxConcurrentBridges,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    connection = new ClientConnection(
                        Interlocked.Increment(ref _nextConnectionId),
                        server);
                    if (!_connections.TryAdd(connection.Id, connection))
                    {
                        connection.Dispose();
                        continue;
                    }

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    connection.ReaderTask = ReadClientAsync(connection, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    RemoveAndDispose(connection);
                    if (Volatile.Read(ref _connectedBridges) == 0)
                    {
                        await PublishLifecycleAsync(
                            "waiting",
                            AgentStatusEvent.Create(AgentState.Disconnected, "等待 Agent 状态桥接", "status.pipe", "core", "AgentOrb Core"),
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    if (!await DelayAfterAcceptFailureAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    RemoveAndDispose(connection);
                    await PublishLifecycleAsync(
                        "listener-error",
                        AgentStatusEvent.Create(AgentState.Error, "状态监听暂时不可用", "status.pipe", "core", "AgentOrb Core"),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!await DelayAfterAcceptFailureAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            var readers = _connections.Values
                .Select(connection => connection.ReaderTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            DisposeConnectionServers();
            try
            {
                await Task.WhenAll(readers).WaitAsync(ReaderShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Shutdown must not be held open by a broken pipe client.
            }
        }
    }

    private async Task ReadClientAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var connected = false;
        try
        {
            using var reader = new StreamReader(
                connection.Server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);

            connected = true;
            if (Interlocked.Increment(ref _connectedBridges) == 1)
            {
                await PublishLifecycleAsync(
                    "connected",
                    AgentStatusEvent.Create(AgentState.Idle, "已连接 Agent 状态桥接", "status.pipe", "core", "AgentOrb Core"),
                    cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    await PublishLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // A single client disconnecting must not stop other bridge readers or the accept loop.
        }
        catch (Exception)
        {
            await PublishLifecycleAsync(
                "reader-error",
                AgentStatusEvent.Create(AgentState.Error, "状态桥接读取异常", "status.pipe.client", "core", "AgentOrb Core"),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            connection.Dispose();
            _connections.TryRemove(connection.Id, out _);
            if (connected && Interlocked.Decrement(ref _connectedBridges) == 0)
            {
                await PublishLifecycleAsync(
                    "disconnected",
                    AgentStatusEvent.Create(AgentState.Disconnected, "状态桥接已断开，等待重新连接", "status.pipe", "core", "AgentOrb Core"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishLifecycleAsync(
        string key,
        AgentStatusEvent status,
        CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_lastLifecycleKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _lastLifecycleKey = key;
            await PublishAsync(status, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task PublishLineAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            var status = StatusEventJson.Deserialize(line);
            if (status is null)
            {
                await PublishAsync(
                    AgentStatusEvent.Create(AgentState.Error, "收到空的状态事件", "status.pipe.json", "core", "AgentOrb Core"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await PublishAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await PublishAsync(
                AgentStatusEvent.Create(AgentState.Error, "收到无法解析的状态 JSON", "status.pipe.json", "core", "AgentOrb Core"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            await PublishAsync(
                AgentStatusEvent.Create(AgentState.Error, "收到不受支持的状态事件", "status.pipe.json", "core", "AgentOrb Core"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> DelayAfterAcceptFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AcceptRetryDelay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void DisposeConnectionServers()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }

    private void RemoveAndDispose(ClientConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        _connections.TryRemove(connection.Id, out _);
        connection.Dispose();
    }

    private ValueTask PublishAsync(AgentStatusEvent status, CancellationToken cancellationToken) =>
        _disposed ? ValueTask.CompletedTask : _sink.PublishAsync(status, cancellationToken);

    private sealed class ClientConnection(int id, NamedPipeServerStream server) : IDisposable
    {
        public int Id { get; } = id;
        public NamedPipeServerStream Server { get; } = server;
        public Task? ReaderTask { get; set; }
        public void Dispose() => Server.Dispose();
    }
}
