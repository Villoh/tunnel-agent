using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Svg.Skia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

// ── Quota bar (one per limit window: primary / weekly / monthly) ─────────────

public partial class QuotaBarViewModel : ViewModelBase
{
    /// <summary>Label shown above the bar, e.g. "Primary Limit (3h)".</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>Reset countdown text shown below the bar, e.g. "Resets in 5d 12h".</summary>
    private string _resetIn = "";
    public string ResetIn
    {
        get => LocalizeResetIn(_resetIn);
        set => SetProperty(ref _resetIn, value);
    }

    /// <summary>0–1 fraction consumed.</summary>
    [ObservableProperty] private double _used;

    /// <summary>Right-side label, e.g. "22% used".</summary>
    public string UsedLabel => $"{Used * 100:0}% used";

    public QuotaBarViewModel()
    {
        LocalizationService.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ResetIn));
    }

    partial void OnUsedChanged(double value) => OnPropertyChanged(nameof(UsedLabel));

    private static string LocalizeResetIn(string value)
    {
        if (!value.StartsWith("loc:", StringComparison.Ordinal))
            return value;

        var parts = value[4..].Split('|');
        if (parts.Length == 0 || string.IsNullOrEmpty(parts[0]))
            return "";

        var args = parts.Skip(1).Select(p => int.TryParse(p, out var n) ? (object)n : p).ToArray();
        return LocalizationService.Instance.GetString(parts[0], args);
    }
}

// ── Account slot ─────────────────────────────────────────────────────────────

public partial class ProviderAccountViewModel : ViewModelBase
{
    public string ProviderId { get; }
    public string ApiKey     { get; }

