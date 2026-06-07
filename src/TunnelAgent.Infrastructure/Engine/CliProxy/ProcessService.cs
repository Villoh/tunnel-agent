using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// Responsible for starting, stopping, and monitoring the CLIProxyAPI process.
/// Does not know how to download the binary or generate config.
/// </summary>
public sealed class ProcessService
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    public bool IsRunning => State == EngineState.Running;
    public int Port { get; private set; }
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    private Process? _process;

    public async Task StartAsync(string binaryPath, string configPath, int port, string? apiKey = null, CancellationToken ct = default)
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
                ArgumentList = { "-config", configPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
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

        // Poll the health endpoint until the server is up (up to 5s)
        var healthy = await WaitForHealthAsync(port, apiKey, ct);
        if (!healthy)
        {
            LastError = "Engine did not respond in time.";
            StopProcess();
            SetState(EngineState.Error);
            return;
        }

        LastError = null;
        SetState(EngineState.Running);
    }

    public Task StopAsync()
    {
        StopProcess();
        SetState(EngineState.Stopped);
        return Task.CompletedTask;
    }

    private void StopProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private static async Task<bool> WaitForHealthAsync(int port, string? apiKey, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        for (var i = 0; i < 25; i++)
        {
            try
            {
                await Task.Delay(200, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                var response = await HealthClient.SendAsync(request, ct);
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* not up yet or http timeout — retry */ }
        }
        return false;
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
