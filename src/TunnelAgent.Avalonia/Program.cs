using Avalonia;
using System;
using Velopack;

namespace TunnelAgent;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must be the very first call — handles installer hooks (install/uninstall/update)
        // and exits early when launched by the Velopack installer, not by the user.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.Svg).Assembly);
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.SvgImageExtension).Assembly);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
