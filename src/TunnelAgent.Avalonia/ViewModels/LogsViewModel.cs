using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

public enum LogsTab { Requests, ProxyLogs }

public sealed record LogPageItem(int? PageNumber, string Label, bool IsCurrent, bool IsEllipsis);

public partial class LogsViewModel : ViewModelBase
{
    private const int PageSize       = 25;
    private const int MaxRequestLogs = 50_000;
    private const int ProxyLogsCap   = 100;

    // Usage-backed requests — newest first, capped to avoid unbounded memory growth.
    private readonly List<RequestLogEntry> _allEntries = new();
    private readonly HashSet<string> _usageEventKeys = new(StringComparer.OrdinalIgnoreCase);
    // Filtered results (search + provider + model) before pagination
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
    [ObservableProperty] private bool _showClearUsageConfirm;

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
    public ObservableCollection<LogPageItem> PageNavigationItems { get; } = new();
    public ObservableCollection<string> ProviderOptions { get; } = new();
    public ObservableCollection<string> ModelOptions { get; } = new();
    private string _allProvidersLabel = LocalizationService.Instance.GetString("LogsView_Requests_AllProviders");
    private string _allModelsLabel = LocalizationService.Instance.GetString("LogsView_Requests_AllModels");

    [ObservableProperty] private string _searchText       = "";
    [ObservableProperty] private string _selectedProvider = "";
    [ObservableProperty] private string _selectedModel    = "";

    public LogsViewModel()
    {
        ProviderOptions.Add(_allProvidersLabel);
        ModelOptions.Add(_allModelsLabel);
        _selectedProvider = _allProvidersLabel;
        _selectedModel = _allModelsLabel;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

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
    partial void OnSelectedModelChanged(string value)    { CurrentPage = 1; ApplyFilter(); }
    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        RebuildPageNavigation();
        ApplyPage();
    }

