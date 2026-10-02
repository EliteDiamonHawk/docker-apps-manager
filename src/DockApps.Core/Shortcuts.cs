using System.Runtime.InteropServices;

namespace DockApps.Core;

/// <summary>
/// Optional manager-specific extension for the application shortcut service.
/// The base IShortcutService contract remains focused on registered apps.
/// </summary>
public interface IManagerShortcutService
{
    Task<OperationResult> CreateManagerAsync(string launcherPath, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateManagerDesktopAsync(string launcherPath, CancellationToken cancellationToken = default);
}

public interface IDesktopShortcutService
{
    Task<OperationResult> CreateDesktopAsync(RegisteredApp app, string launcherPath, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteDesktopAsync(RegisteredApp app, CancellationToken cancellationToken = default);
}

public static class ShortcutPaths
{
    public static string FindLauncherPath()
    {
        var directory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(directory, "dockapps.exe"),
            Path.Combine(directory, "DockApps.Launcher.exe"),
            Path.Combine(directory, "dockapps")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public static string GetAppStartMenuShortcutPath(RegisteredApp app) =>
        Path.Combine(GetStartMenuDirectory(), BuildAppShortcutFileName(app));

    public static string GetAppDesktopShortcutPath(RegisteredApp app) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), BuildAppShortcutFileName(app));

    public static string GetManagerStartMenuShortcutPath() =>
        Path.Combine(GetStartMenuDirectory(), "DockApps.lnk");

    public static string GetManagerDesktopShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DockApps.lnk");

    private static string GetStartMenuDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "DockApps");

    private static string BuildAppShortcutFileName(RegisteredApp app) =>
        $"{SanitizeFileName(app.Name, app.Id)}.lnk";

    internal static string SanitizeFileName(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}

/// <summary>
/// Creates ordinary Windows .lnk files through the Windows Script Host shortcut
/// COM object. No shell command or PowerShell script is involved.
/// </summary>
public sealed class WindowsShortcutService : IShortcutService, IManagerShortcutService, IDesktopShortcutService
{
    public Task<OperationResult> CreateAsync(RegisteredApp app, string launcherPath, CancellationToken cancellationToken = default) =>
        CreateLinkAsync(ShortcutPaths.GetAppStartMenuShortcutPath(app), launcherPath, app.Id, app.Name, app.IconPath, cancellationToken);

    public Task<OperationResult> DeleteAsync(RegisteredApp app, CancellationToken cancellationToken = default) =>
        DeleteLinkAsync(ShortcutPaths.GetAppStartMenuShortcutPath(app), cancellationToken);

    public Task<OperationResult> CreateDesktopAsync(RegisteredApp app, string launcherPath, CancellationToken cancellationToken = default) =>
        CreateLinkAsync(ShortcutPaths.GetAppDesktopShortcutPath(app), launcherPath, app.Id, app.Name, app.IconPath, cancellationToken);

    public Task<OperationResult> DeleteDesktopAsync(RegisteredApp app, CancellationToken cancellationToken = default) =>
        DeleteLinkAsync(ShortcutPaths.GetAppDesktopShortcutPath(app), cancellationToken);

    public Task<OperationResult> CreateManagerAsync(string launcherPath, CancellationToken cancellationToken = default) =>
        CreateLinkAsync(ShortcutPaths.GetManagerStartMenuShortcutPath(), launcherPath, "--manager", "Open DockApps manager", null, cancellationToken);

    public Task<OperationResult> CreateManagerDesktopAsync(string launcherPath, CancellationToken cancellationToken = default) =>
        CreateLinkAsync(ShortcutPaths.GetManagerDesktopShortcutPath(), launcherPath, "--manager", "Open DockApps manager", null, cancellationToken);

    private static Task<OperationResult> CreateLinkAsync(
        string shortcutPath,
        string targetPath,
        string arguments,
        string description,
        string? iconPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var directory = Path.GetDirectoryName(shortcutPath);
                if (string.IsNullOrWhiteSpace(directory)) return OperationResult.Failure("The shortcut directory could not be determined.");
                Directory.CreateDirectory(directory);

                var resolvedTarget = Path.GetFullPath(targetPath);
                var icon = !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath)
                    ? $"{Path.GetFullPath(iconPath)},0"
                    : File.Exists(resolvedTarget) ? $"{resolvedTarget},0" : $"{Environment.SystemDirectory}\\shell32.dll,0";

                object? shell = null;
                object? shortcut = null;
                try
                {
                    var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                        ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
                    shell = Activator.CreateInstance(shellType);
                    if (shell is null) throw new InvalidOperationException("Windows shortcut provider could not be created.");
                    dynamic dynamicShell = shell;
                    shortcut = dynamicShell.CreateShortcut(shortcutPath);
                    dynamic dynamicShortcut = shortcut;
                    dynamicShortcut.TargetPath = resolvedTarget;
                    dynamicShortcut.Arguments = QuoteArgument(arguments);
                    dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(resolvedTarget) ?? AppContext.BaseDirectory;
                    dynamicShortcut.Description = description;
                    dynamicShortcut.IconLocation = icon;
                    dynamicShortcut.Save();
                }
                finally
                {
                    ReleaseComObject(shortcut);
                    ReleaseComObject(shell);
                }

                return OperationResult.Success($"Shortcut created: {shortcutPath}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                return OperationResult.Failure($"Could not create shortcut: {ex.Message}");
            }
        }, cancellationToken);
    }

    private static Task<OperationResult> DeleteLinkAsync(string shortcutPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                return OperationResult.Success();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return OperationResult.Failure($"Could not remove shortcut: {ex.Message}");
            }
        }, cancellationToken);
    }

    private static string QuoteArgument(string value) =>
        value.Length == 0 ? string.Empty : value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
