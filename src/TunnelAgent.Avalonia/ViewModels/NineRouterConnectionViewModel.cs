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
    /// <param name="lastError">Last connection error, if any.</param>
    public NineRouterConnectionViewModel(string id, string providerId, string name, bool isActive, string? lastError)
    {
        Id = id;
        ProviderId = providerId;
        Name = string.IsNullOrWhiteSpace(name) ? providerId : name;
        IsActive = isActive;
        LastError = lastError;
    }

    /// <summary>Gets the connection id.</summary>
    public string Id { get; }

    /// <summary>Gets the 9Router provider id.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets whether the connection is enabled.</summary>
    public bool IsActive { get; }

    /// <summary>Gets the last error text, if 9Router reported one.</summary>
    public string? LastError { get; }

    /// <summary>Gets whether <see cref="LastError"/> should be shown.</summary>
    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);
}
