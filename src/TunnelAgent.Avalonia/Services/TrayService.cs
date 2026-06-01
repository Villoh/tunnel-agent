using System;
using System.ComponentModel;
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
    private readonly NativeMenuItem _showHideItem;
    private readonly NativeMenuItem _cliProxyStartItem;
    private readonly NativeMenuItem _cliProxyStopItem;
    private readonly NativeMenuItem _cliProxyRestartItem;
    private readonly NativeMenuItem _cliProxyStatusItem;
    private readonly NativeMenuItem _perplexityStartItem;
    private readonly NativeMenuItem _perplexityStopItem;
    private readonly NativeMenuItem _perplexityRestartItem;
    private readonly NativeMenuItem _perplexityStatusItem;
    private bool _isQuitting;

    public TrayService(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        _desktop = desktop;
        _window = window;
        _viewModel = viewModel;

        _showHideItem = CreateItem("Hide Window", (_, _) => ToggleWindow());

        _cliProxyStatusItem = CreateItem("Server: Stopped", null);
        _cliProxyStartItem = CreateItem("Start Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StartServerAsync()));
        _cliProxyStopItem = CreateItem("Stop Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync()));
        _cliProxyRestartItem = CreateItem("Restart Server", async (_, _) => await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.RestartEngineAsync()));

        _perplexityStatusItem = CreateItem("Server: Stopped", null);
        _perplexityStartItem = CreateItem("Start Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StartServerAsync()));
        _perplexityStopItem = CreateItem("Stop Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync()));
        _perplexityRestartItem = CreateItem("Restart Server", async (_, _) => await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.RestartEngineAsync()));

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

        var menu = new NativeMenu
        {
            Items =
            {
                _showHideItem,
                new NativeMenuItemSeparator(),
                new NativeMenuItem { Header = "CLIProxyAPI", Menu = cliProxyMenu },
                new NativeMenuItem { Header = "Perplexity", Menu = perplexityMenu },
                new NativeMenuItemSeparator(),
                CreateItem("Configuration…", (_, _) => ShowConfiguration()),
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
        _trayIcon.Clicked += (_, _) => ShowWindow();

        _window.Closing += OnWindowClosing;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _desktop.ShutdownRequested += OnShutdownRequested;

        RefreshMenu();
    }

    public bool IsQuitting => _isQuitting;

    public async Task QuitAsync()
    {
        if (_isQuitting) return;

        _isQuitting = true;
        await RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync());
        await RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync());
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
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_isQuitting) return;

        _isQuitting = true;
        RunForEngineAsync(EngineCatalog.CliProxyApi.Id, () => _viewModel.StopServerAsync()).GetAwaiter().GetResult();
        RunForEngineAsync(EngineCatalog.PerplexityWebUiScraper.Id, () => _viewModel.StopServerAsync()).GetAwaiter().GetResult();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Window.IsVisible) || e.Property.Name == nameof(Window.WindowState))
            RefreshMenu();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EngineState) or nameof(MainWindowViewModel.ServerState)
            or nameof(MainWindowViewModel.CliProxyServerState) or nameof(MainWindowViewModel.PerplexityServerState))
            RefreshMenu();
    }

    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
            _window.Hide();
        else
            ShowWindow();

        RefreshMenu();
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        RefreshMenu();
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
        _trayIcon.Dispose();
    }
}
