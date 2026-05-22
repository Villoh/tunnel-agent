using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>
/// File-based persistence for Perplexity WebUI session accounts.
/// Each account is stored as {id}.json inside the Perplexity accounts directory.
/// </summary>
public sealed class AccountService
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AccountService() : this(IPlatformInfo.Current.PerplexityAccountsDirectory) { }

    public AccountService(string directory) => _dir = directory;

    private void EnsureDir() => Directory.CreateDirectory(_dir);

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    public IReadOnlyList<PerplexityAccountSettings> List()
    {
        if (!Directory.Exists(_dir))
            return [];

        var accounts = new List<PerplexityAccountSettings>();
        foreach (var file in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var account = JsonSerializer.Deserialize<PerplexityAccountSettings>(json, JsonOptions);
                if (account is not null && !string.IsNullOrWhiteSpace(account.Id))
                    accounts.Add(account);
            }
            catch { /* skip corrupt files */ }
        }

        return accounts
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public PerplexityAccountSettings Add(string label, string sessionToken, bool makeDefault = false)
    {
        EnsureDir();

        var existing = List();
        var normalizedLabel = string.IsNullOrWhiteSpace(label)
            ? $"Perplexity {existing.Count + 1}"
            : label.Trim();

        var account = new PerplexityAccountSettings
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = normalizedLabel,
            SessionToken = sessionToken.Trim(),
            IsDefault = makeDefault || existing.Count == 0,
        };

        if (account.IsDefault)
            ClearDefault(existing);

        Save(account);
        return account;
    }

    public bool Remove(string accountId)
    {
        var path = FilePath(accountId);
        if (!File.Exists(path)) return false;

        var wasDefault = false;
        try
        {
            var json = File.ReadAllText(path);
            var account = JsonSerializer.Deserialize<PerplexityAccountSettings>(json, JsonOptions);
            wasDefault = account?.IsDefault ?? false;
        }
        catch { }

        // Backup then delete
        try
        {
            var backupDir = Path.Combine(_dir, ".backup", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupDir);
            File.Copy(path, Path.Combine(backupDir, Path.GetFileName(path)), overwrite: true);
            File.Delete(path);
        }
        catch { return false; }

        // If it was default, promote the next account
        if (wasDefault)
        {
            var remaining = List();
            if (remaining.Count > 0)
            {
                remaining[0].IsDefault = true;
                Save(remaining[0]);
            }
        }

        return true;
    }

    public bool SetDefault(string accountId)
    {
        var existing = List();
        var target = existing.FirstOrDefault(a => a.Id == accountId);
        if (target is null) return false;

        ClearDefault(existing);
        target.IsDefault = true;
        Save(target);
        return true;
    }

    public bool UpdateLabel(string accountId, string label)
    {
        var target = List().FirstOrDefault(a => a.Id == accountId);
        if (target is null) return false;

        var normalizedLabel = string.IsNullOrWhiteSpace(label) ? "Perplexity" : label.Trim();
        if (string.Equals(target.Label, normalizedLabel, StringComparison.Ordinal)) return false;

        target.Label = normalizedLabel;
        Save(target);
        return true;
    }

    public PerplexityAccountSettings? GetDefault()
    {
        var all = List();
        return all.FirstOrDefault(a => a.IsDefault && !a.Disabled)
            ?? all.FirstOrDefault(a => !a.Disabled);
    }

    public void RemoveAll()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var file in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var backupDir = Path.Combine(_dir, ".backup", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(backupDir);
                File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
                File.Delete(file);
            }
            catch { }
        }
    }

    private void Save(PerplexityAccountSettings account)
    {
        EnsureDir();
        var json = JsonSerializer.Serialize(account, JsonOptions);
        File.WriteAllText(FilePath(account.Id), json);
    }

    private void ClearDefault(IEnumerable<PerplexityAccountSettings> accounts)
    {
        foreach (var a in accounts.Where(a => a.IsDefault))
        {
            a.IsDefault = false;
            Save(a);
        }
    }

    /// <summary>
    /// Migrates existing accounts from AppSettings into individual files.
    /// Called once on startup to migrate legacy data.
    /// </summary>
    public void MigrateFromSettings(System.Collections.Generic.List<PerplexityAccountSettings> legacyAccounts)
    {
        if (legacyAccounts.Count == 0) return;

        EnsureDir();
        var existing = List().Select(a => a.Id).ToHashSet();

        foreach (var account in legacyAccounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id) || existing.Contains(account.Id)) continue;
            Save(account);
        }
    }
}
