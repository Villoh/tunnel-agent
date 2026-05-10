// Services/IProxyServer.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

public interface IProxyServer
{
    bool IsRunning { get; }
    int Port { get; }
    event EventHandler? StateChanged;

    Task StartAsync(int port, string bindAddress, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
