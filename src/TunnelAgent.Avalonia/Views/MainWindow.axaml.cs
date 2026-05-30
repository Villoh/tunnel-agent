using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Styling;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Views;

public partial class MainWindow : Window
{
    private const int DwmwaBorderColor = 34;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Activated += (_, _) => ApplyNativeBorderColor();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            ApplyThemeMode(vm);

        ApplyNativeBorderColor();
        Dispatcher.UIThread.Post(ApplyNativeBorderColor, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.ThemeMode))
                {
                    ApplyThemeMode(vm);
                    Dispatcher.UIThread.Post(ApplyNativeBorderColor, DispatcherPriority.Background);
                }
            };
    }


    private static void ApplyThemeMode(MainWindowViewModel vm)
    {
        if (Application.Current is not { } app) return;

        app.RequestedThemeVariant = vm.ThemeMode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        vm.IsDark = vm.ThemeMode switch
        {
            "light" => false,
            "dark" => true,
            _ => app.ActualThemeVariant == ThemeVariant.Dark
        };
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ApplyNativeBorderColor()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        // Hide the thin DWM border that Windows draws even with ExtendClientArea by matching
        // it to the window background color.
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark ||
                   Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
        var color = dark ? ToColorRef(0x1F, 0x21, 0x28) : ToColorRef(0xFC, 0xFC, 0xFD);
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref color, sizeof(int));
    }

    private static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);


    private void OnResizeHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (sender is Control { Tag: string edgeName } && Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }

    private void OnSidebarItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not ListBoxItem item) return;
        vm.SelectedSection = item.Tag switch
        {
            "Providers"     => SectionKey.Providers,
            "Agents"        => SectionKey.Agents,
            "Configuration" => SectionKey.Configuration,
            _ => vm.SelectedSection
        };
    }

    private async void OnConfirmAddAccount(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.AddAccountTarget is not { } target) return;

        var baseUrl = BaseUrlBox.Text?.Trim() ?? "";
        var apiKey = ApiKeyBox.Text?.Trim() ?? "";
        var label = LabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(apiKey)) return;

        var effectiveBaseUrl = string.IsNullOrEmpty(baseUrl) ? target.Description : baseUrl;
        ApiKeyBox.Text = "";
        BaseUrlBox.Text = "";
        LabelBox.Text = "";

        await vm.ConfirmAddAccountAsync(target.Id, effectiveBaseUrl, apiKey, string.IsNullOrEmpty(label) ? null : label);
    }

    private async void OnConfirmPerplexityAccount(object? sender, RoutedEventArgs e) =>
        await ConfirmPerplexityAccountFromInputsAsync();

    private async Task ConfirmPerplexityAccountFromInputsAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var sessionToken = PerplexitySessionTokenBox.Text?.Trim() ?? "";
        var label = PerplexityLabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(sessionToken)) return;

        PerplexitySessionTokenBox.Text = "";
        PerplexityLabelBox.Text = "";
        await vm.ConfirmAddPerplexityAccountAsync(string.IsNullOrEmpty(label) ? null : label, sessionToken);
    }

    private async void OnPerplexityAccountInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConfirmPerplexityAccountFromInputsAsync();
    }

    private async void OnStartPerplexityTokenFlow(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        PerplexityTokenFlowInputBox.Text = "";
        await vm.StartPerplexityTokenFlowAsync();
        PerplexityTokenFlowInputBox.Focus();
    }

    private async void OnSubmitPerplexityTokenFlow(object? sender, RoutedEventArgs e) =>
        await SubmitPerplexityTokenFlowFromInputAsync();

    private async void OnPerplexityTokenFlowInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitPerplexityTokenFlowFromInputAsync();
    }

    private async Task SubmitPerplexityTokenFlowFromInputAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var input = PerplexityTokenFlowInputBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return;

        await vm.SubmitPerplexityTokenFlowAsync(input);

        if (!string.IsNullOrWhiteSpace(vm.PerplexityGeneratedToken))
        {
            PerplexitySessionTokenBox.Text = vm.PerplexityGeneratedToken;
            await vm.CancelPerplexityTokenFlowAsync();
            return;
        }

        if (!vm.PerplexityTokenHasError)
            PerplexityTokenFlowInputBox.Text = "";

        PerplexityTokenFlowInputBox.Focus();
    }

    private async void OnEditPerplexityLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel vm) return;
        e.Handled = true;
        await vm.ConfirmEditPerplexityLabelAsync();
    }

    private async void OnCopyAgentConfigClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var all = string.Join("\n\n---\n\n",
            vm.AgentConfigPreviews.Select(p => $"# {p.Filename}\n# {p.TargetPath}\n\n{p.Content}"));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(all);
    }
}
