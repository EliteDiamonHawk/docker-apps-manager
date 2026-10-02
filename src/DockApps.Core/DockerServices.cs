using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace DockApps.Core;

public sealed class DockerInspectionException : InvalidOperationException
{
    public DockerInspectionException(string message, int exitCode = -1) : base(message) => ExitCode = exitCode;
    public int ExitCode { get; }
}

public sealed class DockerDesktopService(IProcessRunner runner) : IDockerDesktopService
{
    public async Task<DockerDesktopStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("docker", ["info", "--format", "{{json .ServerVersion}}"], TimeSpan.FromSeconds(10), cancellationToken);
        if (result.Succeeded) return new(DockerEngineState.Ready);

        var diagnostic = FailureMessage(result, "Docker Engine did not respond.");
        var dockerInstalled = FindDockerCli() is not null;
        var desktopInstalled = FindDockerDesktop() is not null;
        var desktopRunning = IsDockerDesktopRunning();

        if (!dockerInstalled && !desktopInstalled)
            return new(DockerEngineState.NotInstalled, diagnostic);
        if (desktopInstalled && !desktopRunning)
            return new(DockerEngineState.DesktopStopped, diagnostic);
        return new(DockerEngineState.EngineNotReady, diagnostic);
    }
    public async Task<OperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var exe = FindDockerDesktop();
        if (exe is null) return OperationResult.Failure("Docker Desktop was not found.");
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); return OperationResult.Success(); } catch (Exception ex) { return OperationResult.Failure(ex.Message); }
    }
    public Task<OperationResult> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => WaitAsync(timeout, cancellationToken);
    private async Task<OperationResult> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return OperationResult.Failure("Docker Engine did not become ready before the timeout.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? diagnostic = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(ct);
            diagnostic = status.Diagnostic;
            if (status.IsReady) return OperationResult.Success();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, remaining.TotalMilliseconds))), ct);
        }
        return OperationResult.Failure(string.IsNullOrWhiteSpace(diagnostic)
            ? "Docker Engine did not become ready before the timeout."
            : $"Docker Engine did not become ready before the timeout: {diagnostic}");
    }

    public async Task<OperationResult> StopAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "desktop", "stop" };
        if (force) args.Add("--force");
        var result = await runner.RunAsync("docker", args, TimeSpan.FromSeconds(30), cancellationToken);
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(FailureMessage(result, "Docker Desktop could not be stopped."), result.ExitCode);
    }

    public async Task<string?> GetSessionIdentityAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("docker", ["info", "--format", "{{json .}}"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!result.Succeeded) return null;

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var engineId = GetString(root, "ID", "Id");
            var rootDirectory = GetString(root, "DockerRootDir");
            var serverVersion = GetString(root, "ServerVersion");
            var engineName = GetString(root, "Name");
            var desktopStart = GetDockerDesktopStartTime();

            // A root directory and version are reusable across daemon restarts. Require a
            // daemon ID or a local Desktop process start marker before claiming ownership.
            // Without the engine's own identity we cannot distinguish a restarted
            // Docker session from the session recorded in state.json. Failing closed
            // disables automatic shutdown rather than risking an external shutdown.
            if (string.IsNullOrWhiteSpace(engineId)) return null;
            var material = string.Join("|", new[]
            {
                $"engine={engineId}",
                $"desktop-start={desktopStart?.ToUniversalTime().Ticks}",
                $"root={rootDirectory}",
                $"version={serverVersion}",
                $"name={engineName}"
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FailureMessage(ProcessResult result, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string? FindDockerCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Docker", "Docker", "resources", "bin", "docker.exe")
        };
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            candidates = candidates.Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "docker.exe"))).ToArray();
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindDockerDesktop() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker", "Docker", "Docker Desktop.exe")
    }.FirstOrDefault(File.Exists);

    private static bool IsDockerDesktopRunning()
    {
        try { return Process.GetProcessesByName("Docker Desktop").Length > 0; }
        catch { return false; }
    }

    private static DateTimeOffset? GetDockerDesktopStartTime()
    {
        try
        {
            var process = Process.GetProcessesByName("Docker Desktop").OrderBy(x => x.Id).FirstOrDefault();
            return process?.StartTime.ToUniversalTime();
        }
        catch { return null; }
    }
}

