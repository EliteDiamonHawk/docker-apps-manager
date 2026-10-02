using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DockApps.Core;

public sealed class JsonDockerOwnershipService(string? root = null) : IDockerOwnershipService
{
    private readonly string _path = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps"), "state.json");
    public DockerSessionState Current { get; private set; } = new(false, null, null);
    public async Task LoadAsync(CancellationToken cancellationToken = default) { if (!File.Exists(_path)) return; try { Current = await JsonSerializer.DeserializeAsync<DockerSessionState>(File.OpenRead(_path), options: null, cancellationToken) ?? new(false, null, null); } catch (JsonException) { Current = new(false, null, null); } }
    public async Task MarkOwnedAsync(string sessionIdentity, CancellationToken cancellationToken = default) { Current = new(true, sessionIdentity, DateTimeOffset.UtcNow); await SaveAsync(cancellationToken); }
    public async Task ClearAsync(CancellationToken cancellationToken = default) { Current = new(false, null, null); await SaveAsync(cancellationToken); }
    public bool IsCurrentSessionOwned(string? sessionIdentity) => Current.StartedByDockApps && sessionIdentity is not null && sessionIdentity == Current.SessionIdentity;
    private async Task SaveAsync(CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Current), ct); File.Move(temp, _path, true); }
}

public sealed class BrowserService : IBrowserService
{
    public Task<OperationResult> OpenAsync(string url, CancellationToken cancellationToken = default) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.FromResult(OperationResult.Success()); } catch (Exception ex) { return Task.FromResult(OperationResult.Failure(ex.Message)); } }
}

public sealed class AppLifecycleService(IDockerDesktopService docker, IComposeService compose, IDockerContainerService containers, IAppRegistry registry, IDockerOwnershipService ownership, IBrowserService browser) : IAppLifecycleService
{
    private readonly SemaphoreSlim _dockerLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _appLocks = new(StringComparer.OrdinalIgnoreCase);
    private Task<OperationResult>? _startupTask;
    public async Task<AppRuntimeStatus> OpenAppAsync(string appId, CancellationToken ct = default)
    {
        var app = registry.Find(appId) ?? throw new KeyNotFoundException($"App is not registered: {appId}");
        var appSemaphore = _appLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1)); await appSemaphore.WaitAsync(ct); await using var gate = new AsyncGate(appSemaphore);
        var existing = await compose.GetProjectStatusAsync(app, ct); if (existing.IsHealthy) { if (app.OpenBrowser && app.Url is not null) await browser.OpenAsync(app.Url, ct); return new(app, AppRuntimeState.Running, existing); }
        var ready = await EnsureDockerReadyAsync(app.StartupTimeoutSeconds, ct); if (!ready.Succeeded) return new(app, AppRuntimeState.Error, existing, ready.Message);
        existing = await compose.GetProjectStatusAsync(app, ct); if (!existing.IsRunning) { var started = await compose.StartProjectAsync(app, ct); if (!started.Succeeded) return new(app, AppRuntimeState.Error, existing, started.Message); }
        var deadline = DateTime.UtcNow.AddSeconds(app.StartupTimeoutSeconds); do { existing = await compose.GetProjectStatusAsync(app, ct); if (existing.IsHealthy) { var browserResult = app.OpenBrowser && app.Url is not null ? await browser.OpenAsync(app.Url, ct) : OperationResult.Success(); return new(app, AppRuntimeState.Running, existing, browserResult.Succeeded ? null : $"Running, but browser could not be opened: {browserResult.Message}"); } await Task.Delay(1000, ct); } while (DateTime.UtcNow < deadline);
        return new(app, existing.IsRunning ? AppRuntimeState.Degraded : AppRuntimeState.Error, existing, "The Compose project did not become healthy before the timeout.");
    }
    private async Task<OperationResult> EnsureDockerReadyAsync(int timeoutSeconds, CancellationToken ct)
    {
        await _dockerLock.WaitAsync(ct); try { if ((await docker.GetStatusAsync(ct)).IsReady) return OperationResult.Success(); _startupTask ??= StartDockerAsync(timeoutSeconds, ct); return await _startupTask; } finally { _dockerLock.Release(); }
    }
    private async Task<OperationResult> StartDockerAsync(int timeoutSeconds, CancellationToken ct) { var started = await docker.StartAsync(ct); if (!started.Succeeded) return started; var ready = await docker.WaitUntilReadyAsync(TimeSpan.FromSeconds(timeoutSeconds), ct); if (ready.Succeeded) { var id = await docker.GetSessionIdentityAsync(ct); if (id is not null) await ownership.MarkOwnedAsync(id, ct); } _startupTask = null; return ready; }
    public async Task<OperationResult> StopAppAsync(string appId, CancellationToken ct = default) { var app = registry.Find(appId); if (app is null) return OperationResult.Failure($"App is not registered: {appId}"); var appSemaphore = _appLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1)); await appSemaphore.WaitAsync(ct); await using var gate = new AsyncGate(appSemaphore); return await compose.StopProjectAsync(app, ct); }
    public async Task<OperationResult> SoftStopAsync(CancellationToken ct = default) { await _dockerLock.WaitAsync(ct); try { foreach (var app in registry.Apps) await StopAppAsync(app.Id, ct); var running = await containers.ListRunningContainersAsync(ct); if (running.Count > 0) return OperationResult.Success("Registered apps stopped; other containers remain running."); var id = await docker.GetSessionIdentityAsync(ct); if (ownership.IsCurrentSessionOwned(id)) { var result = await docker.StopAsync(false, ct); if (result.Succeeded) await ownership.ClearAsync(ct); return result; } return OperationResult.Success("Docker was left running because ownership could not be confirmed."); } finally { _dockerLock.Release(); } }
    public async Task<OperationResult> HardStopAsync(CancellationToken ct = default) { await _dockerLock.WaitAsync(ct); try { foreach (var app in registry.Apps) await StopAppAsync(app.Id, ct); foreach (var container in await containers.ListRunningContainersAsync(ct)) await containers.StopContainerAsync(container, ct); var result = await docker.StopAsync(false, ct); await ownership.ClearAsync(ct); return result; } finally { _dockerLock.Release(); } }
    public async Task<AppRuntimeStatus> GetStatusAsync(string appId, CancellationToken ct = default) { var app = registry.Find(appId) ?? throw new KeyNotFoundException($"App is not registered: {appId}"); var project = await compose.GetProjectStatusAsync(app, ct); return new(app, project.IsHealthy ? AppRuntimeState.Running : project.IsRunning ? AppRuntimeState.Degraded : AppRuntimeState.Stopped, project); }
    private sealed class AsyncGate(SemaphoreSlim semaphore) : IAsyncDisposable { public ValueTask DisposeAsync() { semaphore.Release(); return ValueTask.CompletedTask; } }
}
