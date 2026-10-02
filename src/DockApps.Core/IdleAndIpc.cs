using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DockApps.Core;

public sealed class IdleShutdownCoordinator(TimeSpan? idleDelay = null) : IIdleShutdownCoordinator, IDisposable
{
    private readonly TimeSpan _delay = idleDelay ?? TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private CancellationTokenSource? _timer;
    private int _windows;
    private int _operations;
    private bool _disposed;

    public event EventHandler? ShutdownRequested;

    public void AddWindow()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _windows++;
            CancelTimerLocked();
        }
    }

    public void RemoveWindow()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_windows > 0) _windows--;
            StartTimerIfIdleLocked();
        }
    }

    public void AddOperation()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _operations++;
            CancelTimerLocked();
        }
    }

    public void RemoveOperation()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_operations > 0) _operations--;
            StartTimerIfIdleLocked();
        }
    }

    private void StartTimerIfIdleLocked()
    {
        if (_windows > 0 || _operations > 0 || _timer is not null) return;

        _timer = new CancellationTokenSource();
        _ = WaitAsync(_timer.Token);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_disposed || _windows > 0 || _operations > 0) return;
            }

            ShutdownRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // A new window or operation became active.
        }
    }

    private void CancelTimerLocked()
    {
        _timer?.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IdleShutdownCoordinator));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelTimerLocked();
        }
    }
}

public static class IpcProtocol
{
    public const int Version = 1;
    public const string PipeName = "DockApps.Manager.v1";
    public const int MaxMessageLength = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        WriteIndented = false
    };

    public static async Task WriteAsync(Stream stream, object message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) return default;
        if (line.Length > MaxMessageLength) throw new InvalidDataException("IPC message is too large.");
        return JsonSerializer.Deserialize<T>(line, JsonOptions);
    }

    public static bool TryValidateRequest(ManagerRequest? request, out string error)
    {
        if (request is null)
        {
            error = "Request body is missing or malformed.";
            return false;
        }

        if (request.Version != Version)
        {
            error = $"Unsupported IPC protocol version: {request.Version}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 || ContainsControlCharacter(request.RequestId))
        {
            error = "Request ID is missing or invalid.";
            return false;
        }

        if (string.Equals(request.Type, "open-manager", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(request.AppId))
            {
                error = "open-manager requests must not include an app ID.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (!string.Equals(request.Type, "open-app", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported request type.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.AppId) || request.AppId.Length > 256 || ContainsControlCharacter(request.AppId))
        {
            error = "App ID is missing or invalid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}

public sealed class NamedPipeServer : IAsyncDisposable
{
    private readonly Func<ManagerRequest, CancellationToken, Task> _requestHandler;
    private readonly IIdleShutdownCoordinator? _activity;
    private readonly Channel<ManagerRequest> _queue = Channel.CreateUnbounded<ManagerRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<int, Task> _inFlight = new();
    private int _requestNumber;

    public NamedPipeServer(IAppLifecycleService lifecycle, IIdleShutdownCoordinator? activity = null)
        : this(HandleLifecycleRequestAsync(lifecycle), activity)
    {
    }

    public NamedPipeServer(Func<ManagerRequest, CancellationToken, Task> requestHandler, IIdleShutdownCoordinator? activity = null)
    {
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        _activity = activity;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var processor = ProcessQueueAsync();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = CreateServerStream();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // The client may disconnect before the acknowledgement is written.
                }
                catch (UnauthorizedAccessException)
                {
                    // A transient pipe ACL/endpoint error should not terminate the server loop.
                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            try
            {
                await processor.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        ManagerRequest? request = null;
        string error;

        try
        {
            request = await IpcProtocol.ReadAsync<ManagerRequest>(pipe, cancellationToken).ConfigureAwait(false);
            if (!IpcProtocol.TryValidateRequest(request, out error))
            {
                await WriteRejectedAsync(pipe, request?.RequestId, error, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (JsonException)
        {
            await WriteRejectedAsync(pipe, null, "Request body is malformed JSON.", cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (InvalidDataException ex)
        {
            await WriteRejectedAsync(pipe, null, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await IpcProtocol.WriteAsync(pipe, new ManagerResponse(IpcProtocol.Version, true, request!.RequestId), cancellationToken).ConfigureAwait(false);

        // The acknowledgement is deliberately sent before the potentially long-running handler.
        _queue.Writer.TryWrite(request);
    }

    private static Task WriteRejectedAsync(Stream pipe, string? requestId, string error, CancellationToken cancellationToken) =>
        IpcProtocol.WriteAsync(pipe, new ManagerResponse(IpcProtocol.Version, false, requestId ?? string.Empty, error), cancellationToken);

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var number = Interlocked.Increment(ref _requestNumber);
            var task = ProcessRequestAsync(request);
            _inFlight[number] = task;
            _ = RemoveCompletedRequestAsync(number, task);
        }

        await Task.WhenAll(_inFlight.Values).ConfigureAwait(false);
    }

    private async Task RemoveCompletedRequestAsync(int number, Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { _inFlight.TryRemove(number, out _); }
    }

    private async Task ProcessRequestAsync(ManagerRequest request)
    {
        _activity?.AddOperation();
        try
        {
            // A request is processed asynchronously and independently of the pipe connection.
            await _requestHandler(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Processing errors are intentionally isolated to the request. The acknowledgement
            // already told the launcher that the manager accepted it; UI routing owns diagnostics.
        }
        finally
        {
            _activity?.RemoveOperation();
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        // CurrentUserOnly applies the platform's per-user pipe ACL without requiring the
        // optional System.IO.Pipes.AccessControl package at runtime.
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        return new NamedPipeServerStream(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            options,
            4 * 1024,
            4 * 1024);
    }

    private static Func<ManagerRequest, CancellationToken, Task> HandleLifecycleRequestAsync(IAppLifecycleService lifecycle) =>
        async (request, cancellationToken) =>
        {
            if (request.Type.Equals("open-app", StringComparison.OrdinalIgnoreCase))
                await lifecycle.OpenAppAsync(request.AppId!, cancellationToken).ConfigureAwait(false);
        };

    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
