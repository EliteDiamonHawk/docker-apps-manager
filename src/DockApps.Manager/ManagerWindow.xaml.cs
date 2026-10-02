using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DockApps.Core;

namespace DockApps.Manager;

public partial class ManagerWindow : Window
{
    private static WeakReference<ManagerWindow>? _current;

    private readonly IAppRegistry _registry;
    private readonly IAppLifecycleService _lifecycle;
    private readonly IDockerContainerService _containers;
    private readonly IDockerDesktopService _docker;
    private readonly IDockerOwnershipService _ownership;
    private readonly IComposeService _compose;
    private readonly IShortcutService _shortcuts;
    private readonly ObservableCollection<AppRow> _appRows = [];
    private readonly ObservableCollection<ContainerRow> _containerRows = [];
    private readonly Dictionary<string, AppWindow> _appWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _busy;

    public static ManagerWindow? Current
    {
        get => _current is not null && _current.TryGetTarget(out var window) && !window.IsClosed ? window : null;
    }

    private bool IsClosed { get; set; }

    public ManagerWindow(
        IAppRegistry registry,
        IAppLifecycleService lifecycle,
        IDockerContainerService containers,
        IDockerDesktopService docker,
        IComposeService? compose = null,
        IShortcutService? shortcuts = null,
        IDockerOwnershipService? ownership = null)
    {
        InitializeComponent();
        _registry = registry;
        _lifecycle = lifecycle;
        _containers = containers;
        _docker = docker;
        _compose = compose ?? new ComposeService(new ProcessRunner());
        _shortcuts = shortcuts ?? new WindowsShortcutService();
        _ownership = ownership ?? new JsonDockerOwnershipService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps"));

        _current = new WeakReference<ManagerWindow>(this);
        AppsList.ItemsSource = _appRows;
        ContainersList.ItemsSource = _containerRows;
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await _ownership.LoadAsync();
            await RefreshAsync();
            _pollTimer.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _pollTimer.Start();
            else _pollTimer.Stop();
        };
        Closed += (_, _) =>
        {
            IsClosed = true;
            _pollTimer.Stop();
            if (_current is not null && _current.TryGetTarget(out var current) && ReferenceEquals(current, this)) _current = null;
        };
    }

    public static bool TryActivateExisting()
    {
        var window = Current;
        if (window is null) return false;
        window.Dispatcher.BeginInvoke(new Action(window.BringToFront));
        return true;
    }

    /// <summary>
    /// Routes IPC-launched app requests through the manager's canonical window registry.
    /// </summary>
    public static bool TryShowAppWindow(string appId)
    {
        var window = Current;
        if (window is null) return false;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var app = window._registry.Find(appId);
            if (app is null)
            {
                window.StatusMessage.Text = $"App is not registered: {appId}";
                window.BringToFront();
                return;
            }

            window.ShowAppWindow(app);
        }));
        return true;
    }

    private RegisteredApp? SelectedApp => AppsList.SelectedItem is AppRow row ? _registry.Find(row.Id) : null;

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            DockerStatus.Text = "Refreshing Docker status...";
            StatusMessage.Text = "Refreshing application and container state...";

            var dockerTask = _docker.GetStatusAsync();
            var containersTask = _containers.ListAllContainersAsync();
            DockerDesktopStatus dockerStatus;
            IReadOnlyList<ContainerStatus> containers;
            try
            {
                await Task.WhenAll(dockerTask, containersTask);
                dockerStatus = await dockerTask;
                containers = await containersTask;
            }
            catch (Exception ex)
            {
                dockerStatus = new DockerDesktopStatus(DockerEngineState.Unknown, ex.Message);
                containers = [];
            }

            string? sessionIdentity = null;
            if (dockerStatus.IsReady)
            {
                try { sessionIdentity = await _docker.GetSessionIdentityAsync(); } catch { }
            }

            SetDockerStatus(dockerStatus);
            DockerOwnershipStatus.Text = FormatOwnership(sessionIdentity);

            _appRows.Clear();
            var appStatusTasks = _registry.Apps.Select(GetAppRowAsync).ToArray();
            foreach (var row in await Task.WhenAll(appStatusTasks)) _appRows.Add(row);

            _containerRows.Clear();
            var projects = _registry.Apps.Select(x => x.ProjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var container in containers.Where(x => x.Project is null || !projects.Contains(x.Project)))
            {
                _containerRows.Add(new ContainerRow(container));
            }

            var validation = _registry.Validate();
            ValidationMessage.Text = validation.Count == 0
                ? string.Empty
                : $"Registry needs attention: {string.Join("; ", validation.Take(3))}";
            StatusMessage.Text = $"{_appRows.Count} registered app(s), {_containerRows.Count} external/unregistered container(s).";
        }
        catch (Exception ex)
        {
            DockerStatus.Text = "Unable to read Docker status";
            DockerOwnershipStatus.Text = ex.Message;
            StatusMessage.Text = "Refresh failed.";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<AppRow> GetAppRowAsync(RegisteredApp app)
    {
        try
        {
            var status = await _lifecycle.GetStatusAsync(app.Id);
            var services = status.Project?.Containers.Count ?? 0;
            return new AppRow(app, status.State.ToString(), services, status.Project?.IsHealthy == true, status.Message);
        }
        catch (Exception ex)
        {
            return new AppRow(app, AppRuntimeState.Error.ToString(), 0, false, ex.Message);
        }
    }

    private void SetDockerStatus(DockerDesktopStatus status)
    {
        DockerStatus.Text = status.Diagnostic is null
            ? $"Docker: {status.State}"
            : $"Docker: {status.State} — {status.Diagnostic}";
        DockerStatusIndicator.Fill = status.State switch
        {
            DockerEngineState.Ready => System.Windows.Media.Brushes.SeaGreen,
            DockerEngineState.DesktopStarting or DockerEngineState.EngineNotReady => System.Windows.Media.Brushes.DarkOrange,
            DockerEngineState.NotInstalled => System.Windows.Media.Brushes.Firebrick,
            _ => System.Windows.Media.Brushes.Gray
        };
    }

    private string FormatOwnership(string? sessionIdentity)
    {
        if (!_ownership.Current.StartedByDockApps) return "Docker ownership: DockApps did not start the current known session.";
        if (!_ownership.IsCurrentSessionOwned(sessionIdentity)) return "Docker ownership: stored session is stale or could not be confirmed.";
        return $"Docker ownership: DockApps-owned session started {_ownership.Current.StartedAt?.ToLocalTime():g}.";
    }

    private void ShowAppWindow(RegisteredApp app)
    {
        if (_appWindows.TryGetValue(app.Id, out var existing) && !existing.IsClosed)
        {
            existing.BringToFront();
            return;
        }

        var window = new AppWindow(app, _lifecycle, _docker, _compose, new BrowserService());
        _appWindows[app.Id] = window;
        window.Owner = this;
        window.Closed += (_, _) => _appWindows.Remove(app.Id);
        window.Show();
        window.BringToFront();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void AppsSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelectionButtons();

    private void UpdateSelectionButtons()
    {
        var enabled = !_busy && SelectedApp is not null;
        EditButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        ShortcutButton.IsEnabled = enabled;
        DesktopShortcutButton.IsEnabled = enabled;
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void AppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedApp is { } app) ShowAppWindow(app);
    }

    private async void RegisterClick(object sender, RoutedEventArgs e)
    {
        var editor = new AppEditorWindow { Owner = this };
        if (editor.ShowDialog() == true) await SaveEditedAppAsync(editor, null);
    }

    private async void EditClick(object sender, RoutedEventArgs e)
    {
        var existing = SelectedApp;
        if (existing is null) return;
        var editor = new AppEditorWindow(existing) { Owner = this };
        if (editor.ShowDialog() == true) await SaveEditedAppAsync(editor, existing);
    }

    private async Task SaveEditedAppAsync(AppEditorWindow editor, RegisteredApp? existing)
    {
        var app = editor.App;
        if (app is null) return;
        if (_registry.Apps.Any(x => !string.Equals(x.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
            && x.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(this, $"An app with ID '{app.Id}' is already registered.", "Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_registry.Apps.Any(x => !string.Equals(x.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
            && x.ProjectName.Equals(app.ProjectName, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(this, $"Project name '{app.ProjectName}' is already registered.", "Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            if (existing is not null) await _registry.RemoveAsync(existing.Id);
            try
            {
                await _registry.AddAsync(app);
            }
            catch
            {
                if (existing is not null) await _registry.AddAsync(existing);
                throw;
            }

            var shortcutMessage = string.Empty;
            if (editor.ShouldCreateStartMenuShortcut)
            {
                var result = await _shortcuts.CreateAsync(app, ShortcutPaths.FindLauncherPath());
                shortcutMessage = result.Message ?? string.Empty;
            }
            if (editor.ShouldCreateDesktopShortcut && _shortcuts is IDesktopShortcutService desktop)
            {
                var result = await desktop.CreateDesktopAsync(app, ShortcutPaths.FindLauncherPath());
                if (!result.Succeeded) shortcutMessage = result.Message ?? string.Empty;
            }
            StatusMessage.Text = string.IsNullOrWhiteSpace(shortcutMessage) ? "Application saved." : $"Application saved. {shortcutMessage}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Could not save application", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        var app = SelectedApp;
        if (app is null) return;
        if (System.Windows.MessageBox.Show(this, $"Remove '{app.Name}' from DockApps? Running containers will not be stopped.", "Remove application", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            await _registry.RemoveAsync(app.Id);
            await _shortcuts.DeleteAsync(app);
            if (_shortcuts is IDesktopShortcutService desktop) await desktop.DeleteDesktopAsync(app);
            if (_appWindows.TryGetValue(app.Id, out var window)) window.Close();
            StatusMessage.Text = $"Removed {app.Name}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Could not remove application", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ShortcutClick(object sender, RoutedEventArgs e)
    {
        var app = SelectedApp;
        if (app is null) return;
        SetBusy(true);
        try
        {
            var result = await _shortcuts.CreateAsync(app, ShortcutPaths.FindLauncherPath());
            StatusMessage.Text = result.Message ?? (result.Succeeded ? "Start Menu shortcut created." : "Shortcut creation failed.");
        }
        catch (Exception ex) { StatusMessage.Text = $"Shortcut creation failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void DesktopShortcutClick(object sender, RoutedEventArgs e)
    {
        var app = SelectedApp;
        if (app is null) return;
        if (_shortcuts is not IDesktopShortcutService desktop)
        {
            StatusMessage.Text = "The configured shortcut service does not support Desktop shortcuts.";
            return;
        }
        SetBusy(true);
        try
        {
            var result = await desktop.CreateDesktopAsync(app, ShortcutPaths.FindLauncherPath());
            StatusMessage.Text = result.Message ?? (result.Succeeded ? "Desktop shortcut created." : "Shortcut creation failed.");
        }
        catch (Exception ex) { StatusMessage.Text = $"Shortcut creation failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void ManagerShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_shortcuts is not IManagerShortcutService managerShortcuts)
        {
            StatusMessage.Text = "The configured shortcut service does not support manager shortcuts.";
            return;
        }
        SetBusy(true);
        try
        {
            var result = await managerShortcuts.CreateManagerAsync(ShortcutPaths.FindLauncherPath());
            StatusMessage.Text = result.Message ?? (result.Succeeded ? "Manager shortcut created." : "Shortcut creation failed.");
        }
        catch (Exception ex) { StatusMessage.Text = $"Shortcut creation failed: {ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void SoftStopClick(object sender, RoutedEventArgs e) => await RunLifecycleOperationAsync(
        "Soft Stop",
        () => _lifecycle.SoftStopAsync());

    private async void HardStopClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "Stop all containers and Docker Desktop? Docker data will not be deleted.", "Confirm Hard Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunLifecycleOperationAsync("Hard Stop", () => _lifecycle.HardStopAsync());
    }

    private async Task RunLifecycleOperationAsync(string name, Func<Task<OperationResult>> operation)
    {
        SetBusy(true);
        try
        {
            var result = await operation();
            StatusMessage.Text = result.Message ?? (result.Succeeded ? $"{name} completed." : $"{name} failed.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage.Text = $"{name} failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        RefreshButton.IsEnabled = !value;
        RegisterButton.IsEnabled = !value;
        SoftStopButton.IsEnabled = !value;
        HardStopButton.IsEnabled = !value;
        ManagerShortcutButton.IsEnabled = !value;
        UpdateSelectionButtons();
    }

    public sealed class AppRow
    {
        public AppRow(RegisteredApp app, string state, int services, bool healthy, string? message)
        {
            Id = app.Id;
            Name = app.Name;
            ProjectName = app.ProjectName;
            Url = app.Url ?? "—";
            State = healthy ? "Healthy" : state;
            Services = services.ToString();
            Message = message ?? string.Empty;
        }

        public string Id { get; }
        public string Name { get; }
        public string ProjectName { get; }
        public string Url { get; }
        public string State { get; }
        public string Services { get; }
        public string Message { get; }
    }

    public sealed class ContainerRow
    {
        public ContainerRow(ContainerStatus container)
        {
            Name = container.Name;
            State = container.State;
            Health = container.Health ?? "—";
            Project = container.Project ?? "Unlabelled / external";
            Image = container.Image ?? "—";
        }

        public string Name { get; }
        public string State { get; }
        public string Health { get; }
        public string Project { get; }
        public string Image { get; }
    }
}
