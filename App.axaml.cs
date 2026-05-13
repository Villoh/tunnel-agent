// App.axaml.cs
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using TunnelAgent.Views;

namespace TunnelAgent;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings      = new SettingsService();
            var engine        = new EngineService(settings);
            var engineConfig  = new EngineConfigService(settings);
            var catalog       = new ProviderCatalogService(settings, engineConfig);
            var vm            = new MainWindowViewModel(settings, engine, catalog);

            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Run async startup after the window is shown
            Dispatcher.UIThread.Post(async () =>
            {
                try { await vm.InitializeAsync(); }
                catch { /* startup errors surface via EngineState.Error in UI */ }
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
