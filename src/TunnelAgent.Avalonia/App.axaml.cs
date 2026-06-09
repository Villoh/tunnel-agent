// App.axaml.cs
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using TunnelAgent.Views;
using TunnelAgent.Infrastructure.Engine;
using TunnelAgent.Infrastructure.Engine.CliProxy;

namespace TunnelAgent;

public partial class App : Application
{
    internal static SingleInstanceService? SingleInstance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsService();
            settings.LoadSync(); // Apply persisted theme before window is shown to avoid flash
            var engineRegistry = new EngineRegistryService(settings);
            var engineConfig = new ConfigService(settings);
            var catalog = new ProviderCatalogService(settings, engineConfig);
            var perplexityAccounts = new PerplexityAccountCatalogService();
            var launchAtLogin = new LaunchAtLoginService();
            var folderOpen = new FolderOpenService();
            var vm = new MainWindowViewModel(settings, engineRegistry, catalog, perplexityAccounts, launchAtLogin, folderOpen);

            var mainWindow = new MainWindow { DataContext = vm };
            var startInTray = Array.Exists(desktop.Args ?? [], arg => string.Equals(arg, "--start-in-tray", StringComparison.OrdinalIgnoreCase));
            if (!startInTray)
                desktop.MainWindow = mainWindow;
            var tray = new TrayService(desktop, mainWindow, vm);
            desktop.Exit += async (_, _) =>
            {
                try
                {
                    tray.Dispose();
                    await vm.DisposeAsync();
                }
                catch { }
            };

            if (SingleInstance != null)
                SingleInstance.ActivationRequested += () =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (desktop.MainWindow != mainWindow)
                            desktop.MainWindow = mainWindow;
                        if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                            mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                        mainWindow.Show();
                        mainWindow.Activate();
                    });



            Dispatcher.UIThread.Post(async () =>
            {
                try { await vm.InitializeAsync(); }
                catch { }
            }, DispatcherPriority.Background);

            vm.InitAppUpdater();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
