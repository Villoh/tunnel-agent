using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace TunnelAgent.Services;

public sealed record ProviderCredentialRecord(
    string ProviderId,
    string ApiKey,
    string Label,
    string FilePath,
    bool IsDisabled);

public sealed class CustomProviderCredentialStore
{
    private const string AuthType = "openai-compat";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory;
    private readonly Lock _lock = new();

    public CustomProviderCredentialStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Saves a new credential. If an identical (providerId, apiKey) pair exists and is
    /// disabled, re-enables it. Returns (record, created:true) or (record, created:false).
    /// </summary>
    public (ProviderCredentialRecord Record, bool Created) Save(
        string providerId, string apiKey, string? label = null)
    {
        lock (_lock)
        {
            var existing = LoadAll()
                .Where(r => r.ProviderId == providerId && r.ApiKey == apiKey)
                .ToList();

            if (existing.Count > 0)
            {
                if (existing.Any(r => r.IsDisabled))
                    SetDisabled(providerId, apiKey, false);

                var refreshed = LoadAll().First(r => r.ProviderId == providerId && r.ApiKey == apiKey);
                return (refreshed, false);
            }

            var filename = $"openai-compat-{SanitizeFilename(providerId)}-{Guid.NewGuid().ToString("N")[..8]}.json";
            var filePath = Path.Combine(_directory, filename);

            var doc = new JsonObject
            {
                ["type"]     = AuthType,
                ["provider"] = providerId,
                ["api_key"]  = apiKey,
                ["label"]    = label ?? MaskApiKey(apiKey),
                ["created"]  = DateTime.UtcNow.ToString("O")
            };

            WriteSecure(filePath, doc);

            var record = new ProviderCredentialRecord(providerId, apiKey, doc["label"]!.GetValue<string>(), filePath, false);
            return (record, true);
        }
    }

    public void Delete(string providerId, string apiKey)
    {
        lock (_lock)
        {
            foreach (var r in LoadAll().Where(r => r.ProviderId == providerId && r.ApiKey == apiKey))
                File.Delete(r.FilePath);
        }
    }

    public void SetDisabled(string providerId, string apiKey, bool disabled)
    {
        lock (_lock)
        {
            foreach (var r in LoadAll().Where(r => r.ProviderId == providerId && r.ApiKey == apiKey))
                PatchField(r.FilePath, "disabled", disabled);
        }
    }

    public List<ProviderCredentialRecord> LoadAll()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_directory)) return [];

            return Directory
                .GetFiles(_directory, "openai-compat-*.json")
                .OrderBy(f => f)
                .Select(TryLoad)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();
        }
    }

    public List<ProviderCredentialRecord> LoadForProvider(string providerId) =>
        LoadAll().Where(r => r.ProviderId == providerId).ToList();

    // ── private helpers ──────────────────────────────────────────────────────

    private ProviderCredentialRecord? TryLoad(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var doc  = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException();

            if (doc["type"]?.GetValue<string>() != AuthType) return null;

            var providerId = doc["provider"]?.GetValue<string>() ?? "";
            var apiKey     = doc["api_key"]?.GetValue<string>()  ?? "";
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(apiKey)) return null;

            var label    = doc["label"]?.GetValue<string>() ?? MaskApiKey(apiKey);
            var disabled = doc["disabled"]?.GetValue<bool>() ?? false;

            return new ProviderCredentialRecord(providerId, apiKey, label, filePath, disabled);
        }
        catch { return null; }
    }

    private void PatchField(string filePath, string key, bool value)
    {
        var text = File.ReadAllText(filePath);
        var doc  = JsonNode.Parse(text)!.AsObject();
        if (value)
            doc[key] = value;
        else
            doc.Remove(key);
        WriteSecure(filePath, doc);
    }

    private static void WriteSecure(string filePath, JsonNode doc)
    {
        var json = doc.ToJsonString(JsonOpts);
        File.WriteAllText(filePath, json);

        // Best-effort chmod 600 on non-Windows
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* ignore */ }
        }
    }

    private static string SanitizeFilename(string value) =>
        Regex.Replace(value, @"[^A-Za-z0-9._\-]+", "-").Trim('-') is { Length: > 0 } s ? s : "provider";

    private static string MaskApiKey(string key) =>
        key.Length > 12 ? $"{key[..8]}...{key[^4..]}" : key;
}
