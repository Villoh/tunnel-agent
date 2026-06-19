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

        vm.ShowOAuthStatus = false;
        var (success, message) = await vm.ConnectOAuthAsync(provider.Id);

        if (success && message.Contains("Device code"))
        {
            var codeStart = message.IndexOf('\n') + 1;
            var codeEnd = message.IndexOf('\n', codeStart);
            if (codeEnd > codeStart)
            {
                var code = message[codeStart..codeEnd].Trim();
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null && !string.IsNullOrEmpty(code))
                    await clipboard.SetTextAsync(code);
            }
        }

        vm.OAuthStatusIsError = !success;
        vm.OAuthStatusMessage = message;
        vm.ShowOAuthStatus = true;

        if (success)
        {
            await Task.Delay(8000);
            if (vm.OAuthStatusMessage == message)
                vm.ShowOAuthStatus = false;
        }
    }

    private void OnOAuthDisconnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderViewModel provider }) return;
        vm.DisconnectOAuth(provider.Id);
        vm.ShowOAuthStatus = false;
    }

    private void OnDismissOAuthStatus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowOAuthStatus = false;
    }

    private void OnToggleAccount(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderAccountViewModel account })
            account.IsDisabled = !account.IsDisabled;
    }

}
