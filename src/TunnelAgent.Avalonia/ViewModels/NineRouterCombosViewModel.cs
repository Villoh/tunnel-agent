using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Infrastructure.Engine.NineRouter;

namespace TunnelAgent.ViewModels;

/// <summary>One ordered model in a 9Router combo.</summary>
public sealed class NineRouterComboModelViewModel
{
    /// <summary>Creates a model entry.</summary>
    public NineRouterComboModelViewModel(int position, string name)
    {
        Position = position;
        Name = name;
    }

    /// <summary>Gets the one-based fallback position.</summary>
    public int Position { get; }

    /// <summary>Gets the 9Router model identifier.</summary>
    public string Name { get; }
}

/// <summary>One 9Router model combo shown in the Fallback view.</summary>
public sealed partial class NineRouterComboViewModel : ObservableObject
{
    /// <summary>Creates a view model for a 9Router combo.</summary>
    /// <param name="combo">Combo returned by 9Router.</param>
    /// <param name="strategy">The routing strategy for the combo.</param>
    /// <param name="judgeModel">Optional model that judges Fusion responses.</param>
    public NineRouterComboViewModel(NineRouterCombo combo, string? strategy, string? judgeModel)
    {
        Id = combo.Id ?? "";
        Name = combo.Name ?? "";
        Models = (combo.Models ?? [])
            .Select((name, index) => new NineRouterComboModelViewModel(index + 1, name))
            .ToList();
        Strategy = NormalizeStrategy(strategy);
        JudgeModel = judgeModel;
    }

    /// <summary>Gets the combo id used by 9Router.</summary>
    public string Id { get; }

    /// <summary>Gets the combo name exposed as a model.</summary>
    public string Name { get; }

    /// <summary>Gets the ordered models in the combo.</summary>
    public IReadOnlyList<NineRouterComboModelViewModel> Models { get; }

    /// <summary>Gets the number of models in the combo.</summary>
    public int ModelCount => Models.Count;

    /// <summary>Gets or sets the routing strategy.</summary>
    [ObservableProperty] private string _strategy = "fallback";

    /// <summary>Gets or sets the optional Fusion judge model.</summary>
    [ObservableProperty] private string? _judgeModel;

    /// <summary>Gets or sets whether combo details are expanded.</summary>
    [ObservableProperty] private bool _isExpanded;

    private static string NormalizeStrategy(string? strategy) => strategy switch
    {
        "round-robin" => "round-robin",
        "fusion" => "fusion",
        _ => "fallback"
    };
}

/// <summary>Loads and manages 9Router model combos.</summary>
public sealed partial class NineRouterCombosViewModel : ObservableObject
{
    private readonly Func<int> _port;
    private readonly Func<bool> _isEngineRunning;
    private readonly ObservableCollection<AvailableModelGroupViewModel> _modelGroups;
    private string? _editingComboId;
    private string? _editingComboName;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showCreatePanel;
    [ObservableProperty] private string _nameDraft = "";
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private string _draftStrategy = "fallback";
    [ObservableProperty] private string? _draftJudgeModel;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Gets the 9Router combos.</summary>
    public ObservableCollection<NineRouterComboViewModel> Combos { get; } = [];

    /// <summary>Gets the ordered models for the combo being created.</summary>
    public ObservableCollection<string> DraftModels { get; } = [];

