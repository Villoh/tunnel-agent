using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Monitors the CLIProxyAPI auth directory (~/.cli-proxy-api/) for file changes
/// and fires <see cref="Changed"/> after a short debounce.
/// Used to detect when OAuth tokens are written/removed by the proxy binary,
/// and when custom-provider credentials are added/deleted from the UI.
/// </summary>
public sealed class AuthFileWatcher : IDisposable
{
    public event EventHandler? Changed;

    private readonly FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(400);

    public AuthFileWatcher(string directory)
    {
        Directory.CreateDirectory(directory);

        try
        {
            _watcher = new FileSystemWatcher(directory, "*.json")
            {
                NotifyFilter            = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories   = false,
                EnableRaisingEvents     = true,
            };

            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch
        {
            // Directory may not exist yet on first run; watcher will be null.
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleNotify();

    private void ScheduleNotify()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, token);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Manually triggers a change notification (e.g. after writing a credential file).</summary>
    public void NotifyNow() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _watcher?.Dispose();
    }
}
