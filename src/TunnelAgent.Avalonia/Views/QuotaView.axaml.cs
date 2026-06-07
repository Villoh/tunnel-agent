using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class QuotaView : UserControl
{
    public QuotaView() => InitializeComponent();

    private async void OnRefreshAllQuota(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.RefreshAllQuotaProvidersAsync();
    }

    private async void OnRefreshQuota(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: ProviderAccountViewModel account }) return;
        if (account.IsRefreshing) return;

        account.IsRefreshing = true;
        try
        {
            await vm.RefreshQuotaAsync(account);
        }
        catch { }
        finally
        {
            Dispatcher.UIThread.Post(() => account.IsRefreshing = false);
        }
    }
}
