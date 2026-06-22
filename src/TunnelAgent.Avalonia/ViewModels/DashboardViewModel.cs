using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

/// <summary>Time window applied to the dashboard metrics.</summary>
public enum DashboardRange { Today, Last7Days, Last14Days, Last30Days, All }

/// <summary>Which series the usage chart shows.</summary>
public enum DashboardChartMetric { Calls, Tokens, Cost }

/// <summary>
/// Aggregates per-request usage drained from the proxy's <c>/usage-queue</c> and
/// persisted in the usage store into the metrics shown on the Home/Dashboard:
/// headline stat cards (calls, tokens, estimated cost), a usage chart series and
/// a per-provider summary table. Telemetry-only (no log-file fallback), mirroring
/// quotio-desktop's dashboard aggregation.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private const int MaxEvents = 50_000;

    private readonly List<UsageEvent> _events = new();
    private readonly HashSet<string> _eventHashes = new();

    // ── Range tabs ──────────────────────────────────────────────────────
    [ObservableProperty] private DashboardRange _range = DashboardRange.Today;
    public int RangeIndex => (int)Range;

    partial void OnRangeChanged(DashboardRange value)
    {
        OnPropertyChanged(nameof(RangeIndex));
        Recompute();
    }

    [RelayCommand] private void SelectRange(string range)
    {
        if (Enum.TryParse<DashboardRange>(range, out var r)) Range = r;
    }

    // ── Chart metric tabs ───────────────────────────────────────────────
    [ObservableProperty] private DashboardChartMetric _chartMetric = DashboardChartMetric.Calls;
    public int ChartMetricIndex => (int)ChartMetric;

    partial void OnChartMetricChanged(DashboardChartMetric value)
    {
        OnPropertyChanged(nameof(ChartMetricIndex));
        RebuildChart();
    }

    [RelayCommand] private void SelectChartMetric(string metric)
    {
        if (Enum.TryParse<DashboardChartMetric>(metric, out var m)) ChartMetric = m;
    }

    // ── Headline cards ──────────────────────────────────────────────────
    [ObservableProperty] private int _totalCalls;
    [ObservableProperty] private string _successRate = "–";
    [ObservableProperty] private int _failureCount;
    [ObservableProperty] private string _failureRate = "–";
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private string _providerSummaryCount = "0";
    [ObservableProperty] private string _avgLatency = "–";

    [ObservableProperty] private string _estimatedCost = "$0.00";
    [ObservableProperty] private bool _hasCost;
    [ObservableProperty] private string _totalTokens = "0";
    [ObservableProperty] private string _inputTokens = "0";
    [ObservableProperty] private string _outputTokens = "0";
    [ObservableProperty] private string _cacheTokens = "0";
    [ObservableProperty] private string _inputTokenRatio = "";
    [ObservableProperty] private string _outputTokenRatio = "";
    [ObservableProperty] private string _cacheHitRate = "";
    [ObservableProperty] private string _reasoningTokens = "";
    [ObservableProperty] private bool _hasTokenData;

    // ── Chart ───────────────────────────────────────────────────────────