public sealed class DockerContainerService(IProcessRunner runner) : IDockerContainerService
{
    public Task<IReadOnlyList<ContainerStatus>> ListRunningContainersAsync(CancellationToken ct = default) => ListAsync(true, ct);
    public Task<IReadOnlyList<ContainerStatus>> ListAllContainersAsync(CancellationToken ct = default) => ListAsync(false, ct);
    private async Task<IReadOnlyList<ContainerStatus>> ListAsync(bool running, CancellationToken ct)
    {
        var args = new List<string> { "ps" };
        if (!running) args.Add("-a");
        args.AddRange(["--format", "{{json .}}"]);
        var result = await runner.RunAsync("docker", args, TimeSpan.FromSeconds(15), ct);
        if (!result.Succeeded)
            throw new DockerInspectionException(FailureMessage(result, "Docker container inspection failed."), result.ExitCode);
        return ParseContainerLines(result.StandardOutput);
    }

    public async Task<OperationResult> StopContainerAsync(ContainerStatus container, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("docker", ["stop", "--time", "30", container.Id], TimeSpan.FromSeconds(45), ct);
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(FailureMessage(result, $"Container '{container.Name}' could not be stopped."), result.ExitCode);
    }

    private static IReadOnlyList<ContainerStatus> ParseContainerLines(string output)
    {
        var list = new List<ContainerStatus>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = GetString(root, "ID");
                var name = GetString(root, "Names", "Name");
                var image = GetString(root, "Image");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    throw new DockerInspectionException("Docker returned a container without an ID or name.");
                var state = GetString(root, "State") ?? GetStateFromStatus(GetString(root, "Status"));
                var labels = GetString(root, "Labels");
                list.Add(new(id, name, state ?? "unknown", null, GetComposeLabel(labels, "com.docker.compose.project"), null, image, null));
            }
            catch (JsonException ex)
            {
                throw new DockerInspectionException($"Docker returned invalid container inspection JSON: {ex.Message}");
            }
        }
        return list;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string? GetStateFromStatus(string? status) => status?.StartsWith("Up", StringComparison.OrdinalIgnoreCase) == true ? "running" : status;

    private static string? GetComposeLabel(string? labels, string name)
    {
        if (string.IsNullOrWhiteSpace(labels)) return null;
        var prefix = name + "=";
        return labels.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static string FailureMessage(ProcessResult result, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
    }
}

public sealed class ComposeService(IProcessRunner runner) : IComposeService
{
    public async Task<ComposeProjectStatus> GetProjectStatusAsync(RegisteredApp app, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "ps", "-a", "--format", "json"], TimeSpan.FromSeconds(20), ct);
        if (!result.Succeeded)
            return new(app.ProjectName, [], FailureMessage(result, $"Compose inspection failed for '{app.ProjectName}'."));
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var items = EnumerateJsonObjects(document.RootElement).Select(x => new ContainerStatus(
                RequiredString(x, "ID"),
                RequiredString(x, "Name", "Names"),
                GetString(x, "State") ?? "unknown",
                GetString(x, "Health"),
                app.ProjectName,
                GetString(x, "Service"),
                GetString(x, "Image"),
                GetInt(x, "ExitCode"))).ToArray();
            return new(app.ProjectName, items);
        }
        catch (JsonException ex)
        {
            return new(app.ProjectName, [], $"Compose returned invalid inspection JSON: {ex.Message}");
        }
        catch (DockerInspectionException ex)
        {
            return new(app.ProjectName, [], ex.Message);
        }
    }

    public async Task<OperationResult> StartProjectAsync(RegisteredApp app, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "up", "-d"], TimeSpan.FromMinutes(5), ct);
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(FailureMessage(result, $"Compose project '{app.ProjectName}' could not be started."), result.ExitCode);
    }

    public async Task<OperationResult> StopProjectAsync(RegisteredApp app, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "stop", "-t", app.StopTimeoutSeconds.ToString()], TimeSpan.FromSeconds(app.StopTimeoutSeconds + 15), ct);
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(FailureMessage(result, $"Compose project '{app.ProjectName}' could not be stopped."), result.ExitCode);
    }

    private static IEnumerable<JsonElement> EnumerateJsonObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object) yield return root;
    }

    private static string RequiredString(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        if (string.IsNullOrWhiteSpace(value)) throw new DockerInspectionException("Compose returned a container without a required ID or name.");
        return value;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static string FailureMessage(ProcessResult result, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
    }

    public async Task<string> GetLogsAsync(RegisteredApp app, CancellationToken ct = default) { var r = await runner.RunAsync("docker", ["compose", "-f", app.ComposeFile, "-p", app.ProjectName, "logs", "--no-color"], TimeSpan.FromMinutes(2), ct); return r.StandardOutput + r.StandardError; }
}
