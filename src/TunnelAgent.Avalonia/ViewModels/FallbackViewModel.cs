using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IconPacks.Avalonia.SimpleIcons;
using CommunityToolkit.Mvvm.Input;

using TunnelAgent.Infrastructure.Engine.CliProxy;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

/// <summary>A provider/model option offered when adding a fallback entry.</summary>
public sealed class FallbackModelOption
{
    public FallbackModelOption(string providerId, string providerName, string modelId)
    {
        ProviderId = providerId;
        ProviderName = providerName;
        ModelId = modelId;
    }

    public string ProviderId { get; }
    public string ProviderName { get; }
    public string ModelId { get; }

    public string Display => $"{ProviderName} · {ModelId}";

    private ProviderIconRegistry.ProviderIconDisplay Icon => ProviderIconRegistry.GetDisplay(ProviderId, ProviderName);
    public PackIconSimpleIconsKind IconKind => Icon.IconKind;
    public string LogoColor => Icon.LogoColor;
    public string? CustomIconData => Icon.CustomIconData;
    public bool HasCustomIcon => Icon.HasCustomIcon;
    public bool ShowSimpleIcon => Icon.ShowSimpleIcon;
    public bool UseMonogram => Icon.UseMonogram;
    public string Monogram => Icon.Monogram;
}

public sealed class RouteCacheDurationOption(int minutes, string display, bool isResourceKey = false) : INotifyPropertyChanged
{
    public int Minutes { get; } = minutes;
    private string DisplayValue { get; } = display;
    public string Display => isResourceKey ? LocalizationService.Instance.GetString(DisplayValue) : DisplayValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RouteCacheDurationOption(int minutes, string display) : this(minutes, display, false) { }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
}

/// <summary>Editable view over a single <see cref="FallbackEntry"/>.</summary>
public partial class FallbackEntryEditViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    [ObservableProperty] private string _providerId;
    [ObservableProperty] private string _providerDisplayName;
    [ObservableProperty] private string _modelId;
    [ObservableProperty] private int _position;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    public string Id { get; }

    public string ProviderLabel =>
        string.IsNullOrWhiteSpace(ProviderDisplayName) ? ProviderId : ProviderDisplayName;

    private ProviderIconRegistry.ProviderIconDisplay Icon => ProviderIconRegistry.GetDisplay(ProviderId, ProviderLabel);
    public PackIconSimpleIconsKind IconKind => Icon.IconKind;
    public string LogoColor => Icon.LogoColor;
    public string? CustomIconData => Icon.CustomIconData;
    public bool HasCustomIcon => Icon.HasCustomIcon;
    public bool ShowSimpleIcon => Icon.ShowSimpleIcon;
    public bool UseMonogram => Icon.UseMonogram;
    public string Monogram => Icon.Monogram;

    public FallbackEntryEditViewModel(FallbackEntry entry, Action onChanged)
    {
        _onChanged = onChanged;
        Id = entry.Id;
        _providerId = entry.ProviderId;
        _providerDisplayName = entry.ProviderDisplayName;
        _modelId = entry.ModelId;
    }

    partial void OnModelIdChanged(string value) => _onChanged();
    partial void OnProviderIdChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderLabel));
        RaiseIconChanged();
        _onChanged();
    }
    partial void OnProviderDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderLabel));
        RaiseIconChanged();
    }

    private void RaiseIconChanged()
    {
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(LogoColor));
        OnPropertyChanged(nameof(CustomIconData));
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(ShowSimpleIcon));
        OnPropertyChanged(nameof(UseMonogram));
        OnPropertyChanged(nameof(Monogram));
    }

    public FallbackEntry ToModel(int priority) => new()
    {
        Id = Id,
        ProviderId = ProviderId,
        ProviderDisplayName = ProviderDisplayName,
        ModelId = ModelId,
        Priority = priority
    };
}

