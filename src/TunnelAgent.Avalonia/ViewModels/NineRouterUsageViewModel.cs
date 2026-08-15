using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Infrastructure.Engine.NineRouter;

namespace TunnelAgent.ViewModels;

/// <summary>Displays 9Router's live usage aggregates and redacted request metadata on Home.</summary>
public sealed partial class NineRouterUsageViewModel : ViewModelBase
{
    private const int PageSize = 20;
    private readonly Func<int> _port;
    private readonly Func<bool> _isEngineRunning;

    /// <summary>Creates the 9Router usage panel.</summary>
    /// <param name="port">Gets the active 9Router management port.</param>
    /// <param name="isEngineRunning">Gets whether 9Router is running.</param>
    public NineRouterUsageViewModel(Func<int> port, Func<bool> isEngineRunning)
    {
        _port = port;
        _isEngineRunning = isEngineRunning;
    }

    /// <summary>Gets the provider aggregates returned by 9Router.</summary>
    public ObservableCollection<NineRouterUsageProviderRow> ProviderRows { get; } = [];

    /// <summary>Gets the active requests returned by 9Router.</summary>
    public ObservableCollection<NineRouterActiveRequest> ActiveRequests { get; } = [];

    /// <summary>Gets the current page of redacted request metadata.</summary>
    public ObservableCollection<NineRouterUsageRequestRow> RequestRows { get; } = [];

    /// <summary>Gets the navigation items for request-detail paging.</summary>
    public ObservableCollection<LogPageItem> PageNavigationItems { get; } = [];

    /// <summary>Gets whether the 9Router process is available.</summary>
    public bool IsEngineRunning => _isEngineRunning();

    /// <summary>Gets whether the panel has loaded a response from 9Router.</summary>
    [ObservableProperty] private bool _isLoaded;

    /// <summary>Gets whether a request to 9Router is in progress.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Gets a safe error message for a failed 9Router request.</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Gets the total request count for the last seven days.</summary>
    [ObservableProperty] private string _totalRequests = "0";

    /// <summary>Gets prompt plus completion tokens for the last seven days.</summary>
    [ObservableProperty] private string _totalTokens = "0";

    /// <summary>Gets cached tokens for the last seven days.</summary>
    [ObservableProperty] private string _cachedTokens = "0";

    /// <summary>Gets cost reported by 9Router for the last seven days.</summary>
    [ObservableProperty] private string _totalCost = "$0.00";

    /// <summary>Gets the number of requests currently in flight.</summary>
    [ObservableProperty] private string _activeRequestCount = "0";

    /// <summary>Gets the total number of request-detail records.</summary>
    [ObservableProperty] private int _totalRequestDetails;

    /// <summary>Gets the current one-based request-details page.</summary>
    [ObservableProperty] private int _currentPage = 1;

    /// <summary>Gets the total number of request-details pages.</summary>
    [ObservableProperty] private int _totalPages = 1;

    /// <summary>Gets whether the latest refresh failed.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Gets whether provider aggregates exist.</summary>
    public bool HasProviderRows => ProviderRows.Count > 0;

    /// <summary>Gets whether active requests exist.</summary>
    public bool HasActiveRequests => ActiveRequests.Count > 0;

    /// <summary>Gets whether request details exist.</summary>
    public bool HasRequestRows => RequestRows.Count > 0;

    /// <summary>Gets whether a previous request-details page exists.</summary>
    public bool CanGoPrev => CurrentPage > 1;

    /// <summary>Gets whether a next request-details page exists.</summary>
    public bool CanGoNext => CurrentPage < TotalPages;

