using System.IO;
using System.Security.Principal;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using DockApps.Core;

namespace DockApps.Manager;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private NamedPipeServer? _server;
    private IdleShutdownCoordinator? _idle;
    private ManagerWindowCoordinator? _windows;
    private Forms.NotifyIcon? _trayIcon;
    private bool _shutdownRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, GetSingleInstanceName(), out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockApps");
            var runner = new ProcessRunner();
            var registry = new JsonAppRegistry(root);
            await registry.LoadAsync().ConfigureAwait(true);

            var docker = new DockerDesktopService(runner);
            var compose = new ComposeService(runner);
            var containers = new DockerContainerService(runner);
            var ownership = new JsonDockerOwnershipService(root);
            await ownership.LoadAsync().ConfigureAwait(true);
            var lifecycle = new AppLifecycleService(docker, compose, containers, registry, ownership, new BrowserService());

            _idle = new IdleShutdownCoordinator();
            _windows = new ManagerWindowCoordinator(
                registry,
                lifecycle,
                _idle,
                () => new ManagerWindow(registry, lifecycle, containers, docker, compose, new WindowsShortcutService(), ownership));
            _idle.ShutdownRequested += OnIdleShutdownRequested;

            var managerWindow = _windows.GetOrCreateManagerWindow();
            managerWindow.Closing += OnManagerWindowClosing;
            managerWindow.StateChanged += OnManagerWindowStateChanged;
            MainWindow = managerWindow;
            InitializeTrayIcon();
            _windows.ShowManagerWindow();

            _server = new NamedPipeServer(_windows.HandleRequestAsync, _idle);
            _cts = new CancellationTokenSource();
            _ = RunServerAsync(_server, _cts.Token);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"DockApps Manager could not start.\n\n{ex.Message}", "DockApps", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunServerAsync(NamedPipeServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void OnIdleShutdownRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            _shutdownRequested = true;
            Shutdown();
        }));
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open DockApps Manager");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowManagerFromTray));
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray));
        menu.Items.Add(exitItem);

        var icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? Drawing.SystemIcons.Application;
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "DockApps Manager",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowManagerFromTray));
    }

    private void ShowManagerFromTray()
    {
        if (Dispatcher.HasShutdownStarted || _windows is null) return;
        _windows.ShowManagerWindow();
    }

    private void ExitFromTray()
    {
        if (Dispatcher.HasShutdownStarted) return;
        _shutdownRequested = true;
        Shutdown();
    }

    private void OnManagerWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownRequested) return;
        e.Cancel = true;
        if (sender is Window window) window.Hide();
    }

    private static void OnManagerWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is not Window window || window.WindowState != WindowState.Minimized) return;
        window.WindowState = WindowState.Normal;
        window.Hide();
    }

    private static string GetSingleInstanceName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"Local\\DockApps.Manager.SingleInstance.{sid}";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _cts?.Cancel();
        _ = _server?.DisposeAsync();
        _windows?.Dispose();
        _idle?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