    [ObservableProperty] private string _label;
    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DetailText));
    }

    [ObservableProperty] private bool   _isDisabled;
    [ObservableProperty] private string _providerBaseUrl = "";

    public event System.EventHandler<bool>? IsDisabledChanged;
    partial void OnIsDisabledChanged(bool value) => IsDisabledChanged?.Invoke(this, value);

    /// <summary>Email or username shown in the expanded account row.</summary>
    [ObservableProperty] private string _email = "";

    /// <summary>Plan badge text, e.g. "PLUS", "PRO", "FREE". Empty = hide badge.</summary>
    [ObservableProperty] private string _planBadge = "";

    /// <summary>Quota progress bars; typically 0-2 entries.</summary>
    public ObservableCollection<QuotaBarViewModel> QuotaBars { get; }

    public bool HasQuota => QuotaBars.Count > 0;

    /// <summary>Set to true after a successful quota fetch that returned no bars (e.g. no active plan).</summary>
    [ObservableProperty] private bool _quotaFetchedEmpty;
    partial void OnQuotaFetchedEmptyChanged(bool value) { OnPropertyChanged(nameof(QuotaEmptyLabel)); OnPropertyChanged(nameof(QuotaEmptyDescription)); }

    public string QuotaEmptyLabel => QuotaFetchedEmpty ? "No quota data available" : "Quota not loaded";
    public string QuotaEmptyDescription => QuotaFetchedEmpty
        ? "No active plan or no usage data returned by the provider."
        : "Refresh this account to fetch available quota windows.";

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
        ApiKey.Length > 8 ? $"{ApiKey[..4]}...{ApiKey[^4..]}" : ApiKey;

    public string DetailText => IsCustomKey && !string.IsNullOrWhiteSpace(Label) ? MaskedKey : "";

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
        if (at > 0)
        {
            var maskedLocal  = new string('•', at);
            var maskedDomain = new string('•', email.Length - at - 1);
            return $"{maskedLocal}@{maskedDomain}";
        }
        // Non-email identifier (e.g. userId): show first 4 chars then bullets
        var keep = Math.Min(4, email.Length);
        return email[..keep] + new string('•', Math.Max(0, email.Length - keep));
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
    public string ApiKeyBaseUrl { get; set; } = "";
    private readonly string _descriptionFallback;
    public string Description => GetLocalizedDescription();

    // ── Brand SVG icon (Assets/Providers) — same approach as AgentViewModel ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSvgIcon))]
    private SvgImage? _svgIcon;
    public bool HasSvgIcon => SvgIcon is not null;
    public string Initials => Name.Length >= 2 ? Name[..2] : Name;

    /// <summary>Maps a provider id to its bundled brand SVG and whether the glyph uses currentColor (needs theme fill).</summary>
    private static (string Asset, bool ThemeFill)? ResolveProviderSvg(string id) => id.ToLowerInvariant() switch
    {
        "claude"                 => ("/Assets/providers/claude.svg",      false),
        "codex"                  => ("/Assets/providers/openai.svg",      true),
        "gemini-cli"             => ("/Assets/providers/gemini.svg",      false),
        "antigravity"            => ("/Assets/providers/antigravity.svg", false),
        "xai" or "grok"          => ("/Assets/providers/xai.svg",         true),
        "cursor"                 => ("/Assets/providers/cursor.svg",      true),
        "kiro"                   => ("/Assets/providers/kiro.svg",        false),
        "trae"                   => ("/Assets/providers/trae.svg",        false),
        _                        => null,
    };

    private bool _svgThemeFill;

    private void InitProviderSvg()
    {
        if (ResolveProviderSvg(Id) is not { } map) return;
        _svgThemeFill = map.ThemeFill;
        _ = LoadSvgIconAsync(map.Asset);
        if (_svgThemeFill && Avalonia.Application.Current is not null)
            Avalonia.Application.Current.ActualThemeVariantChanged += (_, _) => _ = LoadSvgIconAsync(map.Asset);
    }

    private async Task LoadSvgIconAsync(string path)
    {
        var normalized = Converters.SvgImageConverter.NormalizeAssetPath(path);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            SvgSource? source;
            if (_svgThemeFill)
            {
                // Monochrome brand glyphs must follow the active theme. Setting SvgImage.Css
                // throws when the icon is built during early startup (the styling system isn't
                // ready yet), leaving the icon null — so we bake the theme colour straight into
                // the SVG markup, which is plain parsing and works at any point.
                var raw = ReadAssetText(normalized);
                source = raw is null ? null : SvgSource.LoadFromSvg(Recolor(raw, IsDarkTheme() ? "#ffffff" : "#000000"));
            }
            else
            {
                source = SvgSource.Load(normalized, null);
            }
            SvgIcon = source is null ? null : new SvgImage { Source = source };
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static string? ReadAssetText(string uri)
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Bakes an explicit fill colour into a monochrome brand glyph (replaces an explicit black fill).</summary>
    private static string Recolor(string svg, string color) =>
        svg.Replace("fill=\"#000000\"", $"fill=\"{color}\"").Replace("fill=\"#000\"", $"fill=\"{color}\"");

    private static bool IsDarkTheme() =>
        Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark
        || Avalonia.Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

    /// <summary>True = provider supports OAuth login.</summary>
    public bool SupportsOAuth { get; }

    /// <summary>True = provider supports upstream API-key accounts.</summary>
    public bool SupportsApiKey { get; }

    /// <summary>Back-compat display helper: provider supports OAuth.</summary>
    public bool IsOAuth => SupportsOAuth;

    public bool HasOnlyOAuth => SupportsOAuth && !SupportsApiKey;
    public bool HasSingleAddMode => SupportsOAuth != SupportsApiKey;
    public bool HasMultipleAddModes => SupportsOAuth && SupportsApiKey;
    /// <summary>Custom OpenAI-compatible provider added by the user (not a built-in API-key provider).</summary>
    public bool IsCustomProvider { get; init; }

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

    /// <summary>Upstream models exposed for custom OpenAI-compatible providers (mirrors proxy-config.yaml).</summary>
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>True while this custom provider's models are being fetched from its upstream <c>/models</c> endpoint.</summary>
    [ObservableProperty] private bool _isFetchingModels;

    public int ActiveAccountCount => Accounts.Count(a => !a.IsDisabled);

    // ── Derived display helpers ───────────────────────────────────────────────

    /// <summary>Bottom sub-line when connected: "N connected account(s)" or "N API Key(s) added".</summary>
    public string ConnectedSubText
    {
        get
        {
            var accountCount = Accounts.Count(a => !a.IsCustomKey && !a.IsDisabled);
            var keyCount = Accounts.Count(a => a.IsCustomKey && !a.IsDisabled);

            if (accountCount == 0 && keyCount == 0)
            {
                if (!Connected) return Description;
                accountCount = 1;
            }

            if (accountCount > 0 && keyCount > 0)
                return $"{AccountCountText(accountCount)} / {ApiKeyCountText(keyCount)}";
            if (keyCount > 0)
                return ApiKeyCountText(keyCount);
            return AccountCountText(accountCount);
        }
    }

    private static string AccountCountText(int count) =>
        LocalizationService.Instance.GetString(
            count == 1 ? "ProvidersView_Provider_ConnectedAccountSingular" : "ProvidersView_Provider_ConnectedAccountPlural",
            count);

    private static string ApiKeyCountText(int count) =>
        LocalizationService.Instance.GetString(
            count == 1 ? "ProvidersView_Provider_ApiKeySingular" : "ProvidersView_Provider_ApiKeyPlural",
            count);

    public string ProviderDetailText => IsCustomProvider ? ApiKeyBaseUrl : "";

    /// <summary>Color of the sub-line status text: green when connected/active, muted otherwise.</summary>
    public string StatusColor =>
        !IsEnabled           ? "#CC7A2B" :   // orange = disabled
        Connected || ActiveAccountCount > 0 ? "#3CB371" :  // green = active
        "#888888";                            // muted grey

    /// <summary>True when the expand chevron should be visible (has accounts to show).</summary>
    public bool HasAccounts => Accounts.Count > 0 || (SupportsOAuth && Connected);

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
        string? customIconData = null,
        bool supportsApiKey = false)
    {
        Id             = id;
        Name           = name;
        IconKind       = iconKind;
        LogoColor      = logoColor;
        _descriptionFallback = description;
        SupportsOAuth  = isOAuth;
        SupportsApiKey = supportsApiKey;
        CustomIconData = customIconData;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        InitProviderSvg();
    }

    private string GetLocalizedDescription()
    {
        var key = Id switch
        {
            "cliproxyapi" => "Provider_cliproxyapi_Description",
            "perplexity-webui-scraper" => "Provider_perplexity-webui-scraper_Description",
            _ => null,
        };

        if (key is null)
            return _descriptionFallback;

        var text = LocalizationService.Instance.GetString(key);
        return text == $"[{key}]" ? _descriptionFallback : text;
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ConnectedSubText));
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
