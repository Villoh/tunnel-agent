using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ConfigurationView : UserControl
{
    private MainWindowViewModel? _vm;

    public ConfigurationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
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
        Dispatcher.UIThread.Post(async () => await ScrollToLocalProxySectionAsync(), DispatcherPriority.Background);
    }

    private async Task ScrollToLocalProxySectionAsync()
    {
        // Let SelectedSection + IsVisible layout settle before measuring.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(50);

        var point = LocalProxySectionLabel.TranslatePoint(new Point(0, 0), ConfigurationContent);
        if (point is null) return;

        var y = point.Value.Y - 24;
        ConfigurationScrollViewer.Offset = new Vector(0, y < 0 ? 0 : y);
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
}
