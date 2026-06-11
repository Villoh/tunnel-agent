using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class TrayUsagePopup : Window
{
    /// <summary>Raised when the user asks to open the full Tunnel Agent window.</summary>
    public event EventHandler? OpenMainWindowRequested;

    private MainWindowViewModel? _vm;

    public TrayUsagePopup()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RailItems.LayoutUpdated += (_, _) => RepositionIndicator(animate: true);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        RepositionIndicator(animate: false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.TrayHomeSelected)
            or nameof(MainWindowViewModel.SelectedQuotaProvider)
            or nameof(MainWindowViewModel.QuotaProvidersForRail))
            Dispatcher.UIThread.Post(() => RepositionIndicator(animate: true), DispatcherPriority.Background);
    }

    /// <summary>Moves the single accent indicator to the currently-active rail item.</summary>
    private void RepositionIndicator(bool animate)
    {
        if (_vm is null) return;

        Control? target = _vm.TrayHomeSelected
            ? HomeButton
            : _vm.SelectedQuotaProvider is { } p
                ? ProviderItems.ContainerFromItem(p) as Control
                : null;

        if (target is null || target.Bounds.Height <= 0) return;

        var center = target.TranslatePoint(new Point(0, target.Bounds.Height / 2), RailItems);
        if (center is null) return;

        if (Indicator.RenderTransform is not TranslateTransform transform) return;

        var targetY = center.Value.Y - Indicator.Bounds.Height / 2;

        // Snap (no animation) on first placement to avoid sliding in from the top edge.
        if (!animate || !Indicator.IsVisible)
        {
            var transitions = transform.Transitions;
            transform.Transitions = null;
            transform.Y = targetY;
            transform.Transitions = transitions;
        }
        else
        {
            transform.Y = targetY;
        }

        Indicator.IsVisible = true;
    }

    private async void OnRefreshAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.RefreshAllQuotaProvidersAsync();
    }

    private void OnOpenMainWindow(object? sender, RoutedEventArgs e)
        => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenConfig(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.SelectConfigurationCommand.Execute(null);
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHomeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.TrayHomeSelected = true;
    }

    private void OnRailProviderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderViewModel provider }) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SelectQuotaProviderCommand.Execute(provider);
    }

    private void OnEngineStart(object? sender, RoutedEventArgs e)
        => _ = RunForEngineAsync(sender, vm => vm.StartServerAsync());

    private void OnEngineStop(object? sender, RoutedEventArgs e)
        => _ = RunForEngineAsync(sender, vm => vm.StopServerAsync());

    private void OnEngineRestart(object? sender, RoutedEventArgs e)
    {
        var icon = (sender as Button)?.Content as Control;
        _ = RestartWithSpinAsync(sender, icon);
    }

    private async Task RestartWithSpinAsync(object? sender, Control? icon)
    {
        icon?.Classes.Add("spin");
        try { await RunForEngineAsync(sender, vm => vm.RestartEngineAsync()); }
        finally { icon?.Classes.Remove("spin"); }
    }

    private async void OnRefreshQuota(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderAccountViewModel account }) return;
        if (account.IsRefreshing) return;

        account.IsRefreshing = true;
        try { await vm.RefreshQuotaAsync(account); }
        catch { }
        finally { Dispatcher.UIThread.Post(() => account.IsRefreshing = false); }
    }

    private async void OnCopyEndpoint(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } btn) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(url);
        btn.Classes.Add("copied");
        await Task.Delay(2000);
        btn.Classes.Remove("copied");
    }

    private async Task RunForEngineAsync(object? sender, Func<MainWindowViewModel, Task> action)
    {
        if (sender is not Button { Tag: string engineId }) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var previous = vm.FocusedConfigEngineId;
        try
        {
            vm.FocusedConfigEngineId = engineId;
            await action(vm);
        }
        finally
        {
            vm.FocusedConfigEngineId = previous;
        }
    }
}
