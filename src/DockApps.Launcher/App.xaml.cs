using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using DockApps.Core;

namespace DockApps.Launcher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); var args = e.Args; var requestType = args.Length > 0 && args[0].Equals("--manager", StringComparison.OrdinalIgnoreCase) ? "open-manager" : "open-app"; var appId = requestType == "open-app" && args.Length > 0 ? args[0] : null; if (requestType == "open-app" && string.IsNullOrWhiteSpace(appId)) { MessageBox.Show("Usage: dockapps <app-id> or dockapps --manager", "DockApps"); Shutdown(2); return; }
        var request = new ManagerRequest(IpcProtocol.Version, requestType, appId, Guid.NewGuid().ToString("N"));
        for (var attempt = 0; attempt < 2; attempt++) { try { await SendAsync(request); Shutdown(); return; } catch (IOException) when (attempt == 0) { StartManager(); await Task.Delay(300); } catch (TimeoutException) when (attempt == 0) { StartManager(); await Task.Delay(300); } }
        MessageBox.Show("DockApps Manager could not be started.", "DockApps"); Shutdown(1);
    }
    private static void StartManager() { var manager = Path.Combine(AppContext.BaseDirectory, "DockApps.Manager.exe"); if (File.Exists(manager)) Process.Start(new ProcessStartInfo(manager) { UseShellExecute = false, CreateNoWindow = true }); }
    private static async Task SendAsync(ManagerRequest request) { using var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await pipe.ConnectAsync(cts.Token); await IpcProtocol.WriteAsync(pipe, request, cts.Token); var response = await IpcProtocol.ReadAsync<ManagerResponse>(pipe, cts.Token); if (response is null || !response.Accepted) throw new IOException(response?.Error ?? "No response from manager."); }
}
