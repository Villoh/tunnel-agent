namespace TunnelAgent.ViewModels;

public sealed class PerplexityAccountViewModel : ViewModelBase
{
    public string Id { get; }
    public string SessionToken { get; }
    public string Label { get; }
    public bool IsDefault { get; }

    public PerplexityAccountViewModel(string id, string label, string sessionToken, bool isDefault)
    {
        Id = id;
        Label = string.IsNullOrWhiteSpace(label) ? "Perplexity" : label;
        SessionToken = sessionToken;
        IsDefault = isDefault;
    }

    public string MaskedSessionToken =>
        string.IsNullOrWhiteSpace(SessionToken)
            ? string.Empty
            : SessionToken.Length > 12
                ? $"{SessionToken[..8]}...{SessionToken[^4..]}"
                : SessionToken;
}
