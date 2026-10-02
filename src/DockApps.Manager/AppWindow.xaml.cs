using System.Windows;
using DockApps.Core;

namespace DockApps.Manager;

public partial class AppWindow : Window
{
    private readonly RegisteredApp _app; private readonly IAppLifecycleService _lifecycle;
    public AppWindow(RegisteredApp app, IAppLifecycleService lifecycle) { InitializeComponent(); _app = app; _lifecycle = lifecycle; AppName.Text = app.Name; Loaded += async (_, _) => await RefreshAsync(); }
    private async Task RefreshAsync() { try { var status = await _lifecycle.GetStatusAsync(_app.Id); Status.Text = $"{status.State}\n{status.Message ?? "Ready"}"; } catch (Exception ex) { Status.Text = ex.Message; } }
    private async void OpenClick(object sender, RoutedEventArgs e) { Status.Text = "Starting..."; var result = await _lifecycle.OpenAppAsync(_app.Id); Status.Text = $"{result.State}\n{result.Message ?? "Ready"}"; }
    private async void StopClick(object sender, RoutedEventArgs e) { var result = await _lifecycle.StopAppAsync(_app.Id); Status.Text = result.Message ?? (result.Succeeded ? "Stopped." : "Stop failed."); }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void LogsClick(object sender, RoutedEventArgs e) { MessageBox.Show("Logs are available through the Compose service and will be shown in a future logs window.", "DockApps"); await Task.CompletedTask; }
}