[ObservableProperty] private IReadOnlyList<double> _chartValues = Array.Empty<double>();
[ObservableProperty] private IReadOnlyList<string> _chartLabels = Array.Empty<string>();
[ObservableProperty] private string _chartAxisStart = "";
    [ObservableProperty] private string _chartAxisEnd = "";
    [ObservableProperty] private string _chartAxisMax = "";
    [ObservableProperty] private bool _hasData;

    // ── Provider summary table ──────────────────────────────────────────
    public ObservableCollection<ProviderUsageRow> ProviderRows { get; } = new();

    // ── Feeds ───────────────────────────────────────────────────────────

    /// <summary>Accumulate newly drained usage events, deduped by hash.</summary>
    public void OnUsageEventsLoaded(IReadOnlyList<UsageEvent> events)
    {
        var added = false;
        foreach (var e in events)
        {
            if (!_eventHashes.Add(e.EventHash)) continue;
            _events.Add(e);
            added = true;
        }

        if (_events.Count > MaxEvents)
        {
            _events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            var overflow = _events.Count - MaxEvents;
            for (var i = 0; i < overflow; i++) _eventHashes.Remove(_events[i].EventHash);
            _events.RemoveRange(0, overflow);
        }

        if (added) Recompute();
    }

    public void OnCleared()
    {
        _events.Clear();
        _eventHashes.Clear();
        Recompute();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Recompute();
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(700);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsRefreshing = false);
        });
    }

    [ObservableProperty] private bool _isRefreshing;

    // ── Aggregation ─────────────────────────────────────────────────────
    private DateTime RangeStart(DateTime now) => Range switch
    {
        DashboardRange.Today      => now.Date,
        DashboardRange.Last7Days  => now.Date.AddDays(-6),
        DashboardRange.Last14Days => now.Date.AddDays(-13),
        DashboardRange.Last30Days => now.Date.AddDays(-29),
        _                         => DateTime.MinValue
    };

    private List<UsageEvent> EventsInRange()
    {
        var start = RangeStart(DateTime.Now);
        return _events
            .Where(e => e.Timestamp.Year >= 2000) // ignore bad/epoch timestamps
            .Where(e => e.Timestamp >= start)
            .ToList();
    }

    private void Recompute() => RecomputeFromEvents(EventsInRange());

    private void RecomputeFromEvents(List<UsageEvent> source)
    {
        TotalCalls = source.Count;
        SuccessCount = source.Count(e => e.IsSuccess);
        FailureCount = source.Count(e => !e.IsSuccess);
        HasData = source.Count > 0;

        SetRates();

        var withLatency = source.Where(e => e.LatencyMs > 0).ToList();
        AvgLatency = withLatency.Count == 0 ? "–" : FormatLatency(withLatency.Average(e => e.LatencyMs));

        // Tokens
        var input = source.Sum(e => e.InputTokens);
        var output = source.Sum(e => e.OutputTokens);
        var cached = source.Sum(e => e.CachedTokens);
        var reasoning = source.Sum(e => e.ReasoningTokens);
        var total = source.Sum(e => e.TotalTokens);

        HasTokenData = total > 0;
        TotalTokens = FormatCount(total);
        InputTokens = FormatCount(input);
        OutputTokens = FormatCount(output);
        CacheTokens = FormatCount(cached);
        ReasoningTokens = FormatCount(reasoning);
        InputTokenRatio = Ratio(input, total);
        OutputTokenRatio = Ratio(output, total);
        // Cached vs. total input (fresh + cached); bounded to [0,100].
        CacheHitRate = Ratio(cached, input + cached);

        // Cost
        HasCost = ModelPricing.HasKnownPrice(source);
        var cost = source.Sum(ModelPricing.CostFor);
        EstimatedCost = "$" + cost.ToString("N2", CultureInfo.InvariantCulture);

        RebuildProviderRowsFromEvents(source);
        RebuildChart(source);
    }

    private void SetRates()
    {
        if (TotalCalls == 0)
        {
            SuccessRate = "–";
            FailureRate = "–";
        }
        else
        {
            SuccessRate = (SuccessCount * 100.0 / TotalCalls).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            FailureRate = (FailureCount * 100.0 / TotalCalls).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }
    }

    private void RebuildProviderRowsFromEvents(List<UsageEvent> source)
    {
        var groups = source
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Provider) ? "—" : e.Provider!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var total = g.Count();
                var ok = g.Count(e => e.IsSuccess);
                var fail = total - ok;
                var cost = g.Sum(ModelPricing.CostFor);
                return new ProviderUsageRow
                {
                    Provider = g.Key,
                    TotalCalls = total,
                    SuccessCount = ok,
                    FailureCount = fail,
                    SuccessRate = total == 0 ? "–" : (ok * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    TotalTokens = FormatCount(g.Sum(e => e.TotalTokens)),
                    EstimatedCost = "$" + cost.ToString("N2", CultureInfo.InvariantCulture),
                    LastRequest = g.Max(e => e.Timestamp).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                };
            })
            .OrderByDescending(r => r.TotalCalls)
            .ToList();

        ApplyProviderRows(groups);
    }

    private void ApplyProviderRows(List<ProviderUsageRow> rows)
    {
        ProviderRows.Clear();
        foreach (var row in rows) ProviderRows.Add(row);
        ProviderSummaryCount = rows.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void RebuildChart() => Recompute();

    private void RebuildChart(List<UsageEvent> source)
    {
        BuildChart(source.Count, e => e.Timestamp,
            ChartMetric switch
            {
                DashboardChartMetric.Tokens => (Func<UsageEvent, double>)(e => e.TotalTokens),
                DashboardChartMetric.Cost   => ModelPricing.CostFor,
                _                           => _ => 1,
            },
            source,
            ChartMetric == DashboardChartMetric.Cost);
    }

    private void BuildChart<T>(int count, Func<T, DateTime> time, Func<T, double> value, List<T> source, bool isCost)
    {
        const int buckets = 24;

        if (count == 0)
        {
            ChartValues = Array.Empty<double>();
            ChartLabels = Array.Empty<string>();
            ChartAxisStart = ChartAxisEnd = ChartAxisMax = "";
            return;
        }

        var now = DateTime.Now;
        var start = Range == DashboardRange.All ? source.Min(time) : RangeStart(now);
        var end = Range == DashboardRange.All ? source.Max(time) : now;
        var span = end - start;
        if (span <= TimeSpan.Zero)
        {
            span = TimeSpan.FromHours(1);
            end = start + span;
        }
        var bucketTicks = Math.Max(1, span.Ticks / buckets);

        var series = new double[buckets];
        foreach (var item in source)
        {
            var idx = (int)((time(item) - start).Ticks / bucketTicks);
            if (idx < 0) idx = 0;
            if (idx >= buckets) idx = buckets - 1;
            series[idx] += value(item);
        }

        ChartValues = series;
        var labelFmt = span.TotalDays >= 2 ? "MM-dd HH:mm" : "HH:mm";
        ChartLabels = series.Select((v, i) =>
        {
            var t = start.AddTicks(bucketTicks * i);
            var formatted = isCost
                ? "$" + v.ToString("0.##", CultureInfo.InvariantCulture)
                : FormatCount((long)Math.Round(v));
            return t.ToString(labelFmt, CultureInfo.InvariantCulture) + " • " + formatted;
        }).ToArray();
        var maxVal = series.Max();
        ChartAxisMax = maxVal <= 0
            ? ""
            : isCost
                ? "$" + maxVal.ToString("0.##", CultureInfo.InvariantCulture)
                : FormatCount((long)Math.Round(maxVal));

        var fmt = span.TotalDays >= 2 ? "MM-dd" : "HH:mm";
        ChartAxisStart = start.ToString(fmt, CultureInfo.InvariantCulture);
        ChartAxisEnd = end.ToString(fmt, CultureInfo.InvariantCulture);
    }

    // ── Formatting ──────────────────────────────────────────────────────
    private static string FormatLatency(double ms) =>
        ms >= 1000
            ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s"
            : ms.ToString("0", CultureInfo.InvariantCulture) + "ms";

    private static string FormatCount(long value)
    {
        if (value >= 1_000_000_000) return (value / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "B";
        if (value >= 1_000_000) return (value / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (value >= 1_000) return (value / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Ratio(long part, long whole) =>
        whole <= 0 ? "" : (part * 100.0 / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>A single row in the dashboard's per-provider usage summary.</summary>
public sealed class ProviderUsageRow
{
    public string Provider { get; init; } = "";
    public int TotalCalls { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public string SuccessRate { get; init; } = "–";
    public string TotalTokens { get; init; } = "0";
    public string EstimatedCost { get; init; } = "$0.00";
    public string LastRequest { get; init; } = "";
}
