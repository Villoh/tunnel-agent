using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ProvidersView : UserControl
{
    public ProvidersView() => InitializeComponent();

    private async void OnCopyEndpoint(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(vm.EndpointUrl);
        vm.EndpointCopied = true;
        await Task.Delay(2000);
        vm.EndpointCopied = false;
    }

    private async void OnOAuthConnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderViewModel provider }) return;

        await vm.ConnectOAuthAsync(provider.Id);
    }

    private void OnOAuthDisconnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderViewModel provider }) return;
        vm.DisconnectOAuth(provider.Id);
        vm.ShowOAuthStatus = false;
    }

    private void OnToggleAccount(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderAccountViewModel account })
            account.IsDisabled = !account.IsDisabled;
    }

}
