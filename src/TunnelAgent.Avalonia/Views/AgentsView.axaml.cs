using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class AgentsView : UserControl
{
    public AgentsView() => InitializeComponent();

    private void OnDetectAgents(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DetectAgentsCommand.Execute(null);
    }

    private void OnDocsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnConfigureClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentViewModel vm } &&
            DataContext is MainWindowViewModel mvm)
        {
            mvm.OpenAgentConfigCommand.Execute(vm);
        }
    }

}
