using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Core.Engine;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>Starts and monitors the 9Router standalone Next.js server via Node.js.</summary>
public sealed class ProcessService
{
    private const int MaxStderrBufferChars = 64 * 1024;
    private const int HealthPollAttempts = 150;
    private const int HealthPollIntervalMs = 200;
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    /// <summary>Gets whether the engine process is currently running.</summary>
    public bool IsRunning => State == EngineState.Running;

    /// <summary>Gets the loopback port requested for the last start attempt.</summary>
    public int Port { get; private set; }

    /// <summary>Gets the current process lifecycle state.</summary>
    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Gets the last process error message, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets the structured reason for <see cref="LastError"/>.</summary>
    public EngineErrorKind LastErrorKind { get; private set; } = EngineErrorKind.None;

    /// <summary>Raised after <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    private Process? _process;

    /// <summary>
    /// Starts Node.js against the extracted standalone server on loopback.
    /// Does not throw when the port is taken or the executable cannot be launched;
    /// those cases set <see cref="State"/> to <see cref="EngineState.Error"/>.
    /// </summary>
    /// <param name="nodeExecutable">Path to the Node.js executable.</param>
    /// <param name="serverEntryPath">Path to <c>app/custom-server.js</c> or <c>app/server.js</c>.</param>
    /// <param name="port">Loopback TCP port advertised via the <c>PORT</c> environment variable.</param>
    /// <param name="ct">Token used to cancel the health-check wait.</param>
    public async Task StartAsync(string nodeExecutable, string serverEntryPath, int port, CancellationToken ct = default)
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
            StartInfo = CreateStartInfo(nodeExecutable, serverEntryPath, port),
            EnableRaisingEvents = true
        };

        var stderrLines = new StringBuilder();
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AppendCapped(stderrLines, e.Data);
        };

        _process.Exited += (_, _) =>
        {
            if (State == EngineState.Running)
            {
                var stderr = stderrLines.ToString().Trim();
                LastError = string.IsNullOrEmpty(stderr)
                    ? "Process exited unexpectedly."
                    : stderr.Split('\n')[^1].Trim();
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

        _process.BeginErrorReadLine();

        var result = await WaitForHealthAsync(port, ct);
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

    /// <summary>
    /// Stops the Node.js process tree if it is running and sets state to <see cref="EngineState.Stopped"/>.
    /// Safe to call when nothing is running.
    /// </summary>
    public Task StopAsync()
    {
        StopProcess();
        SetState(EngineState.Stopped);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the Node.js start info for the standalone server without starting a process.
    /// Working directory is the directory that contains <paramref name="serverEntryPath"/>.
    /// Current process environment is inherited, with <c>PORT</c> and <c>HOSTNAME=127.0.0.1</c> applied.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string nodeExecutable, string serverEntryPath, int port)
    {
        var workingDirectory = Path.GetDirectoryName(serverEntryPath);
        if (string.IsNullOrEmpty(workingDirectory))
            throw new ArgumentException("Server entry path must include a directory.", nameof(serverEntryPath));

        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("--dns-result-order=ipv4first");
        startInfo.ArgumentList.Add("--max-old-space-size=6144");
        startInfo.ArgumentList.Add(serverEntryPath);
        startInfo.Environment["PORT"] = port.ToString();
        startInfo.Environment["HOSTNAME"] = "127.0.0.1";
        return startInfo;
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

    private static void AppendCapped(StringBuilder buffer, string line)
    {
        buffer.AppendLine(line);
        if (buffer.Length > MaxStderrBufferChars)
            buffer.Remove(0, buffer.Length - MaxStderrBufferChars);
    }

    private enum HealthResult { Healthy, Timeout, ProcessExited }

    private async Task<HealthResult> WaitForHealthAsync(int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}/v1/models";
        for (var i = 0; i < HealthPollAttempts; i++)
        {
            if (_process is { HasExited: true })
                return HealthResult.ProcessExited;

            try
            {
                await Task.Delay(HealthPollIntervalMs, ct);
                var response = await HealthClient.GetAsync(url, ct);
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
