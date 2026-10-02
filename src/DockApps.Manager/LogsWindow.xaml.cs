using System.Windows;
using DockApps.Core;

namespace DockApps.Manager;

public partial class LogsWindow : Window
{
    private readonly RegisteredApp _app;
    private readonly IComposeService _compose;
    private CancellationTokenSource? _loadCancellation;
    private bool _closed;

    public LogsWindow(RegisteredApp app, IComposeService compose)
    {
        InitializeComponent();
        _app = app;
        _compose = compose;
        TitleText.Text = $"{app.Name} — docker compose logs";
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
        };
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

    private async void RefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_closed) return;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        StatusText.Text = "Loading a read-only log snapshot...";
        LogsTextBox.Text = string.Empty;
        try
        {
            var logs = await _compose.GetLogsAsync(_app, token);
            if (_closed || token.IsCancellationRequested) return;
            LogsTextBox.Text = string.IsNullOrWhiteSpace(logs) ? "No logs were returned." : logs;
            LogsTextBox.ScrollToEnd();
            StatusText.Text = "Read-only snapshot loaded. Refresh to query Docker again.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load logs: {ex.Message}";
            LogsTextBox.Text = ex.ToString();
        }
    }
}
