using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Enforces a single running instance of the app.
/// Call <see cref="TryClaimInstance"/> at startup:
///   - Returns true  → this process is the primary instance; call <see cref="StartListening"/> to receive activation signals.
///   - Returns false → another instance is already running; a signal has been sent to bring it to the front.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
#if DEBUG
    private const string MutexName = "TunnelAgent-SingleInstance-Debug";
    private const string PipeName  = "TunnelAgent-Activate-Debug";
#else
    private const string MutexName = "TunnelAgent-SingleInstance";
    private const string PipeName  = "TunnelAgent-Activate";
#endif

    private Mutex? _mutex;
    private bool _mutexOwned;
    private CancellationTokenSource? _cts;

    public event Action? ActivationRequested;

    /// <summary>
    /// Tries to claim the single-instance slot.
    /// </summary>
    public bool TryClaimInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew)
        {
            _mutexOwned = true;
            return true;
        }

        // Another instance owns the mutex — signal it and surrender.
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1000);
            using var writer = new StreamWriter(client);
            writer.WriteLine("activate");
        }
        catch { /* primary instance may not be listening yet; ignore */ }

        return false;
    }

    /// <summary>
    /// Starts the pipe server that listens for activation signals from secondary instances.
    /// Must be called only when <see cref="TryClaimInstance"/> returned true.
    /// </summary>
    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var msg = await reader.ReadLineAsync(ct);
                if (msg == "activate")
                    ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { /* pipe broken — loop and create a new server */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_mutexOwned)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
