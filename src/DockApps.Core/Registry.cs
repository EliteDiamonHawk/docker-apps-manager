using System.Text.Json;

namespace DockApps.Core;

public sealed class JsonAppRegistry : IAppRegistry
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private List<RegisteredApp> _apps = [];

    public JsonAppRegistry(string? root = null)
    {
        var directory = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps");
        _path = Path.Combine(directory, "apps.json");
    }

    public IReadOnlyList<RegisteredApp> Apps => _apps.ToArray();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The DockApps registry path has no parent directory.");
            Directory.CreateDirectory(directory);
            if (!File.Exists(_path))
            {
                _apps = [];
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                _apps = await JsonSerializer.DeserializeAsync<List<RegisteredApp>>(stream, _options, cancellationToken) ?? [];
            }
            catch (JsonException)
            {
                _apps = [];
                RecoverMalformedRegistry();
            }
            catch (NotSupportedException)
            {
                _apps = [];
                RecoverMalformedRegistry();
            }
            catch (IOException)
            {
                _apps = [];
            }
            catch (UnauthorizedAccessException)
            {
                _apps = [];
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public RegisteredApp? Find(string id) => _apps.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> Validate() => ValidateApps(_apps, checkComposeFiles: true);

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task AddAsync(RegisteredApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var candidate = _apps.Append(app).ToList();
            var errors = ValidateApps(candidate, checkComposeFiles: false);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
            _apps.Add(app);
            try
            {
                await SaveCoreAsync(cancellationToken);
            }
            catch
            {
                _apps.Remove(app);
                throw;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var removed = _apps.Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            _apps.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            try
            {
                await SaveCoreAsync(cancellationToken);
            }
            catch
            {
                _apps.AddRange(removed);
                throw;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The DockApps registry path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(_apps, _options), cancellationToken);
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

    private void RecoverMalformedRegistry()
    {
        try
        {
            var backupPath = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json";
            File.Move(_path, backupPath);
        }
        catch
        {
            // The in-memory registry remains empty and safe even if backup cannot be created.
        }
    }

    private static List<string> ValidateApps(IEnumerable<RegisteredApp> apps, bool checkComposeFiles)
    {
        var list = apps.ToList();
        var errors = new List<string>();
        foreach (var group in list.Where(x => !string.IsNullOrWhiteSpace(x.Id)).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Duplicate app ID: {group.Key}");
        foreach (var group in list.Where(x => !string.IsNullOrWhiteSpace(x.ProjectName)).GroupBy(x => x.ProjectName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"Duplicate project name: {group.Key}");

        foreach (var app in list)
        {
            if (string.IsNullOrWhiteSpace(app.Id)) errors.Add("App ID is required");
            if (string.IsNullOrWhiteSpace(app.Name)) errors.Add($"App name is required for {app.Id ?? "<unknown>"}");
            if (string.IsNullOrWhiteSpace(app.ComposeFile)) errors.Add($"Compose file is required for {app.Id ?? "<unknown>"}");
            else if (checkComposeFiles && !File.Exists(app.ComposeFile)) errors.Add($"Compose file missing: {app.ComposeFile}");
            if (string.IsNullOrWhiteSpace(app.ProjectName)) errors.Add($"Project name is required for {app.Id ?? "<unknown>"}");
            if (app.Url is not null && (!Uri.TryCreate(app.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                errors.Add($"Invalid URL for {app.Id ?? "<unknown>"}");
            if (app.StartupTimeoutSeconds <= 0) errors.Add($"Startup timeout must be positive for {app.Id ?? "<unknown>"}");
            if (app.StopTimeoutSeconds <= 0) errors.Add($"Stop timeout must be positive for {app.Id ?? "<unknown>"}");
        }
        return errors;
    }
}