    /// <summary>Gets all models reported by 9Router.</summary>
    public IReadOnlyList<string> AvailableModels => _modelGroups
        .SelectMany(group => group.Models.Select(model => model.Name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Gets whether at least one combo exists.</summary>
    public bool HasCombos => Combos.Count > 0;

    /// <summary>Gets whether 9Router is running.</summary>
    public bool IsEngineRunning => _isEngineRunning();

    /// <summary>Gets whether the draft contains at least one model.</summary>
    public bool HasDraftModels => DraftModels.Count > 0;

    /// <summary>Gets whether 9Router has reported any models to choose from.</summary>
    public bool HasAvailableModels => AvailableModels.Count > 0;

    /// <summary>Gets whether the dialog has the required values.</summary>
    public bool CanSubmit => IsValidName(NameDraft.Trim()) && DraftModels.Count > 0 && IsEngineRunning && !IsBusy;

    /// <summary>Gets whether the dialog is editing an existing combo.</summary>
    public bool IsEditing => _editingComboId is not null;

    /// <summary>Gets whether the creation dialog has Fusion selected.</summary>
    public bool IsDraftFusion => string.Equals(DraftStrategy, "fusion", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets or sets the strategy index used by the creation dialog selector.</summary>
    public int DraftStrategyIndex
    {
        get => DraftStrategy switch { "round-robin" => 1, "fusion" => 2, _ => 0 };
        set => DraftStrategy = value switch { 1 => "round-robin", 2 => "fusion", _ => "fallback" };
    }

    /// <summary>Gets whether an operation error should be displayed.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Initializes combo management against the active 9Router engine.</summary>
    /// <param name="port">Gets the active 9Router management port.</param>
    /// <param name="isEngineRunning">Gets whether 9Router is available.</param>
    /// <param name="modelGroups">Models reported by 9Router.</param>
    public NineRouterCombosViewModel(
        Func<int> port,
        Func<bool> isEngineRunning,
        ObservableCollection<AvailableModelGroupViewModel> modelGroups)
    {
        _port = port;
        _isEngineRunning = isEngineRunning;
        _modelGroups = modelGroups;
        _modelGroups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(HasAvailableModels));
        };
        Combos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCombos));
        DraftModels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(HasDraftModels));
        };
    }

    partial void OnNameDraftChanged(string value) => OnPropertyChanged(nameof(CanSubmit));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));
    partial void OnDraftStrategyChanged(string value)
    {
        OnPropertyChanged(nameof(DraftStrategyIndex));
        OnPropertyChanged(nameof(IsDraftFusion));
    }
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Refreshes engine-dependent state when the 9Router process changes state.</summary>
    public void NotifyEngineStateChanged()
    {
        OnPropertyChanged(nameof(IsEngineRunning));
        OnPropertyChanged(nameof(CanSubmit));
        if (!IsEngineRunning)
            CloseCreatePanel();
    }

    /// <summary>Loads combos and their strategy overrides from 9Router.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_isEngineRunning())
        {
            Combos.Clear();
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var client = new ApiClient(_port());
            var combos = await client.ListCombosAsync();
            var settings = await client.GetSettingsAsync();
            Combos.Clear();
            foreach (var combo in combos.OrderBy(combo => combo.Name, StringComparer.OrdinalIgnoreCase))
            {
                settings.ComboStrategies.TryGetValue(combo.Name ?? "", out var strategy);
                Combos.Add(new NineRouterComboViewModel(combo, strategy?.FallbackStrategy, strategy?.JudgeModel));
            }
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

    /// <summary>Opens the combo creation dialog.</summary>
    [RelayCommand]
    private void OpenCreatePanel()
    {
        if (!IsEngineRunning) return;
        _editingComboId = null;
        _editingComboName = null;
        OnPropertyChanged(nameof(IsEditing));
        ErrorMessage = null;
        NameDraft = "";
        DraftModels.Clear();
        SelectedModel = AvailableModels.FirstOrDefault();
        DraftStrategy = "fallback";
        DraftJudgeModel = null;
        ShowCreatePanel = true;
    }

    /// <summary>Opens an existing combo in the editor.</summary>
    [RelayCommand]
    private void OpenEditPanel(NineRouterComboViewModel? combo)
    {
        if (combo is null || !IsEngineRunning) return;
        _editingComboId = combo.Id;
        _editingComboName = combo.Name;
        OnPropertyChanged(nameof(IsEditing));
        ErrorMessage = null;
        NameDraft = combo.Name;
        DraftModels.Clear();
        foreach (var model in combo.Models)
            DraftModels.Add(model.Name);
        SelectedModel = AvailableModels.FirstOrDefault();
        DraftStrategy = combo.Strategy;
        DraftJudgeModel = combo.JudgeModel;
        ShowCreatePanel = true;
    }

    /// <summary>Closes and clears the combo dialog.</summary>
    [RelayCommand]
    private void CloseCreatePanel()
    {
        ShowCreatePanel = false;
        _editingComboId = null;
        _editingComboName = null;
        OnPropertyChanged(nameof(IsEditing));
        NameDraft = "";
        DraftModels.Clear();
        SelectedModel = null;
        DraftStrategy = "fallback";
        DraftJudgeModel = null;
        ErrorMessage = null;
    }

    /// <summary>Adds the selected model to the combo in creation order.</summary>
    [RelayCommand]
    private void AddModel()
    {
        if (string.IsNullOrWhiteSpace(SelectedModel) || DraftModels.Contains(SelectedModel, StringComparer.OrdinalIgnoreCase)) return;
        DraftModels.Add(SelectedModel);
    }

    /// <summary>Moves a draft model one position earlier.</summary>
    [RelayCommand]
    private void MoveDraftModelUp(string? model) => MoveDraftModel(model, -1);

    /// <summary>Moves a draft model one position later.</summary>
    [RelayCommand]
    private void MoveDraftModelDown(string? model) => MoveDraftModel(model, 1);

    /// <summary>Removes a model from the draft combo.</summary>
    [RelayCommand]
    private void RemoveDraftModel(string? model)
    {
        if (model is not null) DraftModels.Remove(model);
    }

    /// <summary>Creates or updates the combo through the 9Router management API.</summary>
    [RelayCommand]
    private async Task SaveComboAsync()
    {
        var name = NameDraft.Trim();
        if (!CanSubmit) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var strategy = DraftStrategy;
            var judgeModel = DraftJudgeModel;
            var previousName = _editingComboName;
            using var client = new ApiClient(_port());
            if (_editingComboId is null)
                await client.CreateComboAsync(new NineRouterCreateComboRequest { Name = name, Models = [.. DraftModels] });
            else
                await client.UpdateComboAsync(_editingComboId, new NineRouterUpdateComboRequest { Name = name, Models = [.. DraftModels] });

            var settings = await client.GetSettingsAsync();
            if (previousName is not null && !string.Equals(previousName, name, StringComparison.Ordinal))
                settings.ComboStrategies.Remove(previousName);
            settings.ComboStrategies[name] = new NineRouterComboStrategy
            {
                FallbackStrategy = strategy,
                JudgeModel = strategy == "fusion" ? judgeModel : null
            };
            await client.UpdateComboStrategiesAsync(settings.ComboStrategies);
            CloseCreatePanel();
            await RefreshAsync();
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

    /// <summary>Deletes a combo through the 9Router management API.</summary>
    [RelayCommand]
    private async Task DeleteAsync(NineRouterComboViewModel? combo)
    {
        if (combo is null || !_isEngineRunning() || IsBusy) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var client = new ApiClient(_port());
            await client.DeleteComboAsync(combo.Id);
            var settings = await client.GetSettingsAsync();
            if (settings.ComboStrategies.Remove(combo.Name))
                await client.UpdateComboStrategiesAsync(settings.ComboStrategies);
            await RefreshAsync();
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

    private void MoveDraftModel(string? model, int offset)
    {
        if (model is null) return;
        var index = DraftModels.IndexOf(model);
        var target = index + offset;
        if (index >= 0 && target >= 0 && target < DraftModels.Count)
            DraftModels.Move(index, target);
    }

    private static bool IsValidName(string name) => name.Length > 0 && name.All(character =>
        char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
}
