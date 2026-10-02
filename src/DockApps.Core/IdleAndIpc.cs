using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DockApps.Core;

public sealed class IdleShutdownCoordinator(TimeSpan? idleDelay = null) : IIdleShutdownCoordinator, IDisposable
{
    private readonly TimeSpan _delay = idleDelay ?? TimeSpan.FromSeconds(10); private int _windows; private int _operations; private CancellationTokenSource? _timer;
    public event EventHandler? ShutdownRequested;
    public void AddWindow() { Interlocked.Increment(ref _windows); CancelTimer(); }
    public void RemoveWindow() { if (Interlocked.Decrement(ref _windows) <= 0 && Volatile.Read(ref _operations) <= 0) StartTimer(); }
    public void AddOperation() { Interlocked.Increment(ref _operations); CancelTimer(); }
    public void RemoveOperation() { if (Interlocked.Decrement(ref _operations) <= 0 && Volatile.Read(ref _windows) <= 0) StartTimer(); }
    private void StartTimer() { CancelTimer(); _timer = new CancellationTokenSource(); _ = WaitAsync(_timer.Token); }
    private async Task WaitAsync(CancellationToken ct) { try { await Task.Delay(_delay, ct); if (Volatile.Read(ref _windows) <= 0 && Volatile.Read(ref _operations) <= 0) ShutdownRequested?.Invoke(this, EventArgs.Empty); } catch (OperationCanceledException) { } }
    private void CancelTimer() { _timer?.Cancel(); _timer?.Dispose(); _timer = null; }
    public void Dispose() => CancelTimer();
}

public static class IpcProtocol
{
    public const int Version = 1; public const string PipeName = "DockApps.Manager.v1";
    public static async Task WriteAsync(Stream stream, object message, CancellationToken ct) { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); }
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct) { using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true); var line = await reader.ReadLineAsync(ct); return line is null ? default : JsonSerializer.Deserialize<T>(line); }
}

public sealed class NamedPipeServer(IAppLifecycleService lifecycle)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) { await using var pipe = new NamedPipeServerStream(IpcProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous); try { await pipe.WaitForConnectionAsync(ct); var request = await IpcProtocol.ReadAsync<ManagerRequest>(pipe, ct); if (request is null || request.Version != IpcProtocol.Version) { await IpcProtocol.WriteAsync(pipe, new ManagerResponse(IpcProtocol.Version, false, request?.RequestId ?? "", "Unsupported or malformed request."), ct); continue; } await IpcProtocol.WriteAsync(pipe, new ManagerResponse(IpcProtocol.Version, true, request.RequestId), ct); _ = ProcessAsync(request, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (IOException) { } }
    }
    private async Task ProcessAsync(ManagerRequest request, CancellationToken ct) { try { if (request.Type.Equals("open-app", StringComparison.OrdinalIgnoreCase) && request.AppId is not null) await lifecycle.OpenAppAsync(request.AppId, ct); } catch { } }
}
