using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

/// <summary>
/// One 9Router provider connection shown on the Providers tab.
/// </summary>
public sealed class NineRouterConnectionViewModel
{
    /// <summary>Creates a row from a management-API connection.</summary>
    /// <param name="id">Connection id used for update/delete.</param>
    /// <param name="providerId">9Router provider id (for example <c>openai</c>).</param>
    /// <param name="name">Dashboard display name.</param>
    /// <param name="isActive">Whether the connection is enabled (<c>isActive</c>).</param>
    /// <param name="authType">The credential mode reported by 9Router.</param>
    /// <param name="lastError">Last connection error, if any.</param>
    public NineRouterConnectionViewModel(
        string id,
        string providerId,
        string name,
        bool isActive,
        string? authType,
        string? lastError)
    {
        Id = id;
        ProviderId = providerId;
        Name = string.IsNullOrWhiteSpace(name) ? providerId : name;
        IsActive = isActive;
        AuthType = authType;
        LastError = lastError;
        var icon = ProviderIconRegistry.GetDisplay(providerId, Name);
        IconKind = icon.IconKind;
        LogoColor = icon.LogoColor;
        CustomIconData = icon.CustomIconData;
        UseMonogram = icon.UseMonogram;
        Monogram = icon.Monogram;
    }

    /// <summary>Gets the connection id.</summary>
    public string Id { get; }

    /// <summary>Gets the 9Router provider id.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets whether the connection is enabled.</summary>
    public bool IsActive { get; }

    /// <summary>Gets the credential mode reported by 9Router.</summary>
    public string? AuthType { get; }

    /// <summary>Gets the icon glyph.</summary>
    public PackIconSimpleIconsKind IconKind { get; }

    /// <summary>Gets the icon background color.</summary>
    public string LogoColor { get; }

    /// <summary>Gets custom SVG path data, when the icon needs it.</summary>
    public string? CustomIconData { get; }

    /// <summary>Gets whether the connection uses a letter icon.</summary>
    public bool UseMonogram { get; }

    /// <summary>Gets the fallback letter icon.</summary>
    public string Monogram { get; }

    /// <summary>Gets whether a custom SVG icon is available.</summary>
    public bool HasCustomIcon => CustomIconData is not null;

    /// <summary>Gets whether a Simple Icons glyph is available.</summary>
    public bool ShowSimpleIcon => !UseMonogram && !HasCustomIcon;

    /// <summary>Gets the last error text, if 9Router reported one.</summary>
    public string? LastError { get; }

    /// <summary>Gets whether <see cref="LastError"/> should be shown.</summary>
    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);

    /// <summary>Gets whether <see cref="AuthType"/> should be shown.</summary>
    public bool HasAuthType => !string.IsNullOrWhiteSpace(AuthType);
}
