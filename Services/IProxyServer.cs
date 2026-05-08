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

/// <summary>
/// Stub. Replace internals with Kestrel + Yarp.ReverseProxy (IHttpForwarder)
/// to forward to provider endpoints (Anthropic, OpenAI, etc.).
/// </summary>
public sealed class StubProxyServer : IProxyServer
{
    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public event EventHandler? StateChanged;

    public async Task StartAsync(int port, string bindAddress, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        Port = port;
        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