    partial void OnTotalPagesChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        RebuildPageNavigation();
    }

    [RelayCommand] private void FirstPage() { if (CanGoPrev) CurrentPage = 1; }
    [RelayCommand] private void PrevPage() { if (CanGoPrev) CurrentPage--; }
    [RelayCommand] private void NextPage() { if (CanGoNext) CurrentPage++; }
    [RelayCommand] private void LastPage() { if (CanGoNext) CurrentPage = TotalPages; }
    [RelayCommand]
    private void GoToPage(LogPageItem item)
    {
        if (item.PageNumber is { } page && page >= 1 && page <= TotalPages && page != CurrentPage)
            CurrentPage = page;
    }

    // ── Proxy Logs tab ───────────────────────────────────────────────────
    public ObservableCollection<string> ProxyLogLines { get; } = new();
    [ObservableProperty] private string _proxyLogsSearch = "";
    partial void OnProxyLogsSearchChanged(string value) => ApplyProxyFilter();

    private readonly List<string> _allProxyLines = new(ProxyLogsCap);

    // ── Called by MainWindowViewModel ────────────────────────────────────

    public void OnEntriesLoaded(IReadOnlyList<RequestLogEntry> entries, bool isInitialLoad)
    {
        // Requests tab is usage-backed now. Parsed file entries are intentionally
        // ignored; raw file lines still feed the Proxy logs tab.
    }

    public void OnUsageEventsLoaded(IReadOnlyList<UsageEvent> events)
    {
        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            var key = string.IsNullOrWhiteSpace(e.EventHash)
                ? string.Join('|', e.RequestId, e.Timestamp.Ticks, e.Model)
                : e.EventHash;
            if (!_usageEventKeys.Add(key)) continue;
            _allEntries.Insert(0, RequestLogEntry.FromUsageEvent(e));
        }

        TrimRequestEntries();
        RebuildFilterOptions();
        CurrentPage = 1;
        ApplyFilter();
    }

    public void OnUsageCleared()
    {
        _allEntries.Clear();
        _usageEventKeys.Clear();
        _filteredAll.Clear();
        ProviderOptions.Clear();
        ProviderOptions.Add(_allProvidersLabel);
        ModelOptions.Clear();
        ModelOptions.Add(_allModelsLabel);
        SelectedProvider = _allProvidersLabel;
        SelectedModel = _allModelsLabel;
        FilteredEntries.Clear();
        CurrentPage = 1;
        TotalPages = 1;
        UpdateStats([]);
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
        _allProxyLines.Clear();
        ProxyLogLines.Clear();
    }

    // ── Provider filter ──────────────────────────────────────────────────

    private void TrimRequestEntries()
    {
        if (_allEntries.Count <= MaxRequestLogs) return;
        _allEntries.RemoveRange(MaxRequestLogs, _allEntries.Count - MaxRequestLogs);
    }

    [RelayCommand]
    private void SelectProvider(string provider) => SelectedProvider = provider;

    private void RebuildFilterOptions()
    {
        RebuildOptions(
            ProviderOptions,
            _allProvidersLabel,
            _allEntries.Select(e => e.Provider),
            SelectedProvider,
            value => SelectedProvider = value);

        RebuildOptions(
            ModelOptions,
            _allModelsLabel,
            _allEntries.Select(e => e.Model).Where(m => m != "—"),
            SelectedModel,
            value => SelectedModel = value);
    }

    private static void RebuildOptions(ObservableCollection<string> options, string allLabel, IEnumerable<string> values, string selected, Action<string> setSelected)
    {
        var sorted = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

        var existing = options.Skip(1).ToList();
        foreach (var value in sorted.Where(v => !existing.Contains(v, StringComparer.OrdinalIgnoreCase)))
            options.Add(value);

        var toRemove = existing.Where(v => !sorted.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var value in toRemove) options.Remove(value);

        if (selected != allLabel && !sorted.Contains(selected, StringComparer.OrdinalIgnoreCase))
            setSelected(allLabel);
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var wasAllProvidersSelected = SelectedProvider == _allProvidersLabel;
        var wasAllModelsSelected = SelectedModel == _allModelsLabel;
        _allProvidersLabel = LocalizationService.Instance.GetString("LogsView_Requests_AllProviders");
        _allModelsLabel = LocalizationService.Instance.GetString("LogsView_Requests_AllModels");
        if (ProviderOptions.Count == 0)
            ProviderOptions.Add(_allProvidersLabel);
        else
            ProviderOptions[0] = _allProvidersLabel;
        if (ModelOptions.Count == 0)
            ModelOptions.Add(_allModelsLabel);
        else
            ModelOptions[0] = _allModelsLabel;
        if (wasAllProvidersSelected)
            SelectedProvider = _allProvidersLabel;
        if (wasAllModelsSelected)
            SelectedModel = _allModelsLabel;
    }

    // ── Filtering + pagination ────────────────────────────────────────────

    private void ApplyFilter()
    {
        var q          = SearchText.Trim();
        var byProvider = SelectedProvider != _allProvidersLabel;
        var byModel    = SelectedModel != _allModelsLabel;

        var filtered = _allEntries.AsEnumerable();

        if (byProvider)
            filtered = filtered.Where(e => string.Equals(e.Provider, SelectedProvider, StringComparison.OrdinalIgnoreCase));
        if (byModel)
            filtered = filtered.Where(e => string.Equals(e.Model, SelectedModel, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(q))
            filtered = filtered.Where(e =>
                e.Path.Contains(q,     StringComparison.OrdinalIgnoreCase) ||
                e.Method.Contains(q,   StringComparison.OrdinalIgnoreCase) ||
                e.Provider.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Model.Contains(q,    StringComparison.OrdinalIgnoreCase) ||
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
        RebuildPageNavigation();

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

    private void RebuildPageNavigation()
    {
        PageNavigationItems.Clear();
        foreach (var item in BuildPageNavigation(CurrentPage, TotalPages))
            PageNavigationItems.Add(item);
    }

    private static IEnumerable<LogPageItem> BuildPageNavigation(int currentPage, int totalPages)
    {
        if (totalPages <= 1) yield break;

        var last = 0;
        foreach (var page in Pages())
        {
            if (page - last > 1)
                yield return new LogPageItem(null, "…", false, true);

            yield return new LogPageItem(page, page.ToString(), page == currentPage, false);
            last = page;
        }

        IEnumerable<int> Pages()
        {
            const int visiblePages = 7;
            var set = new SortedSet<int> { 1, totalPages };
            var start = Math.Clamp(currentPage - visiblePages / 2, 2, Math.Max(2, totalPages - visiblePages));
            var end = Math.Min(totalPages - 1, start + visiblePages - 1);

            for (var page = start; page <= end; page++)
                set.Add(page);

            return set;
        }
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
        sb.AppendLine("Timestamp,Provider,Model,Path,Method,Status,Latency,RequestId");
        foreach (var e in _allEntries)
        {
            sb.Append(CsvField(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(','); sb.Append(CsvField(e.Provider));
            sb.Append(','); sb.Append(CsvField(e.Model));
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
