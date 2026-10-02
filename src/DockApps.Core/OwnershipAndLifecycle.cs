using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DockApps.Core;

public sealed class JsonDockerOwnershipService(string? root = null) : IDockerOwnershipService
{
    private readonly string _path = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps"), "state.json");
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public DockerSessionState Current { get; private set; } = new(false, null, null);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                Current = EmptyState;
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var state = await JsonSerializer.DeserializeAsync<DockerSessionState>(stream, cancellationToken: cancellationToken);
                Current = Normalize(state);
            }
            catch (JsonException)
            {
                Current = EmptyState;
            }
            catch (IOException)
            {
                Current = EmptyState;
            }
            catch (UnauthorizedAccessException)
            {
                Current = EmptyState;
            }
            catch (NotSupportedException)
            {
                Current = EmptyState;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task MarkOwnedAsync(string sessionIdentity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionIdentity)) throw new ArgumentException("A non-empty Docker session identity is required.", nameof(sessionIdentity));
        await _fileLock.WaitAsync(cancellationToken);
        var previous = Current;
        try
        {
            Current = new(true, sessionIdentity, DateTimeOffset.UtcNow);
            try
            {
                await SaveCoreAsync(cancellationToken);
            }
            catch
            {
                Current = previous;
                throw;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        var previous = Current;
        try
        {
            Current = EmptyState;
            try
            {
                await SaveCoreAsync(cancellationToken);
            }
            catch
            {
                Current = previous;
                throw;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool IsCurrentSessionOwned(string? sessionIdentity) =>
        Current.StartedByDockApps &&
        !string.IsNullOrWhiteSpace(Current.SessionIdentity) &&
        !string.IsNullOrWhiteSpace(sessionIdentity) &&
        string.Equals(sessionIdentity, Current.SessionIdentity, StringComparison.Ordinal);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The DockApps state path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(Current), cancellationToken);
            File.Move(tempPath, _path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // A failed cleanup must not hide the original write failure.
            }
        }
    }

    private static DockerSessionState Normalize(DockerSessionState? state)
    {
        if (state is null || !state.StartedByDockApps || string.IsNullOrWhiteSpace(state.SessionIdentity) || state.StartedAt is null)
            return EmptyState;
        return state;
    }

    private static DockerSessionState EmptyState => new(false, null, null);
}

public sealed class BrowserService : IBrowserService
{
    public Task<OperationResult> OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(OperationResult.Failure(ex.Message));
        }
    }
}

