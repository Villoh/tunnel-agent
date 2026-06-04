using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
        => _ = ExportAsync();

    public async Task ExportAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var isRequests = vm.Logs.IsRequestsTab;
        var timestamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        var options = new FilePickerSaveOptions
        {
            Title           = isRequests ? "Export Requests" : "Export Proxy Logs",
            SuggestedFileName = isRequests
                ? $"requests_{timestamp}.csv"
                : $"proxy-logs_{timestamp}.log",
            FileTypeChoices = isRequests
                ? [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
                : [new FilePickerFileType("Log") { Patterns = ["*.log"] }],
        };

        var topLevel = TopLevel.GetTopLevel(this);
        var file = await topLevel!.StorageProvider.SaveFilePickerAsync(options);
        if (file is null) return;

        var content = isRequests ? vm.Logs.BuildRequestsCsv() : vm.Logs.BuildProxyLog();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }
}
