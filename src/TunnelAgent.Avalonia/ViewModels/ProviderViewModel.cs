using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.ViewModels;

// ── Quota bar (one per limit window: primary / weekly / monthly) ─────────────

public partial class QuotaBarViewModel : ViewModelBase
{
    /// <summary>Label shown above the bar, e.g. "Primary Limit (3h)".</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>Reset countdown text shown below the bar, e.g. "Resets in 5d 12h".</summary>
    [ObservableProperty] private string _resetIn = "";

    /// <summary>0–1 fraction consumed.</summary>
    [ObservableProperty] private double _used;

    /// <summary>Right-side label, e.g. "22% used".</summary>
    public string UsedLabel => $"{Used * 100:0}% used";

    partial void OnUsedChanged(double value) => OnPropertyChanged(nameof(UsedLabel));
}

// ── Account slot ─────────────────────────────────────────────────────────────

public partial class ProviderAccountViewModel : ViewModelBase
{
    public string ProviderId { get; }
    public string ApiKey     { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private bool   _isDisabled;

    public event System.EventHandler<bool>? IsDisabledChanged;
    partial void OnIsDisabledChanged(bool value) => IsDisabledChanged?.Invoke(this, value);

    /// <summary>Email or username shown in the expanded account row.</summary>
    [ObservableProperty] private string _email = "";

    /// <summary>Plan badge text, e.g. "PLUS", "PRO", "FREE". Empty = hide badge.</summary>
    [ObservableProperty] private string _planBadge = "";

    /// <summary>Quota progress bars; typically 0-2 entries.</summary>
    public ObservableCollection<QuotaBarViewModel> QuotaBars { get; }

    public bool HasQuota => QuotaBars.Count > 0;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isProviderEnabled = true;

    public ProviderAccountViewModel(string providerId, string apiKey, string label, bool isDisabled)
    {
        ProviderId  = providerId;
        ApiKey      = apiKey;
        _label      = label;
        _isDisabled = isDisabled;
        QuotaBars   = new ObservableCollection<QuotaBarViewModel>();
        QuotaBars.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuota));
    }

    public string MaskedKey => string.IsNullOrEmpty(ApiKey) ? "" :
        ApiKey.Length > 12 ? $"{ApiKey[..8]}...{ApiKey[^4..]}" : ApiKey;

    /// <summary>True when this is a custom API-key account (no email, shows masked key).</summary>
    public bool IsCustomKey => !string.IsNullOrEmpty(ApiKey);

    [ObservableProperty] private bool _maskEmails;
    partial void OnMaskEmailsChanged(bool value) => OnPropertyChanged(nameof(DisplayName));

    /// <summary>Display name: email if available, else label, else masked key.</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(Email) ? (MaskEmails ? MaskEmailAddress(Email) : Email) :
        !string.IsNullOrEmpty(Label) ? Label :
        MaskedKey;

    private static string MaskEmailAddress(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        var maskedLocal  = new string('•', at);
        var maskedDomain = new string('•', email.Length - at - 1);
        return $"{maskedLocal}@{maskedDomain}";
    }

}

// ── Provider row ─────────────────────────────────────────────────────────────

public partial class ProviderViewModel : ViewModelBase
{
    public string Id          { get; }
    public string Name        { get; }
    public PackIconSimpleIconsKind IconKind   { get; }
    public string? CustomIconData { get; }
    public bool HasCustomIcon => CustomIconData is not null;
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

    /// <summary>Whether the account/quota panel is expanded.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Whether this provider is selected in the quota page provider rail.</summary>
    [ObservableProperty] private bool _isQuotaSelected;

    /// <summary>Per-account slots — populated for custom (non-OAuth) providers
    /// and for OAuth providers with detected accounts.</summary>
    public ObservableCollection<ProviderAccountViewModel> Accounts { get; } = new();

    public int ActiveAccountCount => Accounts.Count(a => !a.IsDisabled);

    // ── Derived display helpers ───────────────────────────────────────────────

    /// <summary>Bottom sub-line when connected: "N connected account(s)".</summary>
    public string ConnectedSubText
    {
        get
        {
            var n = ActiveAccountCount > 0 ? ActiveAccountCount : (Connected ? 1 : 0);
            if (n == 0) return Description;
            return $"{n} connected account{(n == 1 ? "" : "s")}";
        }
    }

    /// <summary>Color of the sub-line status text: green when connected/active, muted otherwise.</summary>
    public string StatusColor =>
        !IsEnabled           ? "#CC7A2B" :   // orange = disabled
        Connected || ActiveAccountCount > 0 ? "#3CB371" :  // green = active
        "#888888";                            // muted grey

    /// <summary>True when the expand chevron should be visible (has accounts to show).</summary>
    public bool HasAccounts => Accounts.Count > 0 || (IsOAuth && Connected);

    partial void OnIsEnabledChanged(bool value)
    {
        IsEnabledChanged?.Invoke(this, value);
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ConnectedSubText));
        foreach (var a in Accounts)
            a.IsProviderEnabled = value;
    }

    partial void OnConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(ConnectedSubText));
        OnPropertyChanged(nameof(HasAccounts));
    }

    partial void OnAccountChanged(string value)
    {
        OnPropertyChanged(nameof(ConnectedSubText));
    }

    public ProviderViewModel(
        string id, string name,
        PackIconSimpleIconsKind iconKind, string logoColor,
        string description, bool isOAuth = false,
        string? customIconData = null)
    {
        Id             = id;
        Name           = name;
        IconKind       = iconKind;
        LogoColor      = logoColor;
        Description    = description;
        IsOAuth        = isOAuth;
        CustomIconData = customIconData;
    }

    /// <summary>Raised when the user toggles the provider on/off.</summary>
    public event System.EventHandler<bool>? IsEnabledChanged;

    /// <summary>Raised when the expand chevron is toggled.</summary>
    public event System.EventHandler<bool>? IsExpandedChanged;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !HasAccounts) { IsExpanded = false; return; }
        IsExpandedChanged?.Invoke(this, value);
    }

    /// <summary>Raised when the user requests adding a new account.</summary>
    public event System.EventHandler? AddAccountRequested;

    [RelayCommand]
    private void RequestAddAccount() => AddAccountRequested?.Invoke(this, System.EventArgs.Empty);

    [RelayCommand]
    private void ToggleExpand() { if (HasAccounts) IsExpanded = !IsExpanded; }

    public void RefreshAccountCount()
    {
        OnPropertyChanged(nameof(ActiveAccountCount));
        OnPropertyChanged(nameof(ConnectedSubText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(HasAccounts));
    }
}
