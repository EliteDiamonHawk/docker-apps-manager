namespace DockApps.Core;

public sealed record RegisteredApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ComposeFile { get; init; }
    public required string ProjectName { get; init; }
    public string? Url { get; init; }
    public string? IconPath { get; init; }
    public bool OpenBrowser { get; init; } = true;
    public bool AutoCloseAfterStart { get; init; }
    public int StartupTimeoutSeconds { get; init; } = 60;
    public int StopTimeoutSeconds { get; init; } = 30;
}

public enum DockerEngineState { Unknown, NotInstalled, DesktopStopped, DesktopStarting, EngineNotReady, Ready }
public sealed record DockerDesktopStatus(DockerEngineState State, string? Diagnostic = null)
{
    public bool IsReady => State == DockerEngineState.Ready;
    public bool IsDesktopRunning => State is DockerEngineState.DesktopStarting or DockerEngineState.EngineNotReady or DockerEngineState.Ready;
}
public sealed record DockerSessionState(bool StartedByDockApps, string? SessionIdentity, DateTimeOffset? StartedAt);
public sealed record ContainerStatus(string Id, string Name, string State, string? Health, string? Project, string? Service, string? Image, int? ExitCode);
public sealed record ComposeProjectStatus(string ProjectName, IReadOnlyList<ContainerStatus> Containers, string? Diagnostic = null)
{
    public bool HasInspectionError => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool IsRunning => !HasInspectionError && Containers.Any(x => x.State.Equals("running", StringComparison.OrdinalIgnoreCase));
    public bool IsHealthy => !HasInspectionError && Containers.Count > 0 && Containers.All(x =>
        x.State.Equals("running", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(x.Health) ||
         x.Health.Equals("healthy", StringComparison.OrdinalIgnoreCase) ||
         x.Health.Equals("none", StringComparison.OrdinalIgnoreCase)));
}
public enum AppRuntimeState { Unknown, Stopped, Starting, Running, Degraded, Error }
public sealed record AppRuntimeStatus(RegisteredApp App, AppRuntimeState State, ComposeProjectStatus? Project, string? Message = null);
public sealed record OperationResult(bool Succeeded, string? Message = null, int ExitCode = 0)
{
    public static OperationResult Success(string? message = null) => new(true, message);
    public static OperationResult Failure(string message, int exitCode = -1) => new(false, message, exitCode);
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
public sealed record ManagerRequest(int Version, string Type, string? AppId, string RequestId);
public sealed record ManagerResponse(int Version, bool Accepted, string RequestId, string? Error = null);
