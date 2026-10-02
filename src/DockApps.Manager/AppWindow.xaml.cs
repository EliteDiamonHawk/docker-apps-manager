using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DockApps.Core;

namespace DockApps.Manager;

public partial class AppWindow : Window
{
    private readonly RegisteredApp _app;
    private readonly IAppLifecycleService _lifecycle;
    private readonly IDockerDesktopService? _docker;
    private readonly IComposeService _compose;
    private readonly IBrowserService _browser;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ObservableCollection<ServiceRow> _services = [];
    private LogsWindow? _logsWindow;
    private bool _closed;
    private bool _busy;

    public AppWindow(
        RegisteredApp app,
        IAppLifecycleService lifecycle,
        IDockerDesktopService? docker = null,
        IComposeService? compose = null,
        IBrowserService? browser = null)
    {
        InitializeComponent();
        _app = app;
        _lifecycle = lifecycle;
        _docker = docker;
        _compose = compose ?? new ComposeService(new ProcessRunner());
        _browser = browser ?? new BrowserService();
        AppName.Text = app.Name;
        AppId.Text = app.Id;
        AppUrl.Text = app.Url ?? "No URL configured";
        ServicesList.ItemsSource = _services;
        CopyUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(app.Url);
        OpenAgainButton.IsEnabled = !string.IsNullOrWhiteSpace(app.Url);

        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _pollTimer.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_closed) _pollTimer.Start();
            else _pollTimer.Stop();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _pollTimer.Stop();
            _logsWindow?.Close();
        };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public bool IsClosed => _closed;

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async Task RefreshAsync()
    {
        if (_closed || !await _refreshGate.WaitAsync(0)) return;
        try
        {
            if (_docker is not null)
            {
                try
                {
                    var dockerStatus = await _docker.GetStatusAsync();
                    DockerStatus.Text = dockerStatus.Diagnostic is null
                        ? dockerStatus.State.ToString()
                        : $"{dockerStatus.State}: {dockerStatus.Diagnostic}";
                }
                catch (Exception ex) { DockerStatus.Text = $"Unavailable: {ex.Message}"; }
            }

            var status = await _lifecycle.GetStatusAsync(_app.Id);
            Status.Text = status.State.ToString();
            Status.Foreground = status.State switch
            {
                AppRuntimeState.Running => System.Windows.Media.Brushes.SeaGreen,
                AppRuntimeState.Error => System.Windows.Media.Brushes.Firebrick,
                AppRuntimeState.Degraded => System.Windows.Media.Brushes.DarkOrange,
                _ => System.Windows.Media.Brushes.DimGray
            };
            StatusMessage.Text = status.Message ?? (status.State == AppRuntimeState.Running ? "Ready." : "");
            _services.Clear();
            foreach (var container in status.Project?.Containers ?? []) _services.Add(new ServiceRow(container));
        }
        catch (Exception ex)
        {
            Status.Text = "Error";
            StatusMessage.Text = ex.Message;
            _services.Clear();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Starting...", async () =>
        {
            var result = await _lifecycle.OpenAppAsync(_app.Id);
            Status.Text = result.State.ToString();
            StatusMessage.Text = result.Message ?? (result.State == AppRuntimeState.Running ? "Ready." : "Startup did not complete.");
            await RefreshAsync();
            if (result.State == AppRuntimeState.Running && _app.AutoCloseAfterStart)
            {
                await Task.Delay(1200);
                if (!_closed) Close();
            }
        });
    }

    private async void StopClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Stopping...", async () =>
        {
            var result = await _lifecycle.StopAppAsync(_app.Id);
            StatusMessage.Text = result.Message ?? (result.Succeeded ? "Stopped." : "Stop failed.");
            await RefreshAsync();
        });
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void LogsClick(object sender, RoutedEventArgs e)
    {
        if (_logsWindow is not null && !_logsWindow.IsClosed)
        {
            _logsWindow.BringToFront();
            return;
        }

        _logsWindow = new LogsWindow(_app, _compose) { Owner = this };
        _logsWindow.Closed += (_, _) => _logsWindow = null;
        _logsWindow.Show();
    }

    private void CopyUrlClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_app.Url)) return;
        Clipboard.SetText(_app.Url);
        StatusMessage.Text = "URL copied to the clipboard.";
    }

    private async void OpenAgainClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_app.Url)) return;
        var result = await _browser.OpenAsync(_app.Url);
        StatusMessage.Text = result.Succeeded ? "Browser opened." : $"Browser could not be opened: {result.Message}";
    }

    private async Task RunOperationAsync(string initialMessage, Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        OpenButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        LogsButton.IsEnabled = false;
        StatusMessage.Text = initialMessage;
        try { await operation(); }
        catch (Exception ex) { Status.Text = "Error"; StatusMessage.Text = ex.Message; }
        finally
        {
            _busy = false;
            OpenButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            LogsButton.IsEnabled = true;
        }
    }

    public sealed class ServiceRow
    {
        public ServiceRow(ContainerStatus container)
        {
            Name = string.IsNullOrWhiteSpace(container.Service) ? container.Name : container.Service;
            State = container.State;
            Health = container.Health ?? "none";
        }

        public string Name { get; }
        public string State { get; }
        public string Health { get; }
    }
}
