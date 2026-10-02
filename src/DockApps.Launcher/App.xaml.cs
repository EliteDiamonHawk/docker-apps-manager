using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using DockApps.Core;

namespace DockApps.Launcher;

public partial class App : Application
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (!TryCreateRequest(e.Args, out var request, out var usageError))
            {
                MessageBox.Show(usageError, "DockApps", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(2);
                return;
            }

            using var timeout = new CancellationTokenSource(StartupTimeout);
            await SendWithRetryAsync(request!, timeout.Token).ConfigureAwait(true);
            Shutdown(0);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("DockApps Manager did not become available before the startup timeout expired.", "DockApps", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            MessageBox.Show($"DockApps Manager could not be reached.\n\n{ex.Message}", "DockApps", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool TryCreateRequest(string[] args, out ManagerRequest? request, out string error)
    {
        request = null;

        if (args.Length == 1 && args[0].Equals("--manager", StringComparison.OrdinalIgnoreCase))
        {
            request = new ManagerRequest(IpcProtocol.Version, "open-manager", null, Guid.NewGuid().ToString("N"));
            error = string.Empty;
            return true;
        }

        if (args.Length == 1 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            request = new ManagerRequest(IpcProtocol.Version, "open-app", args[0], Guid.NewGuid().ToString("N"));
            error = string.Empty;
            return true;
        }

        error = "Usage: dockapps <app-id> or dockapps --manager";
        return false;
    }

    private static async Task SendWithRetryAsync(ManagerRequest request, CancellationToken cancellationToken)
    {
        var managerStarted = false;
        Exception? lastFailure = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendAsync(request, AttemptTimeout, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException ex)
            {
                lastFailure = ex;
            }
            catch (IOException ex)
            {
                lastFailure = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastFailure = ex;
            }
            catch (JsonException ex)
            {
                lastFailure = ex;
            }
            catch (InvalidDataException ex)
            {
                lastFailure = ex;
            }

            if (!managerStarted)
            {
                StartManager();
                managerStarted = true;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The manager did not accept the request before the startup timeout expired.", lastFailure);
    }

    private static void StartManager()
    {
        var managerPath = Path.Combine(AppContext.BaseDirectory, "DockApps.Manager.exe");
        if (!File.Exists(managerPath))
            throw new FileNotFoundException("DockApps.Manager.exe was not found beside the launcher.", managerPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = managerPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static async Task SendAsync(ManagerRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(attemptCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to DockApps Manager.");
        }

        await IpcProtocol.WriteAsync(pipe, request, attemptCancellation.Token).ConfigureAwait(false);
        var response = await IpcProtocol.ReadAsync<ManagerResponse>(pipe, attemptCancellation.Token).ConfigureAwait(false);

        if (response is null)
            throw new InvalidDataException("DockApps Manager returned an empty response.");
        if (response.Version != IpcProtocol.Version)
            throw new InvalidDataException("DockApps Manager returned an unsupported protocol version.");
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            throw new InvalidDataException("DockApps Manager returned a response for a different request.");
        if (!response.Accepted)
            throw new IOException(response.Error ?? "DockApps Manager rejected the request.");
    }
}
