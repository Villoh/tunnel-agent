using System;
using System.ComponentModel;
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
    private MainWindowViewModel? _currentViewModel;

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
        if (_currentViewModel is not null)
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _currentViewModel = DataContext as MainWindowViewModel;
        if (_currentViewModel is null) return;

        _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not MainWindowViewModel vm) return;

        if (args.PropertyName == nameof(MainWindowViewModel.ThemeMode))
        {
            ApplyThemeMode(vm);
            Dispatcher.UIThread.Post(ApplyNativeBorderColor, DispatcherPriority.Background);
        }
        else if (args.PropertyName == nameof(MainWindowViewModel.ShowApiKeysDialog) && vm.ShowApiKeysDialog)
            Dispatcher.UIThread.Post(() => ApiKeysOverlay.Focus(), DispatcherPriority.Input);
        else if (args.PropertyName == nameof(MainWindowViewModel.ShowPerplexityAccountDialog) && vm.ShowPerplexityAccountDialog)
            Dispatcher.UIThread.Post(() => PerplexityAccountOverlay.Focus(), DispatcherPriority.Input);
        else if (args.PropertyName == nameof(MainWindowViewModel.ShowAgentConfigDialog) && vm.ShowAgentConfigDialog)
            Dispatcher.UIThread.Post(() => AgentConfigOverlay.Focus(), DispatcherPriority.Input);
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

    private void OnOpenUrlClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

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
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
            "Quota"         => SectionKey.Quota,
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
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.DismissEditPerplexityLabelDialogCommand.Execute(null);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.ConfirmEditPerplexityLabelAsync();
        }
    }

    private void OnEditPerplexityLabelOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.DismissEditPerplexityLabelDialogCommand.Execute(null);
    }

    private static void OnDialogCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnApiKeysOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.DismissApiKeysCommand.Execute(null);
    }

    private void OnPerplexityAccountOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.DismissPerplexityAccountDialogCommand.Execute(null);
    }

    private void OnAgentConfigOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.DismissAgentConfigCommand.Execute(null);
    }

    private void OnApiKeysDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            e.Handled = true;
            vm.DismissApiKeysCommand.Execute(null);
        }
    }

    private async void OnPerplexityAccountDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.DismissPerplexityAccountDialogCommand.Execute(null);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConfirmPerplexityAccountFromInputsAsync();
        }
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
        var all = previews.Count > 1
            ? string.Join("\n\n---\n\n", previews.Select(p => $"# {p.Filename}\n# {p.TargetPath}\n\n{p.Content}"))
            : previews[0].Content;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(all);
    }
}