public sealed class AppLifecycleService(
    IDockerDesktopService docker,
    IComposeService compose,
    IDockerContainerService containers,
    IAppRegistry registry,
    IDockerOwnershipService ownership,
    IBrowserService browser) : IAppLifecycleService
{
    private readonly SemaphoreSlim _dockerLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _appLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startupTaskLock = new();
    private Task<OperationResult>? _startupTask;

    public async Task<AppRuntimeStatus> OpenAppAsync(string appId, CancellationToken ct = default)
    {
        var app = registry.Find(appId) ?? throw new KeyNotFoundException($"App is not registered: {appId}");
        await _dockerLock.WaitAsync(ct);
        try
        {
            var appLock = _appLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
            await appLock.WaitAsync(ct);
            try
            {
                return await OpenAppCoreAsync(app, ct);
            }
            finally
            {
                appLock.Release();
            }
        }
        finally
        {
            _dockerLock.Release();
        }
    }

    private async Task<AppRuntimeStatus> OpenAppCoreAsync(RegisteredApp app, CancellationToken ct)
    {
        var existing = await compose.GetProjectStatusAsync(app, ct);
        if (existing.IsHealthy)
            return await CompleteOpenAsync(app, existing, ct);

        var ready = await EnsureDockerReadyUnderLockAsync(app.StartupTimeoutSeconds, ct);
        if (!ready.Succeeded)
            return ErrorStatus(app, existing, ready.Message);

        existing = await compose.GetProjectStatusAsync(app, ct);
        if (existing.HasInspectionError)
            return ErrorStatus(app, existing, existing.Diagnostic);

        if (!existing.IsRunning)
        {
            var started = await compose.StartProjectAsync(app, ct);
            if (!started.Succeeded)
                return ErrorStatus(app, existing, started.Message);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, app.StartupTimeoutSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            existing = await compose.GetProjectStatusAsync(app, ct);
            if (existing.HasInspectionError)
                return ErrorStatus(app, existing, existing.Diagnostic);
            if (existing.IsHealthy)
                return await CompleteOpenAsync(app, existing, ct);

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, remaining.TotalMilliseconds))), ct);
        }

        return new(app, existing.IsRunning ? AppRuntimeState.Degraded : AppRuntimeState.Error, existing, "The Compose project did not become healthy before the timeout.");
    }

    private async Task<AppRuntimeStatus> CompleteOpenAsync(RegisteredApp app, ComposeProjectStatus project, CancellationToken ct)
    {
        if (!app.OpenBrowser || string.IsNullOrWhiteSpace(app.Url))
            return new(app, AppRuntimeState.Running, project);
        var browserResult = await browser.OpenAsync(app.Url, ct);
        return new(app, AppRuntimeState.Running, project, browserResult.Succeeded ? null : $"Running, but browser could not be opened: {browserResult.Message}");
    }

    private static AppRuntimeStatus ErrorStatus(RegisteredApp app, ComposeProjectStatus project, string? message) =>
        new(app, AppRuntimeState.Error, project, message ?? "Docker or Compose inspection failed.");

    private async Task<OperationResult> EnsureDockerReadyUnderLockAsync(int timeoutSeconds, CancellationToken ct)
    {
        var status = await docker.GetStatusAsync(ct);
        if (status.IsReady) return OperationResult.Success();

        Task<OperationResult> startup;
        lock (_startupTaskLock)
        {
            if (_startupTask is null)
            {
                var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _startupTask = completion.Task;
                _ = RunDockerStartupAsync(completion, timeoutSeconds);
            }
            startup = _startupTask;
        }

        // A caller may stop waiting without cancelling the shared startup operation.
        return await startup.WaitAsync(ct);
    }

    private async Task RunDockerStartupAsync(TaskCompletionSource<OperationResult> completion, int timeoutSeconds)
    {
        OperationResult result;
        try
        {
            var started = await docker.StartAsync(CancellationToken.None);
            if (!started.Succeeded)
            {
                result = started;
            }
            else
            {
                var ready = await docker.WaitUntilReadyAsync(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), CancellationToken.None);
                if (!ready.Succeeded)
                {
                    result = ready;
                }
                else
                {
                    string? identity = null;
                    try { identity = await docker.GetSessionIdentityAsync(CancellationToken.None); }
                    catch { /* Ownership is intentionally left unconfirmed. */ }

                    if (string.IsNullOrWhiteSpace(identity))
                    {
                        result = OperationResult.Success("Docker is ready, but its session identity could not be recorded; automatic shutdown will remain disabled.");
                    }
                    else
                    {
                        try
                        {
                            await ownership.MarkOwnedAsync(identity, CancellationToken.None);
                            result = OperationResult.Success();
                        }
                        catch (Exception ex)
                        {
                            result = OperationResult.Success($"Docker is ready, but ownership could not be persisted: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result = OperationResult.Failure($"Docker startup failed: {ex.Message}");
        }
        finally
        {
            lock (_startupTaskLock)
            {
                if (ReferenceEquals(_startupTask, completion.Task)) _startupTask = null;
            }
        }
        completion.TrySetResult(result);
    }

    public async Task<OperationResult> StopAppAsync(string appId, CancellationToken ct = default)
    {
        var app = registry.Find(appId);
        if (app is null) return OperationResult.Failure($"App is not registered: {appId}");

        await _dockerLock.WaitAsync(ct);
        try
        {
            return await StopAppUnderLockAsync(app, ct);
        }
        finally
        {
            _dockerLock.Release();
        }
    }

    private async Task<OperationResult> StopAppUnderLockAsync(RegisteredApp app, CancellationToken ct)
    {
        var appLock = _appLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        await appLock.WaitAsync(ct);
        try
        {
            return await compose.StopProjectAsync(app, ct);
        }
        finally
        {
            appLock.Release();
        }
    }

    public async Task<OperationResult> SoftStopAsync(CancellationToken ct = default)
    {
        await _dockerLock.WaitAsync(ct);
        try
        {
            var failures = new List<string>();
            foreach (var app in registry.Apps.ToArray())
            {
                var result = await StopAppUnderLockAsync(app, ct);
                if (!result.Succeeded) failures.Add($"{app.Name}: {FormatResult(result)}");
            }

            IReadOnlyList<ContainerStatus> running;
            try
            {
                running = await containers.ListRunningContainersAsync(ct);
            }
            catch (DockerInspectionException ex)
            {
                failures.Add($"Container inspection: {ex.Message}");
                return FailureFrom("Soft Stop could not safely inspect Docker containers", failures);
            }

            if (failures.Count > 0)
            {
                if (running.Count > 0) failures.Add($"{running.Count} container(s) remain running.");
                return FailureFrom("Soft Stop completed with failures; Docker was left running", failures);
            }

            if (running.Count > 0)
                return OperationResult.Success("Registered apps stopped; other containers remain running.");

            string? identity;
            try { identity = await docker.GetSessionIdentityAsync(ct); }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Docker session could not be verified; Docker was left running: {ex.Message}");
            }
            if (string.IsNullOrWhiteSpace(identity))
                return OperationResult.Failure("Docker session could not be verified; Docker was left running.");
            if (!ownership.IsCurrentSessionOwned(identity))
                return OperationResult.Success("Docker was left running because DockApps ownership could not be confirmed.");

            var stopped = await docker.StopAsync(false, ct);
            if (!stopped.Succeeded) return OperationResult.Failure($"Docker could not be stopped: {FormatResult(stopped)}", stopped.ExitCode);
            try
            {
                await ownership.ClearAsync(ct);
            }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Docker stopped, but ownership state could not be cleared: {ex.Message}");
            }
            return OperationResult.Success();
        }
        finally
        {
            _dockerLock.Release();
        }
    }

    public async Task<OperationResult> HardStopAsync(CancellationToken ct = default)
    {
        await _dockerLock.WaitAsync(ct);
        try
        {
            var failures = new List<string>();
            foreach (var app in registry.Apps.ToArray())
            {
                var result = await StopAppUnderLockAsync(app, ct);
                if (!result.Succeeded) failures.Add($"{app.Name}: {FormatResult(result)}");
            }

            IReadOnlyList<ContainerStatus> running;
            try
            {
                running = await containers.ListRunningContainersAsync(ct);
            }
            catch (DockerInspectionException ex)
            {
                failures.Add($"Container inspection: {ex.Message}");
                return FailureFrom("Hard Stop could not safely inspect Docker containers; Docker was left running", failures);
            }

            foreach (var container in running)
            {
                var result = await containers.StopContainerAsync(container, ct);
                if (!result.Succeeded) failures.Add($"{container.Name}: {FormatResult(result)}");
            }

            var stopped = await docker.StopAsync(false, ct);
            var usedForce = false;
            if (!stopped.Succeeded)
            {
                usedForce = true;
                var forced = await docker.StopAsync(true, ct);
                if (!forced.Succeeded)
                {
                    failures.Add($"Docker normal stop: {FormatResult(stopped)}");
                    failures.Add($"Docker forced stop: {FormatResult(forced)}");
                }
                else
                {
                    stopped = forced;
                }
            }

            if (!stopped.Succeeded)
                return FailureFrom("Hard Stop could not stop Docker", failures);

            try
            {
                await ownership.ClearAsync(ct);
            }
            catch (Exception ex)
            {
                failures.Add($"Ownership state: {ex.Message}");
            }

            if (failures.Count > 0)
                return FailureFrom(usedForce ? "Hard Stop used forced Docker shutdown but completed with failures" : "Hard Stop completed with failures", failures);
            return OperationResult.Success(usedForce ? "Docker was stopped using forced shutdown." : null);
        }
        finally
        {
            _dockerLock.Release();
        }
    }

    public async Task<AppRuntimeStatus> GetStatusAsync(string appId, CancellationToken ct = default)
    {
        var app = registry.Find(appId) ?? throw new KeyNotFoundException($"App is not registered: {appId}");
        await _dockerLock.WaitAsync(ct);
        try
        {
            var project = await compose.GetProjectStatusAsync(app, ct);
            if (project.HasInspectionError)
                return ErrorStatus(app, project, project.Diagnostic);
            var state = project.IsHealthy ? AppRuntimeState.Running : project.IsRunning ? AppRuntimeState.Degraded : AppRuntimeState.Stopped;
            return new(app, state, project);
        }
        finally
        {
            _dockerLock.Release();
        }
    }

    private static OperationResult FailureFrom(string prefix, IEnumerable<string> failures) =>
        OperationResult.Failure(prefix + ": " + string.Join("; ", failures));

    private static string FormatResult(OperationResult result) => string.IsNullOrWhiteSpace(result.Message) ? "operation failed" : result.Message;
}
