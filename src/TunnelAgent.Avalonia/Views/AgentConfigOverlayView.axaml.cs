using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class AgentConfigOverlayView : UserControl
{
    public AgentConfigOverlayView()
    {
        InitializeComponent();
    }

    public void FocusOverlay() => AgentConfigOverlay.Focus();

    private static void OnDialogCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnOpenUrlClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnAgentConfigOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DismissAgentConfigCommand.Execute(null);
    }

    private async void OnAgentConfigDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.DismissAgentConfigCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && !vm.AgentConfigHasResult && vm.CanApplyAgentConfig())
        {
            e.Handled = true;
            await vm.ApplyAgentConfigCommand.ExecuteAsync(null);
        }
    }

    private async void OnCopyAgentConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var previews = vm.AgentConfigPreviews.ToList();
        if (previews.Count == 0) return;

        var all = previews.Count > 1
            ? string.Join("\n\n---\n\n", previews.Select(p => $"# {p.Filename}\n# {p.TargetPath}\n\n{p.Content}"))
            : previews[0].Content;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(all);
    }
}
