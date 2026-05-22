using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TunnelAgent.Infrastructure.Engine.Perplexity;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>UI-facing catalog for file-backed Perplexity session accounts.</summary>
public sealed class PerplexityAccountCatalogService
{
    private readonly AccountService _accounts;

    public event EventHandler? AccountsChanged;

    public PerplexityAccountCatalogService() : this(new AccountService()) { }

    public PerplexityAccountCatalogService(AccountService accounts) => _accounts = accounts;

    /// <summary>Migrate legacy accounts from settings.json into individual files on first run.</summary>
    public Task InitializeAsync(System.Collections.Generic.List<PerplexityAccountSettings> legacyAccounts)
    {
        _accounts.MigrateFromSettings(legacyAccounts);
        return Task.CompletedTask;
    }

    public IReadOnlyList<PerplexityAccountViewModel> List() =>
        _accounts.List()
            .Select(a => new PerplexityAccountViewModel(a.Id, a.Label, a.SessionToken, a.IsDefault))
            .ToList();

    public Task AddAsync(string? label, string sessionToken)
    {
        _accounts.Add(label ?? string.Empty, sessionToken);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string accountId)
    {
        var removed = _accounts.Remove(accountId);
        if (removed)
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(removed);
    }

    public Task<bool> SetDefaultAsync(string accountId)
    {
        var changed = _accounts.SetDefault(accountId);
        if (changed)
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(changed);
    }

    public Task<bool> UpdateLabelAsync(string accountId, string label)
    {
        var changed = _accounts.UpdateLabel(accountId, label);
        if (changed)
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(changed);
    }

    public void RemoveAll()
    {
        _accounts.RemoveAll();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }
}
