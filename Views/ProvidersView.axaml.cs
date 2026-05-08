using Avalonia.Controls;
using Avalonia.Interactivity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ProvidersView : UserControl
{
    public ProvidersView() => InitializeComponent();

    private async void OnCopyEndpoint(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(vm.EndpointUrl);
    }
}
