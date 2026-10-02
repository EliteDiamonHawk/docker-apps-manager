using System.Diagnostics;
using System.Text.Json;

namespace DockApps.Core;

public sealed class DockerDesktopService(IProcessRunner runner) : IDockerDesktopService
{
    public async Task<DockerDesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("docker", ["info", "--format", "{{json .ServerVersion}}"], TimeSpan.FromSeconds(10), cancellationToken);
        return result.Succeeded ? new(DockerEngineState.Ready) : new(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe")) ? DockerEngineState.EngineNotReady : DockerEngineState.NotInstalled, result.StandardError.Trim());
    }
    public async Task<OperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(exe)) return OperationResult.Failure("Docker Desktop was not found.");
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); return OperationResult.Success(); } catch (Exception ex) { return OperationResult.Failure(ex.Message); }
    }
    public Task<OperationResult> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => WaitAsync(timeout, cancellationToken);
    private async Task<OperationResult> WaitAsync(TimeSpan timeout, CancellationToken ct) { var end = DateTime.UtcNow + timeout; while (DateTime.UtcNow < end) { if ((await GetStatusAsync(ct)).IsReady) return OperationResult.Success(); await Task.Delay(1000, ct); } return OperationResult.Failure("Docker Engine did not become ready before the timeout."); }
    public async Task<OperationResult> StopAsync(bool force = false, CancellationToken cancellationToken = default) { var args = new List<string> { "desktop", "stop" }; if (force) args.Add("--force"); var r = await runner.RunAsync("docker", args, TimeSpan.FromSeconds(30), cancellationToken); return r.Succeeded ? OperationResult.Success() : OperationResult.Failure(r.StandardError, r.ExitCode); }
    public async Task<string?> GetSessionIdentityAsync(CancellationToken cancellationToken = default) { var r = await runner.RunAsync("docker", ["info", "--format", "{{.DockerRootDir}}|{{.ServerVersion}}"], TimeSpan.FromSeconds(10), cancellationToken); return r.Succeeded ? r.StandardOutput.Trim() : null; }
}

public sealed class DockerContainerService(IProcessRunner runner) : IDockerContainerService
{
    public Task<IReadOnlyList<ContainerStatus>> ListRunningContainersAsync(CancellationToken ct = default) => ListAsync(true, ct);
    public Task<IReadOnlyList<ContainerStatus>> ListAllContainersAsync(CancellationToken ct = default) => ListAsync(false, ct);
    private async Task<IReadOnlyList<ContainerStatus>> ListAsync(bool running, CancellationToken ct) { var args = new List<string> { "ps" }; if (!running) args.Add("-a"); args.AddRange(["--format", "{{json .}}"]); var r = await runner.RunAsync("docker", args, TimeSpan.FromSeconds(15), ct); if (!r.Succeeded) return []; var list = new List<ContainerStatus>(); foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { try { using var d = JsonDocument.Parse(line); var x = d.RootElement; list.Add(new(x.GetProperty("ID").GetString() ?? "", x.GetProperty("Names").GetString() ?? "", x.GetProperty("State").GetString() ?? "", null, x.TryGetProperty("Labels", out var labels) ? labels.GetString()?.Split(',').FirstOrDefault(v => v.StartsWith("com.docker.compose.project="))?.Split('=').Last() : null, null, x.GetProperty("Image").GetString(), null)); } catch (JsonException) { } } return list; }
    public async Task<OperationResult> StopContainerAsync(ContainerStatus container, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["stop", "--time", "30", container.Id], TimeSpan.FromSeconds(45), ct); return r.Succeeded ? OperationResult.Success() : OperationResult.Failure(r.StandardError, r.ExitCode); }
}

public sealed class ComposeService(IProcessRunner runner) : IComposeService
{
    public async Task<ComposeProjectStatus> GetProjectStatusAsync(RegisteredApp app, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "ps", "-a", "--format", "json"], TimeSpan.FromSeconds(20), ct); if (!r.Succeeded) return new(app.ProjectName, []); try { var json = JsonDocument.Parse(r.StandardOutput); var items = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray() : []; return new(app.ProjectName, items.Select(x => new ContainerStatus(x.GetProperty("ID").GetString() ?? "", x.GetProperty("Name").GetString() ?? "", x.GetProperty("State").GetString() ?? "", x.TryGetProperty("Health", out var h) ? h.GetString() : null, app.ProjectName, x.TryGetProperty("Service", out var s) ? s.GetString() : null, x.TryGetProperty("Image", out var i) ? i.GetString() : null, null)).ToArray()); } catch (JsonException) { return new(app.ProjectName, []); } }
    public async Task<OperationResult> StartProjectAsync(RegisteredApp app, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "up", "-d"], TimeSpan.FromMinutes(5), ct); return r.Succeeded ? OperationResult.Success() : OperationResult.Failure(r.StandardError, r.ExitCode); }
    public async Task<OperationResult> StopProjectAsync(RegisteredApp app, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "stop", "-t", app.StopTimeoutSeconds.ToString()], TimeSpan.FromSeconds(app.StopTimeoutSeconds + 15), ct); return r.Succeeded ? OperationResult.Success() : OperationResult.Failure(r.StandardError, r.ExitCode); }
    public async Task<string> GetLogsAsync(RegisteredApp app, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "logs", "--no-color"], TimeSpan.FromMinutes(2), ct); return r.StandardOutput + r.StandardError; }
}
