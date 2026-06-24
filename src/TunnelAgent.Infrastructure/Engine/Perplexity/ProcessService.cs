using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>Starts and monitors Perplexity WebUI Scraper local API process.</summary>
public sealed class ProcessService
{
    private const int MaxStderrBufferChars = 64 * 1024;
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    public bool IsRunning => State == EngineState.Running;
    public int Port { get; private set; }
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string? LastError { get; private set; }
    public EngineErrorKind LastErrorKind { get; private set; } = EngineErrorKind.None;

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

        // Pre-flight: fail fast with a clear message if the port is already taken.
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
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
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

        var stderrLines = new System.Text.StringBuilder();
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AppendCapped(stderrLines, e.Data);
        };

        _process.Exited += (_, _) =>
        {
            if (State is EngineState.Running or EngineState.Starting)
            {
                var stderr = stderrLines.ToString().Trim();
                LastError = string.IsNullOrEmpty(stderr)
                    ? "Process exited unexpectedly."
                    : stderr.Split('\n')[^1].Trim(); // last stderr line
                LastErrorKind = EngineErrorKind.Crashed;
                SetState(EngineState.Error);
            }
        };

        _process.Start();
        _process.BeginErrorReadLine();

        var healthy = await WaitForHealthAsync(port, ct);
        if (!healthy)
        {
            // Check if process already exited (crash)
            if (_process?.HasExited == true)
            {
                // Error already set by Exited handler
                return;
            }
            LastError = "Engine did not respond in time.";
            LastErrorKind = EngineErrorKind.Timeout;
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

    private static void AppendCapped(System.Text.StringBuilder buffer, string line)
    {
        buffer.AppendLine(line);
        if (buffer.Length > MaxStderrBufferChars)
            buffer.Remove(0, buffer.Length - MaxStderrBufferChars);
    }

    private static async Task<bool> WaitForHealthAsync(int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        // Python/uvicorn startup can take several seconds on first run
        for (var i = 0; i < 60; i++)
        {
            try
            {
                await Task.Delay(200, ct);
                var response = await HealthClient.GetAsync(url, ct);
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* not up yet or http timeout — retry */ }
        }

        return false;
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
