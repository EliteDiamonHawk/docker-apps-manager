using System.Text.Json;

namespace DockApps.Core;

public sealed class JsonAppRegistry : IAppRegistry
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private List<RegisteredApp> _apps = [];
    public JsonAppRegistry(string? root = null) => _path = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps"), "apps.json");
    public IReadOnlyList<RegisteredApp> Apps => _apps;
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path)) { _apps = []; return; }
        try { _apps = await JsonSerializer.DeserializeAsync<List<RegisteredApp>>(File.OpenRead(_path), _options, cancellationToken) ?? []; }
        catch (JsonException) { _apps = []; }
    }
    public RegisteredApp? Find(string id) => _apps.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var group in _apps.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)) errors.Add($"Duplicate app ID: {group.Key}");
        foreach (var group in _apps.GroupBy(x => x.ProjectName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)) errors.Add($"Duplicate project name: {group.Key}");
        foreach (var app in _apps) { if (string.IsNullOrWhiteSpace(app.Id)) errors.Add("App ID is required"); if (!File.Exists(app.ComposeFile)) errors.Add($"Compose file missing: {app.ComposeFile}"); if (app.Url is not null && !Uri.TryCreate(app.Url, UriKind.Absolute, out _)) errors.Add($"Invalid URL for {app.Id}"); }
        return errors;
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_apps, _options), cancellationToken);
        File.Move(temp, _path, true);
    }
    public async Task AddAsync(RegisteredApp app, CancellationToken cancellationToken = default) { if (Find(app.Id) is not null) throw new InvalidOperationException($"App already exists: {app.Id}"); _apps.Add(app); await SaveAsync(cancellationToken); }
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default) { _apps.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); await SaveAsync(cancellationToken); }
}
