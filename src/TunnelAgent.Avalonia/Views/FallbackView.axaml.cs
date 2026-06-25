using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TunnelAgent.Views;

public partial class FallbackView : UserControl
{
    public FallbackView() => InitializeComponent();

    private void OnOpenUrlClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { /* ignore */ }
        }
    }
}
