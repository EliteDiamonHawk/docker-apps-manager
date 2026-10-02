using System.Windows;
using DockApps.Core;

namespace DockApps.Manager;

public partial class ManagerWindow : Window
{
    private readonly IAppRegistry _registry; private readonly IAppLifecycleService _lifecycle; private readonly IDockerContainerService _containers; private readonly IDockerDesktopService _docker;
    public ManagerWindow(IAppRegistry registry, IAppLifecycleService lifecycle, IDockerContainerService containers, IDockerDesktopService docker) { InitializeComponent(); _registry = registry; _lifecycle = lifecycle; _containers = containers; _docker = docker; Loaded += async (_, _) => await RefreshAsync(); }
    private async Task RefreshAsync() { DockerStatus.Text = (await _docker.GetStatusAsync()).State.ToString(); AppsList.ItemsSource = _registry.Apps; }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void AppDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (AppsList.SelectedItem is RegisteredApp app) new AppWindow(app, _lifecycle).Show(); }
    private async void SoftStopClick(object sender, RoutedEventArgs e) { var result = await _lifecycle.SoftStopAsync(); MessageBox.Show(result.Message ?? (result.Succeeded ? "Soft Stop completed." : "Soft Stop failed."), "DockApps"); await RefreshAsync(); }
    private async void HardStopClick(object sender, RoutedEventArgs e) { if (MessageBox.Show("Stop all containers and Docker Desktop? Docker data will not be deleted.", "Confirm Hard Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; var result = await _lifecycle.HardStopAsync(); MessageBox.Show(result.Message ?? (result.Succeeded ? "Hard Stop completed." : "Hard Stop failed."), "DockApps"); await RefreshAsync(); }
}
