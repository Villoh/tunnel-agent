using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TunnelAgent.ViewModels;

public enum LogsTab { Requests, ProxyLogs }

public partial class LogsViewModel : ViewModelBase
{
    private const int PageSize     = 25;
    private const int MaxRequestLogs = 5000;
    private const int ProxyLogsCap = 100;

    // Full backing store — newest first, capped to avoid unbounded memory growth.
    private readonly List<RequestLogEntry> _allEntries = new();
    // Filtered results (search + provider) before pagination
    private List<RequestLogEntry> _filteredAll = new();

    // ── Tabs ─────────────────────────────────────────────────────────────
    [ObservableProperty] private LogsTab _selectedTab = LogsTab.Requests;
    public int  SelectedTabIndex => (int)SelectedTab;
    public bool IsRequestsTab    => SelectedTab == LogsTab.Requests;
    public bool IsProxyLogsTab   => SelectedTab == LogsTab.ProxyLogs;
    partial void OnSelectedTabChanged(LogsTab value)
    {
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(IsRequestsTab));
        OnPropertyChanged(nameof(IsProxyLogsTab));
    }

    [RelayCommand] private void SelectRequestsTab()  => SelectedTab = LogsTab.Requests;
    [RelayCommand] private void SelectProxyLogsTab() => SelectedTab = LogsTab.ProxyLogs;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _showClearConfirm;

    public async Task RefreshWithSpinAsync(Action action)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        action();
        await Task.Delay(700);
        IsRefreshing = false;
    }

    // ── Requests tab ─────────────────────────────────────────────────────
    public ObservableCollection<RequestLogEntry> FilteredEntries { get; } = new();
    public ObservableCollection<string> ProviderOptions { get; } = new() { "All Providers" };

    [ObservableProperty] private string _searchText       = "";
    [ObservableProperty] private string _selectedProvider = "All Providers";

    // Pagination
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages  = 1;

    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;

    public string PageLabel => TotalPages <= 1 ? "" : $"{CurrentPage} / {TotalPages}";

    // Summary stats (computed from ALL filtered entries, not just current page)
    [ObservableProperty] private int    _totalRequests;
    [ObservableProperty] private string _successRate = "–";
    [ObservableProperty] private string _avgTime     = "–";

    partial void OnSearchTextChanged(string value)       { CurrentPage = 1; ApplyFilter(); }
    partial void OnSelectedProviderChanged(string value) { CurrentPage = 1; ApplyFilter(); }
    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        ApplyPage();
    }

    [RelayCommand] private void PrevPage() { if (CanGoPrev) CurrentPage--; }
    [RelayCommand] private void NextPage() { if (CanGoNext) CurrentPage++; }

    // ── Proxy Logs tab ───────────────────────────────────────────────────
    public ObservableCollection<string> ProxyLogLines { get; } = new();
    [ObservableProperty] private string _proxyLogsSearch = "";
    partial void OnProxyLogsSearchChanged(string value) => ApplyProxyFilter();

    private readonly List<string> _allProxyLines = new(ProxyLogsCap);

    // ── Called by MainWindowViewModel ────────────────────────────────────

    public void OnEntriesLoaded(IReadOnlyList<RequestLogEntry> entries, bool isInitialLoad)
    {
        if (isInitialLoad)
        {
            _allEntries.Clear();
            _allEntries.AddRange(entries);
        }
        else
        {
            foreach (var e in entries.Reverse())
                _allEntries.Insert(0, e);
        }

        TrimRequestEntries();
        RebuildProviderOptions();
        CurrentPage = 1;
        ApplyFilter();
    }

    public void OnRawLinesLoaded(IReadOnlyList<string> lines, bool isInitialLoad)
    {
        if (isInitialLoad)
        {
            _allProxyLines.Clear();
            _allProxyLines.AddRange(lines.Take(ProxyLogsCap));
        }
        else
        {
            foreach (var l in lines.Reverse())
            {
                _allProxyLines.Insert(0, l);
                if (_allProxyLines.Count > ProxyLogsCap)
                    _allProxyLines.RemoveAt(_allProxyLines.Count - 1);
            }
        }

        ApplyProxyFilter();
    }

    public void OnCleared()
    {
        _allEntries.Clear();
        _allProxyLines.Clear();
        _filteredAll.Clear();
        ProviderOptions.Clear();
        ProviderOptions.Add("All Providers");
        SelectedProvider = "All Providers";
        FilteredEntries.Clear();
        ProxyLogLines.Clear();
        CurrentPage = 1;
        TotalPages  = 1;
        UpdateStats([]);
    }

    // ── Provider filter ──────────────────────────────────────────────────

    private void TrimRequestEntries()
    {
        if (_allEntries.Count <= MaxRequestLogs) return;
        _allEntries.RemoveRange(MaxRequestLogs, _allEntries.Count - MaxRequestLogs);
    }

    [RelayCommand]
    private void SelectProvider(string provider) => SelectedProvider = provider;

    private void RebuildProviderOptions()
    {
        var providers = _allEntries
            .Select(e => e.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        var existing = ProviderOptions.Skip(1).ToList();
        foreach (var p in providers.Where(p => !existing.Contains(p, StringComparer.OrdinalIgnoreCase)))
            ProviderOptions.Add(p);

        var toRemove = existing.Where(p => !providers.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var p in toRemove) ProviderOptions.Remove(p);

        if (SelectedProvider != "All Providers" &&
            !providers.Contains(SelectedProvider, StringComparer.OrdinalIgnoreCase))
            SelectedProvider = "All Providers";
    }

    // ── Filtering + pagination ────────────────────────────────────────────

    private void ApplyFilter()
    {
        var q          = SearchText.Trim();
        var byProvider = SelectedProvider != "All Providers";

        var filtered = _allEntries.AsEnumerable();

        if (byProvider)
            filtered = filtered.Where(e => string.Equals(e.Provider, SelectedProvider, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(q))
            filtered = filtered.Where(e =>
                e.Path.Contains(q,     StringComparison.OrdinalIgnoreCase) ||
                e.Method.Contains(q,   StringComparison.OrdinalIgnoreCase) ||
                e.Provider.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.StatusCode.ToString().Contains(q) ||
                e.LatencyRaw.Contains(q, StringComparison.OrdinalIgnoreCase));

        _filteredAll = filtered
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        TotalPages = Math.Max(1, (int)Math.Ceiling(_filteredAll.Count / (double)PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));

        ApplyPage();
        UpdateStats(_filteredAll);
    }

    private void ApplyPage()
    {
        var page = _filteredAll
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        FilteredEntries.Clear();
        foreach (var e in page) FilteredEntries.Add(e);
    }

    private void ApplyProxyFilter()
    {
        var q = ProxyLogsSearch.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allProxyLines
            : _allProxyLines.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        ProxyLogLines.Clear();
        foreach (var l in filtered.Take(ProxyLogsCap)) ProxyLogLines.Add(l);
    }

    private void UpdateStats(List<RequestLogEntry> source)
    {
        TotalRequests = source.Count;
        if (TotalRequests == 0) { SuccessRate = "–"; AvgTime = "–"; return; }

        var successCount = source.Count(e => e.IsSuccess);
        SuccessRate = $"{Math.Round(successCount * 100.0 / TotalRequests)}%";

        var withLatency = source.Where(e => e.Latency > TimeSpan.Zero).ToList();
        if (withLatency.Count == 0) { AvgTime = "–"; return; }

        var avgMs = withLatency.Average(e => e.Latency.TotalMilliseconds);
        AvgTime = avgMs >= 1000 ? $"{avgMs / 1000:F1}s" : $"{avgMs:F0}ms";
    }

    // ── Export ───────────────────────────────────────────────────────────

    public string BuildRequestsCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Provider,Path,Method,Status,Latency,RequestId");
        foreach (var e in _allEntries)
        {
            sb.Append(CsvField(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(','); sb.Append(CsvField(e.Provider));
            sb.Append(','); sb.Append(CsvField(e.Path));
            sb.Append(','); sb.Append(CsvField(e.Method));
            sb.Append(','); sb.Append(e.StatusCode);
            sb.Append(','); sb.Append(CsvField(e.LatencyRaw));
            sb.Append(','); sb.Append(CsvField(e.RequestId));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public string BuildProxyLog() =>
        string.Join(Environment.NewLine, _allProxyLines);

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
