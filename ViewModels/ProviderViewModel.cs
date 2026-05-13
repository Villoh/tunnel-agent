using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.ViewModels;

// ── Account slot (one API key for a custom provider) ─────────────────────────

public partial class ProviderAccountViewModel : ViewModelBase
{
    public string ProviderId { get; }
    public string ApiKey     { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private bool   _isDisabled;

    public string MaskedKey => ApiKey.Length > 12
        ? $"{ApiKey[..8]}...{ApiKey[^4..]}"
        : ApiKey;

    public ProviderAccountViewModel(string providerId, string apiKey, string label, bool isDisabled)
    {
        ProviderId  = providerId;
        ApiKey      = apiKey;
        _label      = label;
        _isDisabled = isDisabled;
    }
}

// ── Provider row ─────────────────────────────────────────────────────────────

public partial class ProviderViewModel : ViewModelBase
{
    public string Id          { get; }
    public string Name        { get; }
    public PackIconSimpleIconsKind IconKind { get; }
    public string LogoColor   { get; }
    public string Description { get; }

    /// <summary>True = provider type uses OAuth (no API keys to manage).</summary>
    public bool IsOAuth { get; }

    /// <summary>Provider is included in config.yaml (not excluded).</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>OAuth providers: at least one active token detected in auth-dir.</summary>
    [ObservableProperty] private bool _connected;

    /// <summary>OAuth providers only — detected account identifier (email / username).</summary>
    [ObservableProperty] private string _account = "";

    /// <summary>Currently active model reported by the proxy.</summary>
    [ObservableProperty] private string _model = "";

    /// <summary>True while the OAuth login process is in flight for this provider.</summary>
    [ObservableProperty] private bool _isConnecting;

    /// <summary>Mini sparkline data (activity history).</summary>
    public ObservableCollection<double> Spark { get; } = new();

    /// <summary>Per-account slots — populated for custom (non-OAuth) providers.</summary>
    public ObservableCollection<ProviderAccountViewModel> Accounts { get; } = new();

    public int ActiveAccountCount => Accounts.Count(a => !a.IsDisabled);

    public ProviderViewModel(
        string id, string name,
        PackIconSimpleIconsKind iconKind, string logoColor,
        string description, bool isOAuth = false)
    {
        Id          = id;
        Name        = name;
        IconKind    = iconKind;
        LogoColor   = logoColor;
        Description = description;
        IsOAuth     = isOAuth;
    }

    partial void OnIsEnabledChanged(bool value) =>
        IsEnabledChanged?.Invoke(this, value);

    /// <summary>Raised when the user toggles the provider on/off. ViewModel layer should react by rewriting config.yaml.</summary>
    public event System.EventHandler<bool>? IsEnabledChanged;

    /// <summary>Raised when the user requests adding a new account to this provider.</summary>
    public event System.EventHandler? AddAccountRequested;

    [RelayCommand]
    private void RequestAddAccount() => AddAccountRequested?.Invoke(this, System.EventArgs.Empty);

    public void RefreshAccountCount() =>
        OnPropertyChanged(nameof(ActiveAccountCount));
}
