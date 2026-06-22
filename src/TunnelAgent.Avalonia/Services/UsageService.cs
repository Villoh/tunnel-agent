using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Continuously drains the CLIProxyAPI management <c>/usage-queue</c> into
/// structured <see cref="UsageEvent"/>s for the dashboard. Mirrors
/// quotio-desktop's always-on collector:
///   1. enable per-request telemetry via PUT <c>/usage-statistics-enabled</c>;
///   2. poll GET <c>/usage-queue?count=N</c> — a DESTRUCTIVE read (records are
///      removed on retrieval) with ~60s retention, so it must be drained on a
///      fixed cadence the whole time the proxy is reachable, regardless of which
///      UI section is visible, or events are lost.
/// </summary>
public sealed class UsageService : IDisposable
{
    private const int DrainCount = 1000;
    private const int PollIntervalMs = 4000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly UsageStore? _store;
    private string _baseUrl = "";
    private string _managementKey = "";
    private bool   _available;
    private bool   _statsEnabled;

    private CancellationTokenSource? _cts;

    /// <summary>Raised with newly drained events (never the full set — callers dedup/accumulate).</summary>
    public event Action<IReadOnlyList<UsageEvent>>? EventsLoaded;

    public UsageService(UsageStore? store = null) => _store = store;

    public void Configure(int port, string managementKey)
    {
        _baseUrl = $"http://127.0.0.1:{port}/v0/management";
        _managementKey = managementKey;
    }

    /// <summary>Drives the collector: starts continuous draining while the proxy is reachable, stops otherwise.</summary>
    public void SetManagementApiAvailable(bool available)
    {
        if (available)
        {
            _available = true;
            StartLoop();
        }
        else
        {
            _available = false;
            _statsEnabled = false;
            StopLoop();
        }
    }

    /// <summary>Kept for parity with the logs lifecycle; draining is driven by availability.</summary>
    public void Start()
    {
        if (_available) StartLoop();
    }

    public void Stop() => StopLoop();

    public void TriggerManualRefresh() => _ = PollOnceAsync(CancellationToken.None);

    private void StartLoop()
    {
        if (_cts is { IsCancellationRequested: false }) return;
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private void StopLoop()
    {
        var oldCts = _cts;
        oldCts?.Cancel();
        oldCts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _available)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { }

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (!_available || string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_managementKey))
            return;

        if (!_statsEnabled)
            _statsEnabled = await TryEnableStatsAsync(ct);

        await DrainQueueAsync(ct);
    }

    /// <summary>PUT /usage-statistics-enabled {value:true} so the proxy starts filling the queue.</summary>
    private async Task<bool> TryEnableStatsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/usage-statistics-enabled")
            {
                Content = new StringContent("{\"value\":true}", Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Management-Key", _managementKey);
            using var resp = await Http.SendAsync(req, ct);
            // Treat unsupported endpoint as "enabled" so we still attempt to drain.
            return resp.IsSuccessStatusCode || resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest;
        }
        catch { return false; }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/usage-queue?count={DrainCount}");
            req.Headers.TryAddWithoutValidation("X-Management-Key", _managementKey);
            using var resp = await Http.SendAsync(req, ct);

            // 400/404 = endpoint not supported by this proxy build; treat as empty.
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return;
            if (!resp.IsSuccessStatusCode) return;

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (JsonNode.Parse(body) is not JsonArray array || array.Count == 0) return;

            var events = array
                .Select(UsageEvent.FromJson)
                .Where(e => e is not null)
                .Cast<UsageEvent>()
                .ToList();

            if (events.Count == 0) return;

            // Persist on this background thread before notifying the UI so history
            // survives restarts and the dashboard reads a consistent store.
            try { _store?.InsertEvents(events); } catch { /* non-fatal */ }

            Dispatcher.UIThread.Post(() => EventsLoaded?.Invoke(events));
        }
        catch { /* swallow — next poll retries */ }
    }

    public void Dispose() => StopLoop();
}
