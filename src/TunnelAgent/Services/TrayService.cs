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

namespace TunnelAgent.Services;

public sealed class TrayService : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _showHideItem;
    private readonly NativeMenuItem _startItem;
    private readonly NativeMenuItem _stopItem;
    private readonly NativeMenuItem _restartItem;
    private readonly NativeMenuItem _statusItem;
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
        _statusItem = CreateItem("Server: Stopped", null);
        _startItem = CreateItem("Start Server", async (_, _) => await _viewModel.StartServerAsync());
        _stopItem = CreateItem("Stop Server", async (_, _) => await _viewModel.StopServerAsync());
        _restartItem = CreateItem("Restart Server", async (_, _) => await _viewModel.RestartEngineAsync());

        var cliProxyMenu = new NativeMenu
        {
            Items =
            {
                _statusItem,
                new NativeMenuItemSeparator(),
                _startItem,
                _stopItem,
                _restartItem
            }
        };

        var menu = new NativeMenu
        {
            Items =
            {
                _showHideItem,
                new NativeMenuItemSeparator(),
                new NativeMenuItem { Header = "CLIProxyAPI", Menu = cliProxyMenu },
                new NativeMenuItemSeparator(),
                CreateItem("Configuration…", (_, _) => ShowConfiguration()),
                CreateItem("Open Auth Folder", (_, _) => _viewModel.OpenAuthFolder()),
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
        await _viewModel.StopServerAsync();
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
        _viewModel.StopServerAsync().GetAwaiter().GetResult();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Window.IsVisible) || e.Property.Name == nameof(Window.WindowState))
            RefreshMenu();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EngineState) or nameof(MainWindowViewModel.ServerState))
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
        _viewModel.SelectedSection = SectionKey.Configuration;
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
        _statusItem.Header = $"Server: {_viewModel.ServerState}";
        _statusItem.IsEnabled = false;

        var state = _viewModel.EngineState;
        var isBusy = state is EngineState.Starting or EngineState.Downloading or EngineState.Installing;
        var isRunning = state == EngineState.Running;
        _startItem.IsEnabled = !isBusy && !isRunning;
        _stopItem.IsEnabled = isRunning;
        _restartItem.IsEnabled = !isBusy && isRunning;
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