/// <summary>Editable view over a single <see cref="VirtualModel"/> and its fallback chain.</summary>
public partial class VirtualModelEditViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private readonly Func<IReadOnlyList<FallbackModelOption>> _optionsProvider;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isModelEnabled;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private FallbackModelOption? _newEntrySelection;
    [ObservableProperty] private FallbackRouteState? _routeState;

    public string Id { get; }
    public ObservableCollection<FallbackEntryEditViewModel> Entries { get; } = new();

    public int EntryCount => Entries.Count;
    public bool HasEntries => Entries.Count > 0;
    public bool CanRemoveEntry => Entries.Count > 1;
    public bool HasRouteState => RouteState is not null;
    public string RouteProgress => RouteState?.ProgressString ?? "";
    public string RouteProviderLabel => RouteState?.ProviderLabel ?? "";
    public string RouteModelId => RouteState?.ModelId ?? "";
    private ProviderIconRegistry.ProviderIconDisplay RouteIcon =>
        ProviderIconRegistry.GetDisplay(RouteState?.ProviderId ?? "", RouteState?.ProviderLabel);
    public PackIconSimpleIconsKind RouteIconKind => RouteIcon.IconKind;
    public string RouteLogoColor => RouteIcon.LogoColor;
    public string? RouteCustomIconData => RouteIcon.CustomIconData;
    public bool RouteHasCustomIcon => RouteIcon.HasCustomIcon;
    public bool RouteShowSimpleIcon => RouteIcon.ShowSimpleIcon;
    public bool RouteUseMonogram => RouteIcon.UseMonogram;
    public string RouteMonogram => RouteIcon.Monogram;

    public IReadOnlyList<FallbackModelOption> AvailableOptions => _optionsProvider();
    public bool HasAvailableOptions => AvailableOptions.Count > 0;

    public VirtualModelEditViewModel(
        VirtualModel model,
        Action onChanged,
        Func<IReadOnlyList<FallbackModelOption>> optionsProvider)
    {
        _onChanged = onChanged;
        _optionsProvider = optionsProvider;
        Id = model.Id;
        _name = model.Name;
        _isModelEnabled = model.Enabled;

        foreach (var entry in model.SortedEntries)
            Entries.Add(new FallbackEntryEditViewModel(entry, onChanged));

        Entries.CollectionChanged += (_, _) =>
        {
            Reindex();
            OnPropertyChanged(nameof(EntryCount));
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(CanRemoveEntry));
        };
        Reindex();
    }

    partial void OnNameChanged(string value) => _onChanged();
    partial void OnIsModelEnabledChanged(bool value) => _onChanged();
    partial void OnRouteStateChanged(FallbackRouteState? value)
    {
        OnPropertyChanged(nameof(HasRouteState));
        OnPropertyChanged(nameof(RouteProgress));
        OnPropertyChanged(nameof(RouteProviderLabel));
        OnPropertyChanged(nameof(RouteModelId));
        OnPropertyChanged(nameof(RouteIconKind));
        OnPropertyChanged(nameof(RouteLogoColor));
        OnPropertyChanged(nameof(RouteCustomIconData));
        OnPropertyChanged(nameof(RouteHasCustomIcon));
        OnPropertyChanged(nameof(RouteShowSimpleIcon));
        OnPropertyChanged(nameof(RouteUseMonogram));
        OnPropertyChanged(nameof(RouteMonogram));
    }

    public void RefreshOptions()
    {
        OnPropertyChanged(nameof(AvailableOptions));
        OnPropertyChanged(nameof(HasAvailableOptions));
    }

    private void Reindex()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            Entries[i].Position = i + 1;
            Entries[i].CanMoveUp = i > 0;
            Entries[i].CanMoveDown = i < Entries.Count - 1;
        }
    }

    [RelayCommand]
    private void AddEntry()
    {
        var option = NewEntrySelection;
        var entry = new FallbackEntry
        {
            ProviderId = option?.ProviderId ?? "",
            ProviderDisplayName = option?.ProviderName ?? "",
            ModelId = option?.ModelId ?? ""
        };
        Entries.Add(new FallbackEntryEditViewModel(entry, _onChanged));
        NewEntrySelection = null;
        _onChanged();
    }

    [RelayCommand]
    private void RemoveEntry(FallbackEntryEditViewModel? entry)
    {
        if (entry is null) return;
        Entries.Remove(entry);
        _onChanged();
    }

    [RelayCommand]
    private void MoveEntryUp(FallbackEntryEditViewModel? entry)
    {
        if (entry is null) return;
        var index = Entries.IndexOf(entry);
        if (index <= 0) return;
        Entries.Move(index, index - 1);
        _onChanged();
    }

    [RelayCommand]
    private void MoveEntryDown(FallbackEntryEditViewModel? entry)
    {
        if (entry is null) return;
        var index = Entries.IndexOf(entry);
        if (index < 0 || index >= Entries.Count - 1) return;
        Entries.Move(index, index + 1);
        _onChanged();
    }

    [RelayCommand]
    private void UseEntryNow(FallbackEntryEditViewModel? entry)
    {
        if (entry is null) return;
        var index = Entries.IndexOf(entry);
        if (index < 0) return;
        FallbackProxyService.SetCachedRoute(Name, entry.ToModel(index + 1), index, Entries.Count);
    }

    [RelayCommand]
    private void ResetCachedRoute()
    {
        FallbackProxyService.ClearCachedRoute(Name);
        RouteState = null;
    }

    public VirtualModel ToModel()
    {
        var model = new VirtualModel
        {
            Id = Id,
            Name = Name.Trim(),
            Enabled = IsModelEnabled
        };
        var priority = 1;
        foreach (var entry in Entries)
            model.Entries.Add(entry.ToModel(priority++));
        return model;
    }
}