    /// <summary>Gets a compact page label.</summary>
    public string PageLabel => TotalPages <= 1 ? "" : $"{CurrentPage} / {TotalPages}";

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        RebuildPageNavigation();
    }

    partial void OnTotalPagesChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
        RebuildPageNavigation();
    }

    /// <summary>Updates engine-dependent state and clears sensitive runtime data after 9Router stops.</summary>
    public void NotifyEngineStateChanged()
    {
        OnPropertyChanged(nameof(IsEngineRunning));
        if (!IsEngineRunning) Clear();
    }

    /// <summary>Loads 9Router's seven-day usage aggregates and the selected page of request details.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsEngineRunning)
        {
            Clear();
            return;
        }
        if (IsBusy) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var client = ApiClient.CreateDashboardClient(_port());
            var stats = await client.GetUsageStatsAsync("7d");
            var details = await client.ListRequestDetailsAsync(CurrentPage, PageSize);
            Apply(stats, details);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private async Task FirstPageAsync()
    {
        if (!CanGoPrev) return;
        CurrentPage = 1;
        await RefreshAsync();
    }

    [RelayCommand] private async Task PrevPageAsync()
    {
        if (!CanGoPrev) return;
        CurrentPage--;
        await RefreshAsync();
    }

    [RelayCommand] private async Task NextPageAsync()
    {
        if (!CanGoNext) return;
        CurrentPage++;
        await RefreshAsync();
    }

    [RelayCommand] private async Task LastPageAsync()
    {
        if (!CanGoNext) return;
        CurrentPage = TotalPages;
        await RefreshAsync();
    }

    [RelayCommand] private async Task GoToPageAsync(LogPageItem? item)
    {
        if (item?.PageNumber is not { } page || page == CurrentPage || page < 1 || page > TotalPages) return;
        CurrentPage = page;
        await RefreshAsync();
    }

    private void Apply(NineRouterUsageStats stats, NineRouterRequestDetailsPage details)
    {
        TotalRequests = FormatCount(stats.TotalRequests);
        TotalTokens = FormatCount(stats.TotalPromptTokens + stats.TotalCompletionTokens);
        CachedTokens = FormatCount(stats.TotalCachedTokens);
        TotalCost = "$" + stats.TotalCost.ToString("N2", CultureInfo.InvariantCulture);
        ActiveRequestCount = FormatCount(stats.ActiveRequests.Sum(request => request.Count));

        ProviderRows.Clear();
        foreach (var provider in stats.ByProvider.OrderByDescending(entry => entry.Value.Requests))
            ProviderRows.Add(new NineRouterUsageProviderRow(provider.Key, provider.Value));

        ActiveRequests.Clear();
        foreach (var request in stats.ActiveRequests)
            ActiveRequests.Add(request);

        RequestRows.Clear();
        foreach (var detail in details.Details)
            RequestRows.Add(new NineRouterUsageRequestRow(detail));

        var pagination = details.Pagination;
        TotalRequestDetails = pagination.TotalItems;
        TotalPages = Math.Max(1, pagination.TotalPages);
        CurrentPage = Math.Clamp(pagination.Page, 1, TotalPages);
        IsLoaded = true;
        OnPropertyChanged(nameof(HasProviderRows));
        OnPropertyChanged(nameof(HasActiveRequests));
        OnPropertyChanged(nameof(HasRequestRows));
    }

    private void Clear()
    {
        ProviderRows.Clear();
        ActiveRequests.Clear();
        RequestRows.Clear();
        PageNavigationItems.Clear();
        TotalRequests = TotalTokens = CachedTokens = ActiveRequestCount = "0";
        TotalCost = "$0.00";
        TotalRequestDetails = 0;
        CurrentPage = 1;
        TotalPages = 1;
        IsLoaded = false;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasProviderRows));
        OnPropertyChanged(nameof(HasActiveRequests));
        OnPropertyChanged(nameof(HasRequestRows));
    }

    private void RebuildPageNavigation()
    {
        PageNavigationItems.Clear();
        if (TotalPages <= 1) return;

        var pages = new SortedSet<int> { 1, TotalPages };
        for (var page = Math.Max(2, CurrentPage - 2); page <= Math.Min(TotalPages - 1, CurrentPage + 2); page++)
            pages.Add(page);

        var previous = 0;
        foreach (var page in pages)
        {
            if (page - previous > 1)
                PageNavigationItems.Add(new LogPageItem(null, "…", false, true));
            PageNavigationItems.Add(new LogPageItem(page, page.ToString(CultureInfo.InvariantCulture), page == CurrentPage, false));
            previous = page;
        }
    }

    private static string FormatCount(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>One provider aggregate in the 9Router Home panel.</summary>
public sealed class NineRouterUsageProviderRow
{
    /// <summary>Creates a provider aggregate row.</summary>
    public NineRouterUsageProviderRow(string provider, NineRouterUsageBucket usage)
    {
        Provider = string.IsNullOrWhiteSpace(provider) ? "—" : provider;
        Requests = FormatCount(usage.Requests);
        Tokens = FormatCount(usage.PromptTokens + usage.CompletionTokens);
        CachedTokens = FormatCount(usage.CachedTokens);
        Cost = "$" + usage.Cost.ToString("N2", CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the provider label.</summary>
    public string Provider { get; }
    /// <summary>Gets the formatted request count.</summary>
    public string Requests { get; }
    /// <summary>Gets formatted prompt plus completion tokens.</summary>
    public string Tokens { get; }
    /// <summary>Gets formatted cached tokens.</summary>
    public string CachedTokens { get; }
    /// <summary>Gets the formatted reported cost.</summary>
    public string Cost { get; }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One redacted request-detail row in the 9Router Home panel.</summary>
public sealed class NineRouterUsageRequestRow
{
    /// <summary>Creates a display row from 9Router's redacted request metadata.</summary>
    public NineRouterUsageRequestRow(NineRouterRequestDetail detail)
    {
        Provider = detail.Provider ?? "—";
        Model = detail.Model ?? "—";
        Connection = detail.ConnectionId ?? "—";
        Status = detail.Status ?? "—";
        Timestamp = FormatTimestamp(detail.Timestamp);
        Tokens = FormatCount(InputTokens(detail.Tokens) + OutputTokens(detail.Tokens));
        Latency = FormatLatency(detail.Latency);
    }

    /// <summary>Gets the provider label.</summary>
    public string Provider { get; }
    /// <summary>Gets the model label.</summary>
    public string Model { get; }
    /// <summary>Gets the selected connection id.</summary>
    public string Connection { get; }
    /// <summary>Gets the request status.</summary>
    public string Status { get; }
    /// <summary>Gets the formatted request timestamp.</summary>
    public string Timestamp { get; }
    /// <summary>Gets the formatted token count.</summary>
    public string Tokens { get; }
    /// <summary>Gets the formatted elapsed time.</summary>
    public string Latency { get; }

    private static long InputTokens(NineRouterUsageTokens? tokens) =>
        tokens?.PromptTokens is > 0 ? tokens.PromptTokens : tokens?.InputTokens ?? 0;

    private static long OutputTokens(NineRouterUsageTokens? tokens) =>
        tokens?.CompletionTokens is > 0 ? tokens.CompletionTokens : tokens?.OutputTokens ?? 0;

    private static string FormatTimestamp(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatLatency(JsonElement? latency)
    {
        if (latency is not { ValueKind: JsonValueKind.Object } value) return "—";
        foreach (var name in new[] { "total", "totalMs", "duration", "latency" })
        {
            if (value.TryGetProperty(name, out var field) && field.TryGetDouble(out var milliseconds))
                return milliseconds >= 1000
                    ? (milliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s"
                    : milliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
        }
        return "—";
    }

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
