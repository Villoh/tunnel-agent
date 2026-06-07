using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Drives both Logs tabs for both Requests and Proxy Logs.
///
/// Strategy:
///   - Engine running  → use GET /v0/management/logs (incremental via ?after=)
///   - Engine stopped  → fallback to reading main.log directly
///
/// Both tabs share the same source; the difference is how the data is presented:
///   - Requests  : lines are parsed into RequestLogEntry
///   - Proxy Logs: lines are shown raw
/// </summary>
public sealed class LogsService : IDisposable
{
    private int  _pollIntervalMs = 5000;
    private bool _autoRefresh    = false;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _logPath;
    private string _managementBaseUrl = "";
    private string _managementKey     = "";
    private bool   _managementApiAvailable;

    private CancellationTokenSource? _cts;

    // API state
    private long? _lastLogTimestamp;

    // File fallback state
    private long _lastFileSize;
    private bool _fileInitialDone;

    public event Action<IReadOnlyList<RequestLogEntry>, bool>? EntriesLoaded;
    public event Action<IReadOnlyList<string>, bool>?          RawLinesLoaded;
    public event Action?                                       Cleared;

    public LogsService(string authDirectory)
    {
        _logPath = Path.Combine(authDirectory, "logs", "main.log");
    }

    public void Configure(int port, string managementKey)
    {
        _managementBaseUrl = $"http://127.0.0.1:{port}/v0/management";
        _managementKey     = managementKey;
    }

    public void SetManagementApiAvailable(bool available) => _managementApiAvailable = available;

    public void SetAutoRefresh(bool enabled, int intervalSeconds)
    {
        _autoRefresh    = enabled;
        _pollIntervalMs = Math.Max(2, intervalSeconds) * 1000;

        // Restart loop so new interval/state takes effect immediately. If disabled,
        // stop polling without doing an extra initial poll.
        var oldCts = _cts;
        oldCts?.Cancel();
        oldCts?.Dispose();

        _cts = null;
        if (!enabled) return;

        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Start()
    {
        if (_cts is { IsCancellationRequested: false }) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Deletes the log file. Uses the management API if reachable,
    /// falls back to deleting the file directly otherwise.
    /// </summary>
    public async Task<bool> DeleteLogFileAsync()
    {
        // Try API with a short timeout so we don't block if server is down
        if (_managementApiAvailable && !string.IsNullOrEmpty(_managementBaseUrl) && !string.IsNullOrEmpty(_managementKey))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_managementBaseUrl}/logs");
                req.Headers.TryAddWithoutValidation("X-Management-Key", _managementKey);
                using var resp = await Http.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _lastLogTimestamp = null;
                    _lastFileSize     = 0;
                    _fileInitialDone  = false;
                    return true;
                }
            }
            catch { }
        }

        // Fallback: delete file directly
        try
        {
            if (File.Exists(_logPath))
                File.Delete(_logPath);
            _lastFileSize    = 0;
            _fileInitialDone = false;
            return true;
        }
        catch { return false; }
    }

    public void Stop()
    {
        var oldCts = _cts;
        oldCts?.Cancel();
        oldCts?.Dispose();
        _cts             = null;
        _lastLogTimestamp = null;
        _lastFileSize    = 0;
        _fileInitialDone = false;
    }

    public void Clear()
    {
        // Clear the in-memory view only. Preserve/advance cursors so old logs do not reappear
        // on the next poll; the trash action owns physical log deletion and full cursor reset.
        if (!_managementApiAvailable)
        {
            try { _lastFileSize = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0; }
            catch { _lastFileSize = 0; }
            _fileInitialDone = true;
        }

        Dispatcher.UIThread.Post(() => Cleared?.Invoke());
    }

    public void ResetAndClear()
    {
        _lastLogTimestamp = null;
        _lastFileSize     = 0;
        _fileInitialDone  = false;
        Dispatcher.UIThread.Post(() => Cleared?.Invoke());
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Always do one initial poll regardless of auto-refresh setting
        try { await PollOnceAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch { }

        while (!ct.IsCancellationRequested && _autoRefresh)
        {
            try { await Task.Delay(_pollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    /// <summary>Triggers a single immediate poll (for the manual Refresh button).</summary>
    public void TriggerManualRefresh()
    {
        // Manual refresh must work even when auto-refresh/log polling is stopped.
        // PollOnceAsync tries management API first, then falls back to the local file.
        _ = PollOnceAsync(CancellationToken.None);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Try API first; fall back to file if API is unavailable
        var apiSucceeded = await TryPollApiAsync(ct);
        if (!apiSucceeded)
            await PollFileAsync(ct);
    }

    // ── Primary: management API ───────────────────────────────────────────

    private async Task<bool> TryPollApiAsync(CancellationToken ct)
    {
        if (!_managementApiAvailable || string.IsNullOrEmpty(_managementBaseUrl) || string.IsNullOrEmpty(_managementKey))
            return false;

        try
        {
            var url = _lastLogTimestamp.HasValue
                ? $"{_managementBaseUrl}/logs?after={_lastLogTimestamp.Value}"
                : $"{_managementBaseUrl}/logs";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Management-Key", _managementKey);

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var lines = body?["lines"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList() ?? [];

            bool isInitial = !_lastLogTimestamp.HasValue;

            if (body?["latest-timestamp"] is JsonNode tsNode)
                _lastLogTimestamp = tsNode.GetValue<long>();

            // Reset file state — API is authoritative when available
            _lastFileSize    = 0;
            _fileInitialDone = false;

            if (lines.Count > 0)
                Emit(lines, isInitial);

            return true;
        }
        catch { return false; }
    }

    // ── Fallback: read main.log directly ─────────────────────────────────

    private async Task PollFileAsync(CancellationToken ct)
    {
        // Reset API state so we do a full reload when API comes back
        _lastLogTimestamp = null;

        if (!File.Exists(_logPath)) return;

        long currentSize;
        try { currentSize = new FileInfo(_logPath).Length; }
        catch { return; }

        if (currentSize == _lastFileSize && _fileInitialDone) return;

        if (currentSize < _lastFileSize)
        {
            _lastFileSize    = 0;
            _fileInitialDone = false;
            Dispatcher.UIThread.Post(() => Cleared?.Invoke());
        }

        List<string> lines;
        try
        {
            await using var fs = new FileStream(
                _logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_lastFileSize, SeekOrigin.Begin);
            lines = new List<string>(256);
            using var reader = new StreamReader(fs);
            while (await reader.ReadLineAsync(ct) is { } line)
                lines.Add(line);
            _lastFileSize = fs.Position;
        }
        catch { return; }

        bool isInitial = !_fileInitialDone;
        _fileInitialDone = true;

        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0) return;

        if (isInitial)
            nonEmpty.Reverse();

        Emit(nonEmpty, isInitial);
    }

    // ── Shared emit: parse entries + forward raw lines ───────────────────

    private void Emit(List<string> lines, bool isInitial)
    {
        var entries = lines
            .Select(RequestLogEntry.TryParse)
            .Where(e => e is not null)
            .Cast<RequestLogEntry>()
            .ToList();

        // No cap on entries — LogsViewModel paginates them

        if (entries.Count > 0)
            Dispatcher.UIThread.Post(() => EntriesLoaded?.Invoke(entries, isInitial));

        Dispatcher.UIThread.Post(() => RawLinesLoaded?.Invoke(lines, isInitial));
    }

    public void Dispose() => Stop();
}
