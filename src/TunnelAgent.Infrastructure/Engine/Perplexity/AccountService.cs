using System;
using System.Collections.Generic;
using System.Linq;

using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>Internal persistence API for Perplexity bearer accounts.</summary>
public sealed class AccountService
{
    private readonly SettingsService _settings;

    public AccountService(SettingsService settings) => _settings = settings;

    public IReadOnlyList<PerplexityAccountSettings> List() =>
        _settings.Current.PerplexityAccounts
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public PerplexityAccountSettings Add(string label, string sessionToken, bool makeDefault = false)
    {
        var normalizedLabel = string.IsNullOrWhiteSpace(label)
            ? $"Perplexity {_settings.Current.PerplexityAccounts.Count + 1}"
            : label.Trim();

        var account = new PerplexityAccountSettings
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = normalizedLabel,
            SessionToken = sessionToken.Trim(),
            IsDefault = makeDefault || _settings.Current.PerplexityAccounts.Count == 0
        };

        if (account.IsDefault)
            ClearDefault();

        _settings.Current.PerplexityAccounts.Add(account);
        _settings.Save();
        return account;
    }

    public bool Remove(string accountId)
    {
        var account = _settings.Current.PerplexityAccounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return false;

        var wasDefault = account.IsDefault;
        _settings.Current.PerplexityAccounts.Remove(account);
        if (wasDefault && _settings.Current.PerplexityAccounts.Count > 0)
            _settings.Current.PerplexityAccounts[0].IsDefault = true;

        _settings.Save();
        return true;
    }

    public bool SetDefault(string accountId)
    {
        var account = _settings.Current.PerplexityAccounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return false;

        ClearDefault();
        account.IsDefault = true;
        _settings.Save();
        return true;
    }

    public PerplexityAccountSettings? GetDefault() =>
        _settings.Current.PerplexityAccounts.FirstOrDefault(a => a.IsDefault && !a.Disabled)
        ?? _settings.Current.PerplexityAccounts.FirstOrDefault(a => !a.Disabled);

    private void ClearDefault()
    {
        foreach (var account in _settings.Current.PerplexityAccounts)
            account.IsDefault = false;
    }
}
