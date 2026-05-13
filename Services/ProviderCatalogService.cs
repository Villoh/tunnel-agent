using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Owns the authoritative list of ProviderViewModels, keeps them in sync with
/// auth-dir files (OAuth token detection + custom credential store), and
/// writes config.yaml when enabled state changes.
/// </summary>
public sealed class ProviderCatalogService : IDisposable
{
    // ── Built-in catalog ──────────────────────────────────────────────────────

    private static readonly ProviderMeta[] BuiltinOAuthProviders =
    [
        new("claude",         "Claude Code",    PackIconSimpleIconsKind.Claude,       "#D97757", "Anthropic models via OAuth."),
        new("codex",          "OpenAI Codex",   PackIconSimpleIconsKind.OpenAi,       "#23262E", "OpenAI Codex via ChatGPT plan."),
        new("gemini-cli",     "Gemini CLI",     PackIconSimpleIconsKind.GoogleGemini, "#4285F4", "Google Gemini via OAuth."),
        new("kimi",           "Kimi",           PackIconSimpleIconsKind.OpenAi,       "#1A73E8", "Moonshot AI via OAuth."),
        new("github-copilot", "GitHub Copilot", PackIconSimpleIconsKind.GitHub,       "#24292E", "GitHub Copilot via OAuth."),
        new("antigravity",    "Antigravity",    PackIconSimpleIconsKind.OpenAi,       "#7C3AED", "Antigravity AI via OAuth."),
        new("qwen",           "Qwen",           PackIconSimpleIconsKind.AlibabaCloud, "#FF6A00", "Alibaba Qwen via OAuth."),
    ];

    private readonly record struct ProviderMeta(
        string Id, string Name,
        PackIconSimpleIconsKind Icon, string Color, string Description);

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly SettingsService _settings;
    private readonly EngineConfigService _config;
    private readonly CustomProviderCredentialStore _store;
    private readonly OAuthTokenDetector _oauthDetector;
    private readonly AuthFileWatcher _watcher;
    private readonly OAuthService _oauth;
    private readonly QuotaFetchService _quota;

    public List<ProviderViewModel> Providers { get; } = [];

    public event EventHandler? ProvidersRefreshed;

    public ProviderCatalogService(SettingsService settings, EngineConfigService config)
    {
        _settings      = settings;
        _config        = config;
        _store         = new CustomProviderCredentialStore(IPlatformInfo.Current.AuthDirectory);
        _oauthDetector = new OAuthTokenDetector(IPlatformInfo.Current.AuthDirectory);
        _watcher       = new AuthFileWatcher(IPlatformInfo.Current.AuthDirectory);
        _oauth         = new OAuthService(config);
        _quota         = new QuotaFetchService(IPlatformInfo.Current.AuthDirectory);

        _watcher.Changed += OnAuthDirChanged;
    }

    /// <summary>
    /// Build the initial provider list from settings + auth-dir state.
    /// Call once during app init.
    /// </summary>
    public Task InitializeAsync()
    {
        BuildProviderList();
        return Task.CompletedTask;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Add an account to a custom provider. Creates the provider settings entry
    /// if not present, saves the credential file, rewrites config.yaml.
    /// Returns false if the account already exists.
    /// </summary>
    public async Task<bool> AddAccountAsync(
        string providerId, string baseUrl, string apiKey,
        string? label = null, string? displayName = null)
    {
        // Ensure provider settings entry exists
        var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId);
        if (ps is null)
        {
            ps = new ProviderSettings { Id = providerId, Enabled = true, BaseUrl = baseUrl, DisplayName = displayName ?? "" };
            _settings.Current.Providers.Add(ps);
        }
        else if (!string.IsNullOrEmpty(baseUrl))
        {
            ps.BaseUrl = baseUrl;
        }

        var (_, created) = _store.Save(providerId, apiKey, label);
        _settings.Save();

        await _config.WriteConfigAsync();
        _watcher.NotifyNow();

        return created;
    }

    /// <summary>Remove one account from a custom provider and rewrite config.yaml.</summary>
    public async Task RemoveAccountAsync(string providerId, string apiKey)
    {
        _store.Delete(providerId, apiKey);
        await _config.WriteConfigAsync();
        _watcher.NotifyNow();
    }

    /// <summary>
    /// Starts the OAuth login flow for the given provider.
    /// Opens the browser; auth-dir watcher updates Connected state on completion.
    /// </summary>
    public Task<(bool Success, string Message)> ConnectOAuthAsync(string providerId) =>
        _oauth.ConnectAsync(providerId);

    public Task RefreshAccountQuotaAsync(ProviderViewModel provider, ProviderAccountViewModel account) =>
        _quota.FetchAccountPublicAsync(provider.Id, account);

