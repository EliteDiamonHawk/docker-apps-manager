using System.Windows;
using System.Windows.Threading;
using DockApps.Core;

namespace DockApps.Manager;

/// <summary>
/// Coordinates windows and routes accepted manager requests without coupling the IPC layer to WPF.
/// Existing windows can adopt these methods incrementally.
/// </summary>
public sealed class ManagerWindowCoordinator : IDisposable
{
    private readonly IAppRegistry _registry;
    private readonly IAppLifecycleService _lifecycle;
    private readonly IIdleShutdownCoordinator _idle;
    private readonly Func<ManagerWindow> _managerWindowFactory;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, WeakReference<AppWindow>> _appWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<AppWindow> _trackedAppWindows = [];
    private ManagerWindow? _managerWindow;
    private bool _managerWindowCounted;
    private bool _disposed;

    public ManagerWindowCoordinator(
        IAppRegistry registry,
        IAppLifecycleService lifecycle,
        IIdleShutdownCoordinator idle,
        Func<ManagerWindow> managerWindowFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _idle = idle ?? throw new ArgumentNullException(nameof(idle));
        _managerWindowFactory = managerWindowFactory ?? throw new ArgumentNullException(nameof(managerWindowFactory));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // This observes AppWindow instances created by the existing UI as well as instances
        // created through this coordinator, so idle shutdown does not close an unregistered window.
        EventManager.RegisterClassHandler(typeof(AppWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAppWindowLoaded));
    }

    public bool IsManagerWindowOpen => _managerWindow is { IsVisible: true };

    public bool IsAppWindowOpen(string appId)
    {
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("Window registry access must run on the WPF dispatcher.");

        return TryGetLiveAppWindow(appId, out _);
    }

    public ManagerWindow GetOrCreateManagerWindow()
    {
        VerifyDispatcherAccess();
        ThrowIfDisposed();

        if (_managerWindow is not null) return _managerWindow;

        _managerWindow = _managerWindowFactory();
        _managerWindow.Closed += OnManagerWindowClosed;
        return _managerWindow;
    }

    public void ShowManagerWindow()
    {
        VerifyDispatcherAccess();
        ThrowIfDisposed();

        var window = GetOrCreateManagerWindow();
        if (!window.IsVisible)
        {
            _managerWindowCounted = true;
            _idle.AddWindow();
            System.Windows.Application.Current.MainWindow = window;
            window.Show();
        }

        Activate(window);
    }

    public Task ShowManagerWindowAsync() => OnDispatcherAsync(ShowManagerWindow);

    public Task ShowAppWindowAsync(string appId, CancellationToken cancellationToken = default)
    {
        return OnDispatcherAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowOrFocusAppWindow(appId);
        });
    }

    public async Task HandleRequestAsync(ManagerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Type.Equals("open-manager", StringComparison.OrdinalIgnoreCase))
        {
            await ShowManagerWindowAsync().ConfigureAwait(false);
            return;
        }

        if (!request.Type.Equals("open-app", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.AppId))
            throw new InvalidOperationException("Unsupported manager request.");

        var app = _registry.Find(request.AppId);
        await ShowAppWindowAsync(request.AppId, cancellationToken).ConfigureAwait(false);
        if (app is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.OpenAppAsync(app.Id, cancellationToken).ConfigureAwait(false);
    }

    public void ShowOrFocusAppWindow(string appId)
    {
        VerifyDispatcherAccess();
        ThrowIfDisposed();

        // The WPF manager owns the canonical app-window registry. Reuse it for
        // IPC-launched requests so shortcut clicks cannot create a second window.
        if (ManagerWindow.TryShowAppWindow(appId)) return;

        var app = _registry.Find(appId) ?? throw new InvalidOperationException($"No registered app matches '{appId}'.");
        if (TryGetLiveAppWindow(app.Id, out var existing))
        {
            Activate(existing);
            return;
        }

        var window = new AppWindow(app, _lifecycle);
        _appWindows[app.Id] = new WeakReference<AppWindow>(window);
        window.Closed += (_, _) =>
        {
            if (_appWindows.TryGetValue(app.Id, out var reference) && reference.TryGetTarget(out var tracked) && ReferenceEquals(tracked, window))
                _appWindows.Remove(app.Id);
        };
        window.Show();
        Activate(window);
    }

    private void OnAppWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not AppWindow window || !_trackedAppWindows.Add(window)) return;

        _idle.AddWindow();
        window.Closed += OnTrackedAppWindowClosed;
    }

    private void OnTrackedAppWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not AppWindow window || !_trackedAppWindows.Remove(window)) return;
        _idle.RemoveWindow();
    }

    private void OnManagerWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not ManagerWindow window || !ReferenceEquals(window, _managerWindow)) return;

        _managerWindow = null;
        if (_managerWindowCounted)
        {
            _managerWindowCounted = false;
            _idle.RemoveWindow();
        }
    }

    private bool TryGetLiveAppWindow(string appId, out AppWindow window)
    {
        if (_appWindows.TryGetValue(appId, out var reference) && reference.TryGetTarget(out window!) && !window.IsDisposed())
            return true;

        _appWindows.Remove(appId);
        window = null!;
        return false;
    }

    private async Task OnDispatcherAsync(Action action)
    {
        VerifyNotDisposed();
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }

    private static void Activate(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Focus();
    }

    private void VerifyDispatcherAccess()
    {
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("Window registry access must run on the WPF dispatcher.");
    }

    private void VerifyNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ManagerWindowCoordinator));
    }

    private void ThrowIfDisposed() => VerifyNotDisposed();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _appWindows.Clear();
        _trackedAppWindows.Clear();
    }
}

internal static class WindowStateExtensions
{
    public static bool IsDisposed(this Window window) => !window.IsLoaded && !window.IsVisible;
}
