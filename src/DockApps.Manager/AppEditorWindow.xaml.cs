using System.IO;
using System.Windows;
using Microsoft.Win32;
using DockApps.Core;

namespace DockApps.Manager;

public partial class AppEditorWindow : Window
{
    private readonly RegisteredApp? _existing;
    public RegisteredApp? App { get; private set; }
    public bool ShouldCreateStartMenuShortcut { get; private set; } = true;
    public bool ShouldCreateDesktopShortcut { get; private set; }

    public AppEditorWindow(RegisteredApp? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is null) { OpenBrowserBox.IsChecked = true; return; }
        NameBox.Text = existing.Name; IdBox.Text = existing.Id; ComposeFileBox.Text = existing.ComposeFile;
        ProjectBox.Text = existing.ProjectName; UrlBox.Text = existing.Url ?? string.Empty; IconBox.Text = existing.IconPath ?? string.Empty;
        OpenBrowserBox.IsChecked = existing.OpenBrowser; AutoCloseBox.IsChecked = existing.AutoCloseAfterStart;
    }

    private void BrowseComposeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Compose files|compose.y*ml;docker-compose.y*ml|All files|*.*" };
        if (dialog.ShowDialog(this) == true) ComposeFileBox.Text = dialog.FileName;
    }

    private void BrowseIconClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Image files|*.ico;*.png;*.jpg;*.jpeg|All files|*.*" };
        if (dialog.ShowDialog(this) == true) IconBox.Text = dialog.FileName;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim(); var id = IdBox.Text.Trim(); var compose = ComposeFileBox.Text.Trim(); var project = ProjectBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(compose) || string.IsNullOrWhiteSpace(project)) { ErrorText.Text = "Name, app ID, Compose file, and project name are required."; return; }
        if (id.Any(char.IsWhiteSpace) || id.Any(char.IsControl)) { ErrorText.Text = "App ID must not contain whitespace or control characters."; return; }
        if (!File.Exists(compose)) { ErrorText.Text = "The selected Compose file does not exist."; return; }
        if (!string.IsNullOrWhiteSpace(UrlBox.Text) && (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))) { ErrorText.Text = "URL must be a valid HTTP or HTTPS URL."; return; }
        App = new RegisteredApp { Id = id, Name = name, ComposeFile = compose, ProjectName = project, Url = string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim(), IconPath = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim(), OpenBrowser = OpenBrowserBox.IsChecked == true, AutoCloseAfterStart = AutoCloseBox.IsChecked == true, StartupTimeoutSeconds = _existing?.StartupTimeoutSeconds ?? 60, StopTimeoutSeconds = _existing?.StopTimeoutSeconds ?? 30 };
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) { DialogResult = false; }
}
