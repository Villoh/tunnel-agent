using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using TunnelAgent.ViewModels;
using TunnelAgent.Views;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Services;

public sealed class TrayService : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _showUsageItem;
    private readonly NativeMenuItem _showHideItem;
    private TrayUsagePopup? _popup;
    private DateTime _popupShownAt;
    private readonly NativeMenuItem _cliProxyStartItem;
    private readonly NativeMenuItem _cliProxyStopItem;
    private readonly NativeMenuItem _cliProxyRestartItem;
    private readonly NativeMenuItem _cliProxyStatusItem;
    private readonly NativeMenuItem _perplexityStartItem;
    private readonly NativeMenuItem _perplexityStopItem;
    private readonly NativeMenuItem _perplexityRestartItem;
    private readonly NativeMenuItem _perplexityStatusItem;
    private readonly NativeMenuItem _nineRouterStartItem;
    private readonly NativeMenuItem _nineRouterStopItem;
    private readonly NativeMenuItem _nineRouterRestartItem;
    private readonly NativeMenuItem _nineRouterStatusItem;
    private bool _isQuitting;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    public TrayService(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        _desktop = desktop;
        _window = window;
        _viewModel = viewModel;

        _showUsageItem = CreateItem("Show Usage", (_, _) => TogglePopup());
        _showHideItem = CreateItem("Hide Window", (_, _) => ToggleWindow());

        _cliProxyStatusItem = CreateItem("Server: Stopped", null);
        _cliProxyStartItem = CreateItem("Start Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StartServerAsync()));
        _cliProxyStopItem = CreateItem("Stop Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync()));
        _cliProxyRestartItem = CreateItem("Restart Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.RestartEngineAsync()));

        _perplexityStatusItem = CreateItem("Server: Stopped", null);
        _perplexityStartItem = CreateItem("Start Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StartServerAsync()));
        _perplexityStopItem = CreateItem("Stop Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync()));
        _perplexityRestartItem = CreateItem("Restart Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.RestartEngineAsync()));

        _nineRouterStatusItem = CreateItem("Server: Stopped", null);
        _nineRouterStartItem = CreateItem("Start Server", async (_, _) => await RunForEngineAsync(EngineCatalog.NineRouter.Id, () => _viewModel.StartServerAsync()));
        _nineRouterStopItem = CreateItem("Stop Server", async (_, _) => await RunForEngineAsync(EngineCatalog.NineRouter.Id, () => _viewModel.StopServerAsync()));
        _nineRouterRestartItem = CreateItem("Restart Server", async (_, _) => await RunForEngineAsync(EngineCatalog.NineRouter.Id, () => _viewModel.RestartEngineAsync()));

        var cliProxyMenu = new NativeMenu
        {
            Items =
            {
                _cliProxyStatusItem,
                new NativeMenuItemSeparator(),
                _cliProxyStartItem,
                _cliProxyStopItem,
                _cliProxyRestartItem
            }
        };

        var perplexityMenu = new NativeMenu
        {
            Items =
            {
                _perplexityStatusItem,
                new NativeMenuItemSeparator(),
                _perplexityStartItem,
                _perplexityStopItem,
                _perplexityRestartItem
            }
        };

        var nineRouterMenu = new NativeMenu
        {
            Items =
            {
                _nineRouterStatusItem,
                new NativeMenuItemSeparator(),
                _nineRouterStartItem,
                _nineRouterStopItem,
                _nineRouterRestartItem
            }
        };

        var menu = new NativeMenu
        {
            Items =
            {
                _showUsageItem,
                _showHideItem,
                new NativeMenuItemSeparator(),
                new NativeMenuItem { Header = "CLIProxyAPI", Menu = cliProxyMenu },
                new NativeMenuItem { Header = "Perplexity", Menu = perplexityMenu },
                new NativeMenuItem { Header = "9Router", Menu = nineRouterMenu },
                new NativeMenuItemSeparator(),
                CreateItem("Configuration", (_, _) => ShowConfiguration()),
                CreateItem("Open Auth Folder", (_, _) => _viewModel.OpenAuthFolder()),
                CreateItem("Open Settings Folder", (_, _) => _viewModel.OpenSettingsFolder()),
                new NativeMenuItemSeparator(),
                CreateItem("Quit Tunnel Agent", async (_, _) => await QuitAsync())
            }
        };

        _trayIcon = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "Tunnel Agent",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => TogglePopup();

        _window.Closing += OnWindowClosing;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _desktop.ShutdownRequested += OnShutdownRequested;

        RefreshMenu();
        UpdateLogVisibility();
    }

    public bool IsQuitting => _isQuitting;

    public async Task QuitAsync()
    {
        if (_isQuitting) return;

        _isQuitting = true;
        await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync());
        await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync());
        await RunForEngineAsync(EngineCatalog.NineRouter.Id, () => _viewModel.StopServerAsync());
        _desktop.Shutdown();
    }

    private static WindowIcon LoadTrayIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://TunnelAgent/Assets/logo.ico"));
        return new WindowIcon(stream);
    }

    private static NativeMenuItem CreateItem(string header, EventHandler? click)
    {
        var item = new NativeMenuItem { Header = header };
        if (click is not null) item.Click += click;
        return item;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isQuitting) return;

        e.Cancel = true;
        _window.Hide();
        RefreshMenu();
        UpdateLogVisibility();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_isQuitting) return;

        _isQuitting = true;
        RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync()).GetAwaiter().GetResult();
        RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync()).GetAwaiter().GetResult();
        RunForEngineAsync(EngineCatalog.NineRouter.Id, () => _viewModel.StopServerAsync()).GetAwaiter().GetResult();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Window.IsVisible) || e.Property.Name == nameof(Window.WindowState))
        {
            RefreshMenu();
            UpdateLogVisibility();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EngineState) or nameof(MainWindowViewModel.ServerState)
            or nameof(MainWindowViewModel.CliProxyServerState) or nameof(MainWindowViewModel.PerplexityServerState)
            or nameof(MainWindowViewModel.NineRouterServerState))
            RefreshMenu();
    }

    private void TogglePopup()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(TogglePopup);
            return;
        }

        if (_popup is { IsVisible: true })
        {
            _popup.Hide();
            return;
        }

        ShowPopup();
    }

    private void ShowPopup()
    {
        if (_popup is null)
        {
            _popup = new TrayUsagePopup { DataContext = _viewModel };
            _popup.OpenMainWindowRequested += (_, _) =>
            {
                _popup?.Hide();
                ShowWindow();
            };
            _popup.Deactivated += OnPopupDeactivated;
        }

        _popup.Show();
        _ = _viewModel.RefreshNineRouterUsageAsync();
        PositionPopup(_popup);
        _popupShownAt = DateTime.UtcNow;
        _popup.Activate();
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        // Ignore the spurious deactivation that can fire right after showing.
        if ((DateTime.UtcNow - _popupShownAt).TotalMilliseconds < 250) return;
        _popup?.Hide();
    }

    private void PositionPopup(TrayUsagePopup popup)
    {
        var hasCursor = TryGetCursorPosition(out var cursor);
        var screen = hasCursor
            ? popup.Screens?.All?.FirstOrDefault(s => s.Bounds.Contains(cursor))
            : null;
        screen ??= popup.Screens?.Primary ?? popup.Screens?.All?.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(popup.Width * scale);
        var height = (int)(popup.Height * scale);
        var margin = (int)(8 * scale);

        popup.Position = hasCursor
            ? PositionNearCursor(area, cursor, width, height, margin)
            : new PixelPoint(
                area.X + area.Width - width - margin,
                OperatingSystem.IsWindows()
                    ? area.Y + area.Height - height - margin
                    : area.Y + margin);
    }

    internal static PixelPoint PositionNearCursor(
        PixelRect area,
        PixelPoint cursor,
        int popupWidth,
        int popupHeight,
        int margin)
    {
        var minX = area.X + margin;
        var maxX = Math.Max(minX, area.X + area.Width - popupWidth - margin);
        var minY = area.Y + margin;
        var maxY = Math.Max(minY, area.Y + area.Height - popupHeight - margin);
        var x = Math.Clamp(cursor.X - popupWidth + margin, minX, maxX);
        var y = cursor.Y <= area.Y + area.Height / 2
            ? Math.Min(cursor.Y + margin, maxY)
            : Math.Max(cursor.Y - popupHeight - margin, minY);
        return new PixelPoint(x, y);
    }

    private static bool TryGetCursorPosition(out PixelPoint cursor)
    {
        cursor = default;
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var point)) return false;
        cursor = new PixelPoint(point.X, point.Y);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
            _window.Hide();
        else
            ShowWindow();

        RefreshMenu();
        UpdateLogVisibility();
    }

    private void ShowWindow()
    {
        if (_desktop.MainWindow != _window)
            _desktop.MainWindow = _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        RefreshMenu();
        UpdateLogVisibility();
    }

    private void UpdateLogVisibility()
    {
        var visible = _window.IsVisible && _window.WindowState != WindowState.Minimized;
        _viewModel.SetWindowVisibleForLogs(visible);
    }

    private void ShowConfiguration()
    {
        _viewModel.SelectedSection = SectionKey.ConfigGeneral;
        ShowWindow();
    }

    private void RefreshMenu()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshMenu);
            return;
        }

        var visible = _window.IsVisible && _window.WindowState != WindowState.Minimized;
        _showHideItem.Header = visible ? "Hide Window" : "Show Window";
        RefreshEngineMenu(_cliProxyStatusItem, _cliProxyStartItem, _cliProxyStopItem, _cliProxyRestartItem, _viewModel.CliProxyServerState, _viewModel.CliProxyStatusText);
        RefreshEngineMenu(_perplexityStatusItem, _perplexityStartItem, _perplexityStopItem, _perplexityRestartItem, _viewModel.PerplexityServerState, _viewModel.PerplexityStatusText);
        RefreshEngineMenu(_nineRouterStatusItem, _nineRouterStartItem, _nineRouterStopItem, _nineRouterRestartItem, _viewModel.NineRouterServerState, _viewModel.NineRouterStatusText);
    }

    private async Task RunForEngineAsync(string engineId, Func<Task> action)
    {
        var previousActive = _viewModel.FocusedConfigEngineId;
        try
        {
            _viewModel.FocusedConfigEngineId = engineId;
            await action();
        }
        finally
        {
            _viewModel.FocusedConfigEngineId = previousActive;
        }
    }

    private static void RefreshEngineMenu(
        NativeMenuItem statusItem,
        NativeMenuItem startItem,
        NativeMenuItem stopItem,
        NativeMenuItem restartItem,
        ServerState serverState,
        string statusText)
    {
        statusItem.Header = statusText;
        statusItem.IsEnabled = false;

        var isBusy = serverState == ServerState.Starting;
        var isRunning = serverState == ServerState.Running;
        startItem.IsEnabled = !isBusy && !isRunning;
        stopItem.IsEnabled = isRunning;
        restartItem.IsEnabled = !isBusy && isRunning;
    }

    public void Dispose()
    {
        _window.Closing -= OnWindowClosing;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _desktop.ShutdownRequested -= OnShutdownRequested;
        if (_popup is not null)
        {
            _popup.Deactivated -= OnPopupDeactivated;
            _popup.Close();
            _popup = null;
        }
        _trayIcon.Dispose();
    }
}
