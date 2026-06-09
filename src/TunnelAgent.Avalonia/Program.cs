using Avalonia;
using Avalonia.Controls;
using System;
using TunnelAgent.Services;
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

        TunnelAgent.Infrastructure.Services.UserEnvironmentService.Initialize();

        using var singleInstance = new SingleInstanceService();
        if (!singleInstance.TryClaimInstance())
            return;

        singleInstance.StartListening();
        BuildAvaloniaApp(singleInstance).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp(SingleInstanceService? singleInstance = null)
    {
        App.SingleInstance = singleInstance;
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.Svg).Assembly);
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.SvgImageExtension).Assembly);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