    /// <summary>
    /// Disconnects an OAuth provider by deleting its token files from the auth-dir.
    /// </summary>
    public void DisconnectOAuth(string providerId)
    {
        _oauth.CancelPreviousAuth();

        if (!OAuthTokenDetector.KnownProviders.TryGetValue(providerId, out var prefix)) return;
        if (!System.IO.Directory.Exists(IPlatformInfo.Current.AuthDirectory)) return;

        foreach (var file in System.IO.Directory.GetFiles(IPlatformInfo.Current.AuthDirectory, $"{prefix}-*.json"))
        {
            // Skip custom-provider credential files
            if (System.IO.Path.GetFileName(file).StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase))
                continue;
            try { System.IO.File.Delete(file); } catch { /* best-effort */ }
        }

        _watcher.NotifyNow();
    }

    /// <summary>Remove a single OAuth account by deleting its token file.</summary>
    public void RemoveOAuthAccount(string providerId, string email)
    {
        if (!OAuthTokenDetector.KnownProviders.TryGetValue(providerId, out var prefix)) return;
        if (!System.IO.Directory.Exists(IPlatformInfo.Current.AuthDirectory)) return;

        foreach (var file in System.IO.Directory.GetFiles(
            IPlatformInfo.Current.AuthDirectory, $"{prefix}-{email}*.json"))
        {
            if (System.IO.Path.GetFileName(file).StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase))
                continue;
            try { System.IO.File.Delete(file); } catch { }
        }

        _watcher.NotifyNow();
    }

    /// <summary>Toggle enabled/disabled for a provider and rewrite config.yaml.</summary>
    public async Task SetProviderEnabledAsync(string providerId, bool enabled)
    {
        var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId);
        if (ps is null)
        {
            ps = new ProviderSettings { Id = providerId, Enabled = enabled };
            _settings.Current.Providers.Add(ps);
        }
        else
        {
            ps.Enabled = enabled;
        }

        _settings.Save();
        await _config.WriteConfigAsync();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void BuildProviderList()
    {
        Providers.Clear();

        var oauthAccounts    = _oauthDetector.GetAccounts();
        var customCredentials = _store.LoadAll()
            .GroupBy(r => r.ProviderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 1. Built-in OAuth providers
        foreach (var meta in BuiltinOAuthProviders)
        {
            var ps       = _settings.Current.Providers.FirstOrDefault(p => p.Id == meta.Id);
            var accounts = oauthAccounts.TryGetValue(meta.Id, out var accs) ? accs : [];
            var hasAccts = accounts.Any(a => !a.IsDisabled);
            // Only respect saved enabled=true if there are actually accounts
            var enabled  = hasAccts && (ps?.Enabled ?? true);

            var vm = new ProviderViewModel(meta.Id, meta.Name, meta.Icon, meta.Color, meta.Description, isOAuth: true)
            {
                IsEnabled = enabled,
                Connected = hasAccts,
            };

            SyncOAuthAccounts(vm, accounts);
            WireEvents(vm);
            Providers.Add(vm);
        }

        // 2. Custom providers from settings
        foreach (var ps in _settings.Current.Providers.Where(p => !string.IsNullOrEmpty(p.BaseUrl)))
        {
            if (Providers.Any(p => p.Id == ps.Id)) continue; // skip if already added

            var vm = BuildCustomProviderViewModel(ps, customCredentials);
            WireEvents(vm);
            Providers.Add(vm);
        }

        // 3. Custom providers discovered from credential store (not yet in settings)
        foreach (var (providerId, records) in customCredentials)
        {
            if (Providers.Any(p => p.Id == providerId)) continue;

            var ps = new ProviderSettings { Id = providerId, Enabled = true };
            _settings.Current.Providers.Add(ps);

            var vm = new ProviderViewModel(
                providerId, TitleCase(providerId),
                PackIconSimpleIconsKind.OpenAi, "#555555",
                "Custom OpenAI-compatible provider.", isOAuth: false)
            {
                IsEnabled = true
            };

            foreach (var r in records)
                vm.Accounts.Add(new ProviderAccountViewModel(r.ProviderId, r.ApiKey, r.Label, r.IsDisabled));

            vm.RefreshAccountCount();
            WireEvents(vm);
            Providers.Add(vm);
        }
    }

    private ProviderViewModel BuildCustomProviderViewModel(
        ProviderSettings ps,
        Dictionary<string, List<ProviderCredentialRecord>> customCredentials)
    {
        var name = string.IsNullOrEmpty(ps.DisplayName) ? TitleCase(ps.Id) : ps.DisplayName;
        var vm   = new ProviderViewModel(
            ps.Id, name,
            PackIconSimpleIconsKind.OpenAi, "#555555",
            $"Custom provider — {ps.BaseUrl}", isOAuth: false)
        {
            IsEnabled = ps.Enabled
        };

        if (customCredentials.TryGetValue(ps.Id, out var records))
        {
            foreach (var r in records)
                vm.Accounts.Add(new ProviderAccountViewModel(r.ProviderId, r.ApiKey, r.Label, r.IsDisabled));
        }

        vm.RefreshAccountCount();
        return vm;
    }

    private void WireEvents(ProviderViewModel vm)
    {
        vm.IsEnabledChanged += async (_, enabled) =>
            await SetProviderEnabledAsync(vm.Id, enabled);

        // Fetch quota when the user expands a connected OAuth provider
        vm.IsExpandedChanged += async (_, isExpanded) =>
        {
            if (isExpanded && vm.IsOAuth && vm.Accounts.Count > 0)
                await _quota.FetchAndApplyAsync(vm);
        };
    }

    private void OnAuthDirChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var oauthAccounts = _oauthDetector.GetAccounts();
            var customCreds   = _store.LoadAll()
                .GroupBy(r => r.ProviderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Update OAuth providers
            foreach (var vm in Providers.Where(p => p.IsOAuth))
            {
                var accounts  = oauthAccounts.TryGetValue(vm.Id, out var accs) ? accs : [];
                var hasAccts  = accounts.Any(a => !a.IsDisabled);
                vm.Connected  = hasAccts;
                // Auto-disable toggle when last account is removed
                if (!hasAccts) vm.IsEnabled = false;
                SyncOAuthAccounts(vm, accounts);
            }

            // Update custom provider account lists
            foreach (var vm in Providers.Where(p => !p.IsOAuth))
            {
                var records = customCreds.TryGetValue(vm.Id, out var r) ? r : [];
                SyncCustomAccounts(vm, records);
            }

            ProvidersRefreshed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SyncOAuthAccounts(ProviderViewModel vm, List<OAuthAccount> accounts)
    {
        // Remove stale (keyed by email)
        var toRemove = vm.Accounts
            .Where(a => !accounts.Any(r => r.Email == a.Email))
            .ToList();
        foreach (var a in toRemove) vm.Accounts.Remove(a);

        // Add new
        foreach (var r in accounts.Where(r => !vm.Accounts.Any(a => a.Email == r.Email)))
        {
            var acct = new ProviderAccountViewModel(r.ProviderId, apiKey: "", label: r.Email, r.IsDisabled)
            {
                Email     = r.Email,
                PlanBadge = r.Plan,
            };
            WireAccountDisable(acct);
            vm.Accounts.Add(acct);
        }

        // Update disabled state
        foreach (var a in vm.Accounts)
        {
            var match = accounts.FirstOrDefault(r => r.Email == a.Email);
            if (match is not null)
            {
                a.IsDisabled = match.IsDisabled;
                a.PlanBadge  = match.Plan;
            }
        }

        vm.RefreshAccountCount();
    }

    private void SyncCustomAccounts(ProviderViewModel vm, List<ProviderCredentialRecord> records)
    {
        // Remove stale
        var toRemove = vm.Accounts
            .Where(a => !records.Any(r => r.ApiKey == a.ApiKey))
            .ToList();
        foreach (var a in toRemove) vm.Accounts.Remove(a);

        // Add new
        foreach (var r in records.Where(r => !vm.Accounts.Any(a => a.ApiKey == r.ApiKey)))
        {
            var acct = new ProviderAccountViewModel(r.ProviderId, r.ApiKey, r.Label, r.IsDisabled);
            WireAccountDisable(acct);
            vm.Accounts.Add(acct);
        }

        // Update disabled state
        foreach (var a in vm.Accounts)
        {
            var match = records.FirstOrDefault(r => r.ApiKey == a.ApiKey);
            if (match is not null) a.IsDisabled = match.IsDisabled;
        }

        vm.RefreshAccountCount();
    }

    private void WireAccountDisable(ProviderAccountViewModel acct)
    {
        acct.IsDisabledChanged += (_, disabled) =>
        {
            if (acct.IsCustomKey)
                _store.SetDisabled(acct.ProviderId, acct.ApiKey, disabled);
            else
                _oauthDetector.SetDisabled(acct.ProviderId, acct.Email, disabled);
        };
    }

    private static string TitleCase(string id) =>
        System.Text.RegularExpressions.Regex.Replace(id, @"[^A-Za-z0-9]+", " ") is { } s
            ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s)
            : id;

    public void Dispose()
    {
        _watcher.Dispose();
        _oauth.Dispose();
    }
}