/// <summary>
/// Drives the experimental Model Fallback view. Persists changes to settings and asks the
/// host to restart the engine when the bridge needs to be (de)activated.
/// </summary>
public partial class FallbackViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly Func<Task> _onActiveRoutesChanged;
    private readonly List<FallbackModelOption> _availableModels = new();
    private bool _loading;
    private bool _lastHasActiveRoutes;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _routeCachingEnabled;
    [ObservableProperty] private RouteCacheDurationOption _selectedRouteCacheDuration;
    [ObservableProperty] private bool _showAddModelPopup;
    [ObservableProperty] private FallbackModelOption? _selectedNewModelOption;

    public ObservableCollection<VirtualModelEditViewModel> VirtualModels { get; } = new();

    public IReadOnlyList<FallbackModelOption> AvailableModels => _availableModels;
    public IReadOnlyList<RouteCacheDurationOption> RouteCacheDurations { get; } =
    [
        new(15, "15 min"),
        new(30, "30 min"),
        new(60, "1 h"),
        new(360, "6 h"),
        new(1440, "24 h"),
        new(-1, "Fallback_RouteCache_UntilRestart", isResourceKey: true)
    ];

    public int VirtualModelCount => VirtualModels.Count;
    public bool HasVirtualModels => VirtualModels.Count > 0;
    public bool HasAvailableModels => AvailableModels.Count > 0;

    public FallbackViewModel(SettingsService settings, Func<Task> onActiveRoutesChanged)
    {
        _settings = settings;
        _onActiveRoutesChanged = onActiveRoutesChanged;

        var config = _settings.Current.Fallback;
        _loading = true;
        _isEnabled = config.Enabled;
        _routeCachingEnabled = config.RouteCachingEnabled;
        _selectedRouteCacheDuration = RouteCacheDurations.FirstOrDefault(o => o.Minutes == config.RouteCacheMinutes)
            ?? RouteCacheDurations.First(o => o.Minutes == 60);
        foreach (var model in config.VirtualModels)
            VirtualModels.Add(CreateModelVm(model));
        _loading = false;

        _lastHasActiveRoutes = config.HasActiveRoutes;

        VirtualModels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VirtualModelCount));
            OnPropertyChanged(nameof(HasVirtualModels));
            ApplyRouteStates(FallbackProxyService.SnapshotRouteStates());
        };

        FallbackProxyService.RouteStateChanged += OnRouteStateChanged;
        FallbackProxyService.RouteStatesCleared += OnRouteStatesCleared;
        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            foreach (var option in RouteCacheDurations) option.Refresh();
        };
        ApplyRouteStates(FallbackProxyService.SnapshotRouteStates());
    }

    private VirtualModelEditViewModel CreateModelVm(VirtualModel model) =>
        new(model, OnConfigurationMutated, () => AvailableModels);

    private void OnRouteStateChanged(FallbackRouteState state) =>
        Dispatcher.UIThread.Post(() => ApplyRouteState(state));

    private void OnRouteStatesCleared() =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var model in VirtualModels)
                model.RouteState = null;
        });

    private void ApplyRouteStates(IReadOnlyDictionary<string, FallbackRouteState> states)
    {
        foreach (var model in VirtualModels)
            model.RouteState = states.TryGetValue(model.Name, out var state) ? state : null;
    }

    private void ApplyRouteState(FallbackRouteState state)
    {
        foreach (var model in VirtualModels)
        {
            if (string.Equals(model.Name, state.VirtualModelName, StringComparison.OrdinalIgnoreCase))
                model.RouteState = state;
        }
    }

    /// <summary>Replaces the provider/model options shown when adding entries.</summary>
    public void SetAvailableModels(IEnumerable<FallbackModelOption> options)
    {
        _availableModels.Clear();
        _availableModels.AddRange(options
            .GroupBy(o => $"{o.ProviderId}\u0000{o.ModelId}")
            .Select(g => g.First())
            .OrderBy(o => o.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ModelId, StringComparer.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(AvailableModels));
        OnPropertyChanged(nameof(HasAvailableModels));
        foreach (var vm in VirtualModels) vm.RefreshOptions();
    }

    partial void OnIsEnabledChanged(bool value) => OnConfigurationMutated();
    partial void OnRouteCachingEnabledChanged(bool value) => OnConfigurationMutated();
    partial void OnSelectedRouteCacheDurationChanged(RouteCacheDurationOption value) => OnConfigurationMutated();

    [RelayCommand]
    private void OpenAddVirtualModelPopup()
    {
        SelectedNewModelOption = AvailableModels.FirstOrDefault();
        ShowAddModelPopup = true;
    }

    [RelayCommand]
    private void CloseAddVirtualModelPopup()
    {
        ShowAddModelPopup = false;
        SelectedNewModelOption = null;
    }

    [RelayCommand]
    private void AddVirtualModelFromSelection()
    {
        var option = SelectedNewModelOption;
        if (option is null) return;
        if (VirtualModels.Any(m => string.Equals(m.Name, option.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            CloseAddVirtualModelPopup();
            return;
        }

        var model = new VirtualModel
        {
            Name = option.ModelId,
            Entries =
            [
                new FallbackEntry
                {
                    ProviderId = option.ProviderId,
                    ProviderDisplayName = option.ProviderName,
                    ModelId = option.ModelId,
                    Priority = 1
                }
            ]
        };

        VirtualModels.Add(CreateModelVm(model));
        CloseAddVirtualModelPopup();
        OnConfigurationMutated();
    }

    [RelayCommand]
    private void RemoveVirtualModel(VirtualModelEditViewModel? model)
    {
        if (model is null) return;
        VirtualModels.Remove(model);
        OnConfigurationMutated();
    }

    /// <summary>Rebuilds and persists the configuration, restarting the engine if needed.</summary>
    private void OnConfigurationMutated()
    {
        if (_loading) return;

        var config = new FallbackConfiguration
        {
            Enabled = IsEnabled,
            RouteCachingEnabled = RouteCachingEnabled,
            RouteCacheMinutes = SelectedRouteCacheDuration.Minutes,
            VirtualModels = VirtualModels.Select(m => m.ToModel()).ToList()
        };

        _settings.Current.Fallback = config;
        _settings.Save();

        var nowActive = config.HasActiveRoutes;
        if (nowActive != _lastHasActiveRoutes)
        {
            _lastHasActiveRoutes = nowActive;
            _ = _onActiveRoutesChanged();
        }
    }
}
