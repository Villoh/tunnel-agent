using System;
using System.Runtime.InteropServices;
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
        if (DataContext is MainWindowViewModel vm && Application.Current is { } app)
            vm.IsDark = app.ActualThemeVariant == ThemeVariant.Dark;

        ApplyNativeBorderColor();
        Dispatcher.UIThread.Post(ApplyNativeBorderColor, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsDark) && Application.Current is { } app)
                {
                    app.RequestedThemeVariant = vm.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
                    Dispatcher.UIThread.Post(ApplyNativeBorderColor, DispatcherPriority.Background);
                }
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
            "Activity"      => SectionKey.Activity,
            "Configuration" => SectionKey.Configuration,
            _ => vm.SelectedSection
        };
    }
}
