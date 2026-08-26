using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.ViewModels;

using TunnelAgent.Infrastructure.Engine.CliProxy;
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
        new("claude",         "Claude",         "Anthropic models via OAuth or API key."),
        new("codex",          "OpenAI",         "OpenAI models via OAuth or API key."),
        new("kimi",           "Kimi",           "Moonshot AI via OAuth."),
        new("antigravity",    "Antigravity",    "Antigravity AI via OAuth."),
        new("xai",            "xAI",            "Grok models via xAI OAuth."),
    ];

    private static readonly ProviderMeta[] BuiltinApiKeyProviders =
    [
        new("gemini-cli", "Gemini", "Google Gemini via API key."),
    ];

    private readonly record struct ProviderMeta(string Id, string Name, string Description);

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly SettingsService _settings;
    private readonly ConfigService _config;
    private readonly OAuthTokenDetector _oauthDetector;
    private readonly AuthFileWatcher _watcher;
    private readonly OAuthService _oauth;
    private readonly QuotaFetchService _quota;
    private readonly string _authDir;

    public List<ProviderViewModel> Providers { get; } = [];

    public event EventHandler? ProvidersRefreshed;
    /// <summary>Raised when the provider list itself changes (provider added/removed), so the UI rebinds.</summary>
    public event EventHandler? ProvidersRebuilt;
    /// <summary>Raised when a provider transitions from no accounts to having at least one.</summary>
    public event EventHandler<string>? ProviderFirstConnected;

    public ProviderCatalogService(SettingsService settings, ConfigService config)
        : this(settings, config, IPlatformInfo.Current.AuthDirectory) { }

    public ProviderCatalogService(SettingsService settings, ConfigService config, string authDir)
    {
        _settings      = settings;
        _config        = config;
        _authDir       = authDir;
        _oauthDetector = new OAuthTokenDetector(authDir);
        _watcher       = new AuthFileWatcher(authDir);
        _oauth         = new OAuthService(config);
        _quota         = new QuotaFetchService(authDir);

        _watcher.Changed += OnAuthDirChanged;
    }

    /// <summary>
    /// Build the initial provider list from settings + auth-dir state.
    /// Call once during app init.
    /// </summary>
    public async Task InitializeAsync()
    {
        var fromConfig = await _config.ReadProviderSettingsFromConfigAsync();
        foreach (var provider in fromConfig)
        {
            var existing = _settings.Current.Providers.FirstOrDefault(p => p.Id == provider.Id);
            if (existing is null)
            {
                _settings.Current.Providers.Add(provider);
                continue;
            }

            existing.Enabled = provider.Enabled;
            if (!string.IsNullOrWhiteSpace(provider.BaseUrl)) existing.BaseUrl = provider.BaseUrl;
            if (!string.IsNullOrWhiteSpace(provider.DisplayName)) existing.DisplayName = provider.DisplayName;
            existing.Kind = provider.Kind;
        }
        BuildProviderList();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Add an API-key account to a provider. Persists it in proxy-config.yaml
    /// (the credential store for upstream provider keys). Returns false if the
    /// account already exists.
    /// </summary>
    public async Task<bool> AddAccountAsync(
        string providerId, string baseUrl, string apiKey,
        string? displayName = null) =>
        await AddAccountAsync(providerId, baseUrl, apiKey, displayName, GetDefaultKind(providerId));

    public async Task<bool> AddAccountAsync(
        string providerId, string baseUrl, string apiKey,
        string? displayName, ProviderKind kind)
    {
        // Ensure provider settings entry exists
        var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId && p.Kind == kind);
        if (ps is null)
        {
            ps = new ProviderSettings { Id = providerId, Enabled = true, BaseUrl = baseUrl, DisplayName = displayName ?? "", Kind = kind };
            _settings.Current.Providers.Add(ps);
        }
        else if (!string.IsNullOrEmpty(baseUrl))
        {
            ps.BaseUrl = baseUrl;
        }

        var created = true;
        var existing = ps.Accounts.FirstOrDefault(a => a.ApiKey == apiKey);
        if (existing is not null)
        {
            created = false;
            existing.Disabled = false;
        }
        else
        {
            ps.Accounts.Add(new ProviderAccountSettings { ApiKey = apiKey });
        }
        _settings.Save();

        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
        _watcher.NotifyNow();

        return created;
    }

    /// <summary>Remove one API-key account from a provider and rewrite config.yaml.</summary>
    public async Task RemoveAccountAsync(string providerId, string apiKey)
    {
        foreach (var ps in _settings.Current.Providers.Where(p => p.Id == providerId))
            ps.Accounts.RemoveAll(a => a.ApiKey == apiKey);
        _settings.Save();
        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
        _watcher.NotifyNow();
    }

    /// <summary>
    /// Add a custom OpenAI-compatible provider (name + base-url + api key) to
    /// proxy-config.yaml under <c>openai-compatibility</c>, then rebuild the list.
    /// </summary>
    public async Task AddCustomProviderAsync(string name, string baseUrl, string apiKey, IReadOnlyList<string>? models = null)
    {
        var id = UniqueProviderId(name);
        var ps = new ProviderSettings
        {
            Id = id,
            Enabled = true,
            Kind = ProviderKind.OpenAICompatibility,
            BaseUrl = baseUrl,
            Accounts = [new ProviderAccountSettings { ApiKey = apiKey }],
            Models = models?.ToList() ?? []
        };
        _settings.Current.Providers.Add(ps);
        _settings.Save();
        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Update a custom OpenAI-compatible provider's name, base-url and api key,
    /// then rewrite config.yaml and rebuild the list.
    /// </summary>
    public async Task<string> UpdateCustomProviderAsync(string providerId, string name, string baseUrl, string apiKey)
    {
        var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId && p.Kind == ProviderKind.OpenAICompatibility);
        if (ps is null) return providerId;

        // The proxy's openai-compatibility schema has no display-name field; the
        // provider's `name:` (mapped to Id) is the only human-readable identifier,
        // so renaming updates the Id (kept unique against the other providers).
        ps.Id = UniqueProviderId(name, ps.Id);
        ps.BaseUrl = baseUrl;
        ps.Accounts = [new ProviderAccountSettings { ApiKey = apiKey }];
        _settings.Save();
        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
        return ps.Id;
    }

    /// <summary>Replace the exposed model list for a custom provider and rewrite config.yaml.</summary>
    public async Task UpdateCustomProviderModelsAsync(string providerId, IReadOnlyList<string> models)
    {
        var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId && p.Kind == ProviderKind.OpenAICompatibility);
        if (ps is null) return;

        ps.Models = models.ToList();
        _settings.Save();
        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove an entire custom provider (settings + config.yaml) and rebuild the list.</summary>
    public async Task RemoveCustomProviderAsync(string providerId)
    {
        _settings.Current.Providers.RemoveAll(p => p.Id == providerId && p.Kind == ProviderKind.OpenAICompatibility);
        _settings.Save();
        await _config.WriteConfigAsync();
        BuildProviderList();
        ProvidersRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private string UniqueProviderId(string name, string? excludeSelfId = null)
    {
        // The proxy uses `name` verbatim (it becomes owned_by in /v1/models and the UI label),
        // so keep the user's exact text instead of slugifying — only de-duplicate on collision.
        var baseName = name.Trim();
        if (string.IsNullOrEmpty(baseName)) baseName = "provider";
        var id = baseName;
        var n = 2;
        while (_settings.Current.Providers.Any(p => p.Id == id && !string.Equals(p.Id, excludeSelfId, StringComparison.Ordinal)))
            id = $"{baseName} {n++}";
        return id;
    }

    /// <summary>
    /// Starts the OAuth login flow for the given provider.
    /// Opens the browser; auth-dir watcher updates Connected state on completion.
    /// </summary>
    public Task<OAuthConnectResult> ConnectOAuthAsync(string providerId) =>
        _oauth.ConnectAsync(providerId);

    /// <summary>Latest token-file write time (UTC) for an OAuth provider, or null when none exist.</summary>
    public DateTime? LatestOAuthTokenWriteUtc(string providerId) =>
        _oauthDetector.GetLatestTokenWriteUtc(providerId);

    public Task RefreshAccountQuotaAsync(ProviderViewModel provider, ProviderAccountViewModel account) =>
        _quota.FetchAccountPublicAsync(provider.Id, account);

    /// <summary>
    /// Disconnects an OAuth provider by deleting its token files from the auth-dir.
    /// </summary>
    public void DisconnectOAuth(string providerId)
    {
        _oauth.CancelPreviousAuth();

        if (!OAuthTokenDetector.KnownProviders.TryGetValue(providerId, out var prefix)) return;
        foreach (var file in EnumerateOAuthCredentialFiles(_authDir, prefix))
            BackupAndDeleteCredentialFile(file, "disconnect-oauth");

        _watcher.NotifyNow();
    }

    /// <summary>Remove a single OAuth account by deleting its token file.</summary>
    public void RemoveOAuthAccount(string providerId, string email)
    {
        if (!OAuthTokenDetector.KnownProviders.TryGetValue(providerId, out var prefix)) return;
        foreach (var file in EnumerateOAuthCredentialFiles(_authDir, prefix, email))
            BackupAndDeleteCredentialFile(file, "remove-oauth-account");

        _watcher.NotifyNow();
    }

    /// <summary>Deletes all OAuth and custom credential files from the auth directory.</summary>
    public async Task ResetAllCredentialsAsync()
    {
        _oauth.CancelPreviousAuth();

        foreach (var ps in _settings.Current.Providers)
            ps.Accounts.Clear();

        _settings.Save();

        foreach (var file in EnumerateManagedCredentialFiles(_authDir))
            BackupAndDeleteCredentialFile(file, "reset-all-credentials");

        await _config.WriteConfigAsync();
        _watcher.NotifyNow();
    }


    private static IEnumerable<string> EnumerateManagedCredentialFiles(string authDir)
    {
        if (!Directory.Exists(authDir)) yield break;

        // Legacy upstream-key json store (now stored in proxy-config.yaml) — clean up if present.
        foreach (var file in Directory.GetFiles(authDir, "openai-compat-*.json"))
            yield return file;

        foreach (var prefix in OAuthTokenDetector.KnownProviders.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in EnumerateOAuthCredentialFiles(authDir, prefix))
                yield return file;
        }
    }

    private static IEnumerable<string> EnumerateOAuthCredentialFiles(string authDir, string prefix, string? email = null)
    {
        if (!Directory.Exists(authDir)) yield break;

        var pattern = email is null ? $"{prefix}-*.json" : $"{prefix}-{email}*.json";
        foreach (var file in Directory.GetFiles(authDir, pattern))
        {
            if (Path.GetFileName(file).StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return file;
        }
    }

    private static void BackupAndDeleteCredentialFile(string file, string reason)
    {
        try
        {
            // Backups must live outside auth-dir: CLIProxyAPI's own management UI scans
            // auth-dir for credential files and would otherwise list these backups as accounts.
            var backupDir = Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "credential-backups",
                DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupDir);

            var backupPath = Path.Combine(backupDir, Path.GetFileName(file));
            File.Copy(file, backupPath, overwrite: true);
            File.Delete(file);
            System.Diagnostics.Debug.WriteLine($"[ProviderCatalogService] Deleted auth file ({reason}): {file}; backup: {backupPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProviderCatalogService] Failed to delete auth file ({reason}): {file}; {ex.Message}");
        }
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

    /// <summary>
    /// One-time migration: imports upstream API keys from the legacy
    /// openai-compat-*.json files into proxy-config.yaml (the correct store),
    /// then deletes the json files. No-op when none exist.
    /// </summary>
    private void BuildProviderList()
    {
        Providers.Clear();

        var oauthAccounts    = _oauthDetector.GetAccounts();

        // 1. Built-in OAuth providers
        foreach (var meta in BuiltinOAuthProviders)
        {
            var ps       = _settings.Current.Providers.FirstOrDefault(p => p.Id == meta.Id && p.Kind == GetDefaultKind(meta.Id));
            var accounts        = oauthAccounts.TryGetValue(meta.Id, out var accs) ? accs : [];
            var keyRecords      = ApiKeyAccountsFor(meta.Id, GetDefaultKind(meta.Id));
            var hasAccts        = accounts.Count > 0;
            var hasActiveAccts  = accounts.Any(a => !a.IsDisabled) || keyRecords.Any(a => !a.Disabled);
            // Only respect saved enabled=true if there are actually active accounts
            var enabled  = hasActiveAccts && (ps?.Enabled ?? true);

            var icon = ProviderIconRegistry.Get(meta.Id);
            var vm = new ProviderViewModel(meta.Id, meta.Name, icon.IconKind, icon.LogoColor, meta.Description, isOAuth: true, customIconData: icon.CustomIconData, supportsApiKey: SupportsNativeApiKey(meta.Id))
            {
                IsEnabled = enabled,
                Connected = hasAccts,
                ApiKeyBaseUrl = ps?.BaseUrl ?? "",
            };

            SyncOAuthAccounts(vm, accounts);
            if (vm.SupportsApiKey) SyncCustomAccounts(vm, keyRecords);
            WireEvents(vm);
            Providers.Add(vm);
        }

        // 2. Built-in API-key-only providers
        foreach (var meta in BuiltinApiKeyProviders)
        {
            var kind = GetDefaultKind(meta.Id);
            var ps = _settings.Current.Providers.FirstOrDefault(p => p.Id == meta.Id && p.Kind == kind);
            var keyRecords = ApiKeyAccountsFor(meta.Id, kind);
            var hasActiveAccts = keyRecords.Any(a => !a.Disabled);
            var icon = ProviderIconRegistry.Get(meta.Id);
            var vm = new ProviderViewModel(meta.Id, meta.Name, icon.IconKind, icon.LogoColor, meta.Description, customIconData: icon.CustomIconData, supportsApiKey: true)
            {
                IsEnabled = hasActiveAccts && (ps?.Enabled ?? true),
                ApiKeyBaseUrl = ps?.BaseUrl ?? "",
            };

            SyncCustomAccounts(vm, keyRecords);
            WireEvents(vm);
            Providers.Add(vm);
        }

        // 3. Custom OpenAI-compatible providers from settings
        foreach (var ps in _settings.Current.Providers.Where(p => p.Kind == ProviderKind.OpenAICompatibility && !string.IsNullOrEmpty(p.BaseUrl)))
        {
            if (Providers.Any(p => p.Id == ps.Id)) continue; // skip if already added

            var vm = BuildCustomProviderViewModel(ps);
            WireEvents(vm);
            Providers.Add(vm);
        }
    }

    private List<ProviderAccountSettings> ApiKeyAccountsFor(string providerId, ProviderKind kind) =>
        _settings.Current.Providers.FirstOrDefault(p => p.Id == providerId && p.Kind == kind)?.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.ApiKey)).ToList() ?? [];

    private ProviderViewModel BuildCustomProviderViewModel(ProviderSettings ps)
    {
        var name = string.IsNullOrEmpty(ps.DisplayName) ? ps.Id : ps.DisplayName;
        var vm   = new ProviderViewModel(
            ps.Id, name,
            PackIconSimpleIconsKind.OpenAi, "#555555",
            $"Custom provider — {ps.BaseUrl}", isOAuth: false, supportsApiKey: true)
        {
            IsEnabled = ps.Enabled,
            ApiKeyBaseUrl = ps.BaseUrl,
            Models = ps.Models.ToList(),
            IsCustomProvider = true
        };

        SyncCustomAccounts(vm, ApiKeyAccountsFor(ps.Id, ProviderKind.OpenAICompatibility));
        return vm;
    }

    private void WireEvents(ProviderViewModel vm)
    {
        vm.IsEnabledChanged += async (_, enabled) =>
            await SetProviderEnabledAsync(vm.Id, enabled);
    }

    private void OnAuthDirChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var oauthAccounts = _oauthDetector.GetAccounts();

            // Update OAuth providers
            var newlyConnected = new List<string>();
            foreach (var vm in Providers.Where(p => p.SupportsOAuth))
            {
                var accounts     = oauthAccounts.TryGetValue(vm.Id, out var accs) ? accs : [];
                var keyRecords   = ApiKeyAccountsFor(vm.Id, GetDefaultKind(vm.Id));
                var hasAccts     = accounts.Count > 0;
                var hasAnyAccts  = hasAccts || keyRecords.Count > 0;
                var wasConnected = vm.Connected;
                vm.Connected     = hasAccts;
                // Auto-enable on first account added; auto-disable when last account removed
                if (!wasConnected && hasAccts) { vm.IsEnabled = true; newlyConnected.Add(vm.Id); }
                else if (!hasAnyAccts)          vm.IsEnabled = false;
                SyncOAuthAccounts(vm, accounts);
                if (vm.SupportsApiKey) SyncCustomAccounts(vm, keyRecords);
            }
            // Raise after SyncOAuthAccounts so vm.Accounts is already populated
            foreach (var id in newlyConnected)
                ProviderFirstConnected?.Invoke(this, id);

            // Update API-key-only provider account lists
            foreach (var vm in Providers.Where(p => !p.SupportsOAuth))
                SyncCustomAccounts(vm, ApiKeyAccountsFor(vm.Id, GetDefaultKind(vm.Id)));

            ProvidersRefreshed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SyncOAuthAccounts(ProviderViewModel vm, List<OAuthAccount> accounts)
    {
        // Remove stale (keyed by email)
        var toRemove = vm.Accounts
            .Where(a => !a.IsCustomKey && !accounts.Any(r => r.Email == a.Email))
            .ToList();
        foreach (var a in toRemove) vm.Accounts.Remove(a);

        // Add new
        foreach (var r in accounts.Where(r => !vm.Accounts.Any(a => a.Email == r.Email)))
        {
            var acct = new ProviderAccountViewModel(r.ProviderId, apiKey: "", label: r.Email, r.IsDisabled)
            {
                Email             = r.Email,
                PlanBadge         = r.Plan,
                IsProviderEnabled = vm.IsEnabled,
            };
            WireAccountDisable(acct, vm);
            vm.Accounts.Add(acct);
        }

        // Update disabled state — preserve PlanBadge if already enriched by QuotaFetchService
        foreach (var a in vm.Accounts.Where(a => !a.IsCustomKey))
        {
            var match = accounts.FirstOrDefault(r => r.Email == a.Email);
            if (match is not null)
            {
                a.IsDisabled = match.IsDisabled;
                if (string.IsNullOrEmpty(a.PlanBadge))
                    a.PlanBadge = match.Plan;
            }
        }

        vm.RefreshAccountCount();
    }

    private void SyncCustomAccounts(ProviderViewModel vm, List<ProviderAccountSettings> records)
    {
        // Remove stale
        var toRemove = vm.Accounts
            .Where(a => a.IsCustomKey && !records.Any(r => r.ApiKey == a.ApiKey))
            .ToList();
        foreach (var a in toRemove) vm.Accounts.Remove(a);

        // Add new
        foreach (var r in records.Where(r => !vm.Accounts.Any(a => a.ApiKey == r.ApiKey)))
        {
            var acct = new ProviderAccountViewModel(vm.Id, r.ApiKey, "", isDisabled: false)
            {
                IsProviderEnabled = vm.IsEnabled,
                ProviderBaseUrl = vm.ApiKeyBaseUrl,
            };
            vm.Accounts.Add(acct);
        }

        // API-key accounts cannot be disabled individually in CLIProxyAPI config.
        foreach (var a in vm.Accounts.Where(a => a.IsCustomKey))
            a.IsDisabled = false;

        vm.RefreshAccountCount();
    }

    private void WireAccountDisable(ProviderAccountViewModel acct, ProviderViewModel provider)
    {
        acct.IsDisabledChanged += async (_, disabled) =>
        {
            if (acct.IsCustomKey)
            {
                foreach (var ps in _settings.Current.Providers.Where(p => p.Id == acct.ProviderId))
                    foreach (var a in ps.Accounts.Where(a => a.ApiKey == acct.ApiKey))
                        a.Disabled = disabled;
                _settings.Save();
            }
            else
                _oauthDetector.SetDisabled(acct.ProviderId, acct.Email, disabled);

            if (disabled)
            {
                acct.QuotaBars.Clear();
            }
            else
            {
                await _quota.FetchAccountPublicAsync(provider.Id, acct);
            }

            provider.RefreshAccountCount();
            await _config.WriteConfigAsync();
        };
    }

    public static ProviderKind GetDefaultKind(string providerId) => providerId switch
    {
        "claude" => ProviderKind.ClaudeApiKey,
        "gemini-cli" => ProviderKind.GeminiApiKey,
        "codex" => ProviderKind.CodexApiKey,
        _ => ProviderKind.OpenAICompatibility
    };

    private static bool SupportsNativeApiKey(string providerId) =>
        GetDefaultKind(providerId) is ProviderKind.ClaudeApiKey or ProviderKind.GeminiApiKey or ProviderKind.CodexApiKey;

    public void Dispose()
    {
        _watcher.Dispose();
        _oauth.Dispose();
    }
}
