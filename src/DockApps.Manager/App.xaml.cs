using System.IO;
using System.Windows;
using DockApps.Core;

namespace DockApps.Manager;

public partial class App : Application
{
    private Mutex? _mutex; private CancellationTokenSource? _cts; private NamedPipeServer? _server;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); _mutex = new Mutex(true, "DockApps.Manager.SingleInstance", out var created); if (!created) { Shutdown(); return; }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps"); var runner = new ProcessRunner(); var registry = new JsonAppRegistry(root); await registry.LoadAsync(); var docker = new DockerDesktopService(runner); var compose = new ComposeService(runner); var containers = new DockerContainerService(runner); var ownership = new JsonDockerOwnershipService(root); await ownership.LoadAsync(); var lifecycle = new AppLifecycleService(docker, compose, containers, registry, ownership, new BrowserService()); _server = new NamedPipeServer(lifecycle); _cts = new CancellationTokenSource(); _ = _server.RunAsync(_cts.Token);
        var managerWindow = new ManagerWindow(registry, lifecycle, containers, docker); managerWindow.Closed += (_, _) => Shutdown(); MainWindow = managerWindow; managerWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _cts?.Cancel(); _mutex?.Dispose(); base.OnExit(e); }
}
