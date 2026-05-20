using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>Starts and monitors Perplexity WebUI Scraper local API process.</summary>
public sealed class ProcessService
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    public bool IsRunning => State == EngineState.Running;
    public int Port { get; private set; }
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    private Process? _process;

    public async Task StartAsync(string binaryPath, int port, CancellationToken ct = default)
    {
        Port = port;
        SetState(EngineState.Starting);

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo(binaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                ArgumentList =
                {
                    "api",
                    "--host", "127.0.0.1",
                    "--port", port.ToString(),
                    "--log-level", "warning"
                }
            },
            EnableRaisingEvents = true
        };

        _process.Exited += (_, _) =>
        {
            if (State == EngineState.Running)
            {
                LastError = "Process exited unexpectedly.";
                SetState(EngineState.Error);
            }
        };

        _process.Start();

        var healthy = await WaitForHealthAsync(port, ct);
        if (!healthy)
        {
            LastError = "Engine did not respond in time.";
            SetState(EngineState.Error);
            return;
        }

        LastError = null;
        SetState(EngineState.Running);
    }

    public Task StopAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
                _process = null;
            }
        }
        catch { }

        SetState(EngineState.Stopped);
        return Task.CompletedTask;
    }

    private static async Task<bool> WaitForHealthAsync(int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        for (var i = 0; i < 25; i++)
        {
            try
            {
                await Task.Delay(200, ct);
                var response = await HealthClient.GetAsync(url, ct);
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return false;
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
