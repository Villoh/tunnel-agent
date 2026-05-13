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
        if (clipboard is not null)
            await clipboard.SetTextAsync(vm.EndpointUrl);
    }

    private async void OnOAuthConnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderViewModel provider }) return;

        vm.ShowOAuthStatus = false;

        var (success, message) = await vm.ConnectOAuthAsync(provider.Id);

        // For Copilot: copy device code to clipboard
        if (success && message.Contains("Device code"))
        {
            var codeStart = message.IndexOf('\n') + 1;
            var codeEnd   = message.IndexOf('\n', codeStart);
            if (codeEnd > codeStart)
            {
                var code = message[codeStart..codeEnd].Trim();
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null && !string.IsNullOrEmpty(code))
                    await clipboard.SetTextAsync(code);
            }
        }

        vm.OAuthStatusIsError   = !success;
        vm.OAuthStatusMessage   = message;
        vm.ShowOAuthStatus      = true;

        if (success)
        {
            // Auto-dismiss after 8s
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

    private async void OnRefreshQuota(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderAccountViewModel account }) return;
        account.IsRefreshing = true;
        try   { await vm.RefreshQuotaAsync(account); }
        finally { account.IsRefreshing = false; }
    }

    private async void OnConfirmAddAccount(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.AddAccountTarget is not { } target) return;

        var baseUrl = BaseUrlBox.Text?.Trim() ?? "";
        var apiKey  = ApiKeyBox.Text?.Trim()  ?? "";
        var label   = LabelBox.Text?.Trim();

        if (string.IsNullOrEmpty(apiKey)) return;

        var effectiveBaseUrl = string.IsNullOrEmpty(baseUrl) ? target.Description : baseUrl;

        ApiKeyBox.Text  = "";
        BaseUrlBox.Text = "";
        LabelBox.Text   = "";

        await vm.ConfirmAddAccountAsync(
            target.Id,
            effectiveBaseUrl,
            apiKey,
            string.IsNullOrEmpty(label) ? null : label);
    }
}
