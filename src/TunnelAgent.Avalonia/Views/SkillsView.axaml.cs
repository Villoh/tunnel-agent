using Avalonia.Controls;
using Avalonia.Input;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class SkillsView : UserControl
{
    public SkillsView() => InitializeComponent();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var vm = DataContext switch
        {
            MainWindowViewModel main => main.Skills,
            SkillsViewModel skills => skills,
            _ => null
        };
        vm?.SearchCommand.Execute(null);
    }
}
