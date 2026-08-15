using Avalonia.Controls;
using Avalonia.Input;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class NineRouterComboOverlayView : UserControl
{
    public NineRouterComboOverlayView() => InitializeComponent();

    private static void OnDialogCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.NineRouterCombos.CloseCreatePanelCommand.Execute(null);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel vm) return;
        e.Handled = true;
        vm.NineRouterCombos.CloseCreatePanelCommand.Execute(null);
    }
}
