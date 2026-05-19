using Avalonia.Controls;
using Avalonia.Interactivity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ConfigurationView : UserControl
{
    public ConfigurationView() => InitializeComponent();

    private void OnDismissNoUpdateToast(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowNoUpdateToast = false;
    }
}
