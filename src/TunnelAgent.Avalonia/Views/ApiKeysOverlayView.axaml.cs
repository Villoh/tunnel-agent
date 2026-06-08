using Avalonia.Controls;
using Avalonia.Input;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class ApiKeysOverlayView : UserControl
{
    public ApiKeysOverlayView()
    {
        InitializeComponent();
    }

    public void FocusOverlay() => ApiKeysOverlay.Focus();

    private static void OnDialogCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnApiKeysOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DismissApiKeysCommand.Execute(null);
    }

    private void OnApiKeysDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            e.Handled = true;
            vm.DismissApiKeysCommand.Execute(null);
        }
    }
}
