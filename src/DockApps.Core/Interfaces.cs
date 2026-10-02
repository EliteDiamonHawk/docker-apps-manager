namespace DockApps.Core;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}
public interface IDockerDesktopService
{
    Task<DockerDesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> StartAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> StopAsync(bool force = false, CancellationToken cancellationToken = default);
    Task<OperationResult> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string?> GetSessionIdentityAsync(CancellationToken cancellationToken = default);
}
public interface IComposeService
{
    Task<ComposeProjectStatus> GetProjectStatusAsync(RegisteredApp app, CancellationToken cancellationToken = default);
    Task<OperationResult> StartProjectAsync(RegisteredApp app, CancellationToken cancellationToken = default);
    Task<OperationResult> StopProjectAsync(RegisteredApp app, CancellationToken cancellationToken = default);
    Task<string> GetLogsAsync(RegisteredApp app, CancellationToken cancellationToken = default);
}
public interface IDockerContainerService
{
    Task<IReadOnlyList<ContainerStatus>> ListRunningContainersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContainerStatus>> ListAllContainersAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> StopContainerAsync(ContainerStatus container, CancellationToken cancellationToken = default);
}
public interface IAppRegistry
{
    IReadOnlyList<RegisteredApp> Apps { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    RegisteredApp? Find(string id);
    IReadOnlyList<string> Validate();
    Task AddAsync(RegisteredApp app, CancellationToken cancellationToken = default);
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
public interface IDockerOwnershipService
{
    DockerSessionState Current { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task MarkOwnedAsync(string sessionIdentity, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    bool IsCurrentSessionOwned(string? sessionIdentity);
}
public interface IBrowserService { Task<OperationResult> OpenAsync(string url, CancellationToken cancellationToken = default); }
public interface IShortcutService { Task CreateAsync(RegisteredApp app, string launcherPath, CancellationToken cancellationToken = default); Task DeleteAsync(RegisteredApp app, CancellationToken cancellationToken = default); }
public interface IAppLifecycleService
{
    Task<AppRuntimeStatus> OpenAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<OperationResult> StopAppAsync(string appId, CancellationToken cancellationToken = default);
    Task<OperationResult> SoftStopAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> HardStopAsync(CancellationToken cancellationToken = default);
    Task<AppRuntimeStatus> GetStatusAsync(string appId, CancellationToken cancellationToken = default);
}
public interface IIdleShutdownCoordinator { void AddWindow(); void RemoveWindow(); void AddOperation(); void RemoveOperation(); event EventHandler? ShutdownRequested; }
