using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

/// <summary>One 9Router provider and its configured connections.</summary>
public sealed partial class NineRouterProviderViewModel : ObservableObject
{
    /// <summary>Initializes a provider row from its catalog entry.</summary>
    /// <param name="option">The provider metadata from the 9Router registry.</param>
    public NineRouterProviderViewModel(NineRouterProviderOption option, bool isRoundRobin = false)
    {
        Option = option;
        IsRoundRobin = isRoundRobin;
        var icon = ProviderIconRegistry.GetDisplay(option.Id, option.Name);
        IconKind = icon.IconKind;
        LogoColor = icon.LogoColor;
        CustomIconData = icon.CustomIconData;
        UseMonogram = icon.UseMonogram;
        Monogram = icon.Monogram;
    }

    /// <summary>Gets the provider metadata.</summary>
    public NineRouterProviderOption Option { get; }

    /// <summary>Gets the 9Router provider identifier.</summary>
    public string Id => Option.Id;

    /// <summary>Gets the provider display name.</summary>
    public string Name => Option.Name;

    /// <summary>Gets the configured connections for this provider.</summary>
    public ObservableCollection<NineRouterConnectionViewModel> Accounts { get; } = [];

    /// <summary>Gets whether the provider has at least one connection.</summary>
    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Gets whether at least one connection is active.</summary>
    public bool IsEnabled => Accounts.Any(account => account.IsActive);

    /// <summary>Gets the connection count shown below the provider name.</summary>
    public string ConnectedSubText => Accounts.Count switch
    {
        0 => "No connections",
        1 => "1 connection",
        _ => $"{Accounts.Count} connections",
    };

    /// <summary>Gets whether this provider accepts an API key or browser cookie.</summary>
    public bool SupportsStoredCredential => Option.SupportsApiKey || Option.SupportsCookie;

    /// <summary>Gets whether this provider supports only OAuth.</summary>
    public bool HasOnlyOAuth => Option.SupportsOAuth && !Option.SupportsApiKey && !Option.SupportsCookie && !Option.SupportsNoAuth;

    /// <summary>Gets whether this provider has more than one available credential mode.</summary>
    public bool HasMultipleAddModes =>
        (Option.SupportsOAuth ? 1 : 0)
        + (Option.SupportsApiKey || Option.SupportsCookie ? 1 : 0)
        + (Option.SupportsNoAuth ? 1 : 0) > 1;

    internal bool MatchesAuthFilter(string filter) => filter switch
    {
        "OAuth" => Option.SupportsOAuth,
        "API Key" => SupportsStoredCredential,
        _ => Option.SupportsOAuth || SupportsStoredCredential,
    };

    /// <summary>Gets the icon glyph.</summary>
    public PackIconSimpleIconsKind IconKind { get; }

    /// <summary>Gets the icon background color.</summary>
    public string LogoColor { get; }

    /// <summary>Gets custom SVG path data, when required.</summary>
    public string? CustomIconData { get; }

    /// <summary>Gets whether the icon is a monogram fallback.</summary>
    public bool UseMonogram { get; }

    /// <summary>Gets the fallback icon character.</summary>
    public string Monogram { get; }

    /// <summary>Gets whether a custom SVG icon is available.</summary>
    public bool HasCustomIcon => CustomIconData is not null;

    /// <summary>Gets whether a Simple Icons glyph is available.</summary>
    public bool ShowSimpleIcon => !UseMonogram && !HasCustomIcon;

    /// <summary>Gets or sets whether 9Router rotates this provider's accounts.</summary>
    [ObservableProperty] private bool _isRoundRobin;

    /// <summary>Gets or sets whether the provider's connection list is expanded.</summary>
    [ObservableProperty] private bool _isExpanded;
}
