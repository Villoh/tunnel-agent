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
        new("claude",         "Claude Code",    "Anthropic models via OAuth."),
        new("codex",          "OpenAI Codex",   "OpenAI Codex via ChatGPT plan."),
        new("gemini-cli",     "Gemini CLI",     "Google Gemini via OAuth."),
        new("kimi",           "Kimi",           "Moonshot AI via OAuth."),
        new("github-copilot", "GitHub Copilot", "GitHub Copilot via OAuth."),
        new("antigravity",    "Antigravity",    "Antigravity AI via OAuth."),
        new("qwen",           "Qwen",           "Alibaba Qwen via OAuth."),
    ];

    private readonly record struct ProviderMeta(string Id, string Name, string Description);

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly SettingsService _settings;
    private readonly ConfigService _config;
    private readonly CustomProviderCredentialStore _store;
    private readonly OAuthTokenDetector _oauthDetector;
    private readonly AuthFileWatcher _watcher;
    private readonly OAuthService _oauth;
    private readonly QuotaFetchService _quota;
    private readonly string _authDir;

    public List<ProviderViewModel> Providers { get; } = [];

    public event EventHandler? ProvidersRefreshed;

    public ProviderCatalogService(SettingsService settings, ConfigService config)
        : this(settings, config, IPlatformInfo.Current.AuthDirectory) { }

    public ProviderCatalogService(SettingsService settings, ConfigService config, string authDir)
    {
        _settings      = settings;
        _config        = config;
        _authDir       = authDir;
        _store         = new CustomProviderCredentialStore(authDir);
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
            var backupDir = Path.Combine(Path.GetDirectoryName(file)!, ".tunnelagent-backup",
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
            var accounts        = oauthAccounts.TryGetValue(meta.Id, out var accs) ? accs : [];
            var hasAccts        = accounts.Count > 0;
            var hasActiveAccts  = accounts.Any(a => !a.IsDisabled);
            // Only respect saved enabled=true if there are actually active accounts
            var enabled  = hasActiveAccts && (ps?.Enabled ?? true);

            var icon = ProviderIconRegistry.Get(meta.Id);
            var vm = new ProviderViewModel(meta.Id, meta.Name, icon.IconKind, icon.LogoColor, meta.Description, isOAuth: true, customIconData: icon.CustomIconData)
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
                var hasAccts  = accounts.Count > 0;
                vm.Connected  = hasAccts;
                // Auto-disable toggle only when all accounts are actually removed, not just disabled
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
                Email             = r.Email,
                PlanBadge         = r.Plan,
                IsProviderEnabled = vm.IsEnabled,
            };
            WireAccountDisable(acct, vm);
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
            var acct = new ProviderAccountViewModel(r.ProviderId, r.ApiKey, r.Label, r.IsDisabled)
            {
                IsProviderEnabled = vm.IsEnabled,
            };
            WireAccountDisable(acct, vm);
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

    private void WireAccountDisable(ProviderAccountViewModel acct, ProviderViewModel provider)
    {
        acct.IsDisabledChanged += async (_, disabled) =>
        {
            if (acct.IsCustomKey)
                _store.SetDisabled(acct.ProviderId, acct.ApiKey, disabled);
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
