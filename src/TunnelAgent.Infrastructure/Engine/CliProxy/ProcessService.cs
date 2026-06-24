using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    public EngineErrorKind LastErrorKind { get; private set; } = EngineErrorKind.None;

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

        // Pre-flight: if the port is already taken (e.g. CLIProxyAPI is running in a
        // terminal or another app), bail out deterministically. Without this, the
        // foreign instance can answer our health check before our own process finishes
        // exiting, which would surface a misleading "exited unexpectedly" error.
        if (IsPortInUse(port))
        {
            LastError = $"Port {port} is already in use by another process.";
            LastErrorKind = EngineErrorKind.PortInUse;
            SetState(EngineState.Error);
            return;
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
                LastErrorKind = EngineErrorKind.Crashed;
                SetState(EngineState.Error);
            }
        };

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            LastError = $"Failed to launch engine: {ex.Message}";
            LastErrorKind = EngineErrorKind.LaunchFailed;
            StopProcess();
            SetState(EngineState.Error);
            return;
        }

        // Poll the health endpoint until the server is up (up to 5s).
        // WaitForHealthAsync also detects our own process exiting early, which is
        // what happens when the port is already in use by another instance.
        var result = await WaitForHealthAsync(port, apiKey, ct);
        if (result != HealthResult.Healthy)
        {
            if (result == HealthResult.ProcessExited)
            {
                LastError = $"Engine exited on startup — port {port} may already be in use by another process.";
                LastErrorKind = EngineErrorKind.PortInUse;
            }
            else
            {
                LastError = "Engine did not respond in time.";
                LastErrorKind = EngineErrorKind.Timeout;
            }
            StopProcess();
            SetState(EngineState.Error);
            return;
        }

        LastError = null;
        LastErrorKind = EngineErrorKind.None;
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

    private enum HealthResult { Healthy, Timeout, ProcessExited }

    private async Task<HealthResult> WaitForHealthAsync(int port, string? apiKey, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        for (var i = 0; i < 25; i++)
        {
            // If our own process already exited, a healthy endpoint would only mean
            // some other instance owns the port. Report that distinctly so the engine
            // surfaces a clear, retryable error instead of a false "Running".
            if (_process is { HasExited: true })
                return HealthResult.ProcessExited;

            try
            {
                await Task.Delay(200, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                var response = await HealthClient.SendAsync(request, ct);
                if (_process is { HasExited: true })
                    return HealthResult.ProcessExited;
                if ((int)response.StatusCode < 500)
                    return HealthResult.Healthy;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* not up yet or http timeout — retry */ }
        }
        return HealthResult.Timeout;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
