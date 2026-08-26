using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ConfigurationView : UserControl
{
    private MainWindowViewModel? _vm;
    private int _lastScrollRequestId = -1;
    private DispatcherTimer? _scrollTimer;

    public ConfigurationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        Loaded += (_, _) => HookViewModel();
        HookViewModel();
    }

    private void HookViewModel()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.LocalProxyScrollRequestId)) return;
        TryScrollToLocalProxySection();
    }

    private void TryScrollToLocalProxySection()
    {
        if (_vm is null || _vm.LocalProxyScrollRequestId == _lastScrollRequestId) return;
        _lastScrollRequestId = _vm.LocalProxyScrollRequestId;

        _scrollTimer?.Stop();
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var deadline = DateTime.UtcNow.AddSeconds(2);
        _scrollTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > deadline)
            {
                _scrollTimer?.Stop();
                return;
            }

            var label = this.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => control.Name == "LocalProxySectionLabel");
            var card = this.GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => control.Name == "LocalProxySectionCard");
            if (label?.GetVisualRoot() is null || label.Bounds.Height <= 0 || card is null)
                return;

            card.BringIntoView();
            _scrollTimer?.Stop();
        };
        _scrollTimer.Start();
    }

    private async void OnCopyCliProxyEndpoint(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(vm.CliProxyEndpointUrl);
        vm.EndpointCopied = true;
        await Task.Delay(2000);
        vm.EndpointCopied = false;
    }

    private async void OnCopyManagementKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(vm.ManagementKey);
        vm.ManagementKeyCopied = true;
        await Task.Delay(2000);
        vm.ManagementKeyCopied = false;
    }
}
