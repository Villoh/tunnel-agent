using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TunnelAgent.Services;

public sealed record QuotaScanResult(
    QuotaProviderInfo Cursor,
    QuotaProviderInfo Kiro,
    QuotaProviderInfo Trae);

public sealed record QuotaProviderInfo(
    bool IsDetected,
    string Email,
    string PlanType,
    string? AccessToken,
    string? RefreshToken,
    string? ExpiresAt,
    string? ClientId,
    string? ClientSecret,
    string? AuthMethod,
    string? ProfileArn,
    string  Region,
    string? ApiHost);

/// <summary>
/// Scans the local file system for Kiro (Amazon) and Trae (ByteDance) auth files.
/// </summary>
public sealed class QuotaProviderService
{
    private static readonly QuotaProviderInfo NotDetected =
        new(false, "", "", null, null, null, null, null, null, null, "us-east-1", null);

    public Task<QuotaScanResult> ScanAsync() =>
        Task.FromResult(new QuotaScanResult(ScanCursor(), ScanKiro(), ScanTrae()));

    private static QuotaProviderInfo ScanCursor()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath  = Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(dbPath)) return NotDetected;

            // Copy to temp so we don't lock Cursor's live DB
            // Use immutable=1 to avoid WAL file requirement when Cursor is not running
            string? accessToken = null, refreshToken = null, email = null, planType = null;
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Immutable=True");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM ItemTable WHERE key LIKE 'cursorAuth/%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                switch (reader.GetString(0))
                {
                    case "cursorAuth/accessToken":          accessToken  = reader.GetString(1); break;
                    case "cursorAuth/refreshToken":         refreshToken = reader.GetString(1); break;
                    case "cursorAuth/cachedEmail":          email        = reader.GetString(1); break;
                    case "cursorAuth/stripeMembershipType": planType     = reader.GetString(1); break;
                }
            }

            if (string.IsNullOrEmpty(accessToken)) return NotDetected;

            return new QuotaProviderInfo(
                IsDetected:   true,
                Email:        email        ?? "",
                PlanType:     planType     ?? "",
                AccessToken:  accessToken,
                RefreshToken: refreshToken,
                ExpiresAt:    null,
                ClientId:     "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB",
                ClientSecret: null,
                AuthMethod:   null,
                ProfileArn:   null,
                Region:       "",
                ApiHost:      "https://api2.cursor.sh");
        }
        catch
        {
            return NotDetected;
        }
    }

    private static QuotaProviderInfo ScanKiro()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(userProfile, ".aws", "sso", "cache", "kiro-auth-token.json");
            if (!File.Exists(path)) return NotDetected;

            var json = File.ReadAllText(path);
            var doc  = JsonNode.Parse(json)?.AsObject();
            if (doc is null) return NotDetected;

            var accessToken  = doc["access_token"]?.GetValue<string>();
            var refreshToken = doc["refresh_token"]?.GetValue<string>();
            var expiresAt    = doc["expiresAt"]?.GetValue<string>();
            var authMethod   = doc["authMethod"]?.GetValue<string>();
            var profileArn   = doc["profileArn"]?.GetValue<string>();
            var region       = doc["region"]?.GetValue<string>() ?? "us-east-1";
            var clientIdHash = doc["clientIdHash"]?.GetValue<string>();
            var clientId     = doc["client_id"]?.GetValue<string>();
            var clientSecret = doc["client_secret"]?.GetValue<string>();

            // Fall back to {clientIdHash}.json for client credentials if missing
            if ((clientId is null || clientSecret is null) && clientIdHash is not null)
            {
                var hashPath = Path.Combine(userProfile, ".aws", "sso", "cache", $"{clientIdHash}.json");
                if (File.Exists(hashPath))
                {
                    try
                    {
                        var hashDoc = JsonNode.Parse(File.ReadAllText(hashPath))?.AsObject();
                        clientId     ??= hashDoc?["client_id"]?.GetValue<string>();
                        clientSecret ??= hashDoc?["client_secret"]?.GetValue<string>();
                    }
                    catch { }
                }
            }

            if (refreshToken is null) return NotDetected;

            return new QuotaProviderInfo(
                IsDetected:   true,
                Email:        "",
                PlanType:     "",
                AccessToken:  accessToken,
                RefreshToken: refreshToken,
                ExpiresAt:    expiresAt,
                ClientId:     clientId,
                ClientSecret: clientSecret,
                AuthMethod:   authMethod,
                ProfileArn:   profileArn,
                Region:       region,
                ApiHost:      null);
        }
        catch
        {
            return NotDetected;
        }
    }

    private static QuotaProviderInfo ScanTrae()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "Trae", "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) return NotDetected;

            var json = File.ReadAllText(path);
            var doc  = JsonNode.Parse(json)?.AsObject();
            if (doc is null) return NotDetected;

            var authInfoRaw = doc["iCubeAuthInfo://icube.cloudide"]?.GetValue<string>();
            if (string.IsNullOrEmpty(authInfoRaw)) return NotDetected;

            var authDoc = JsonNode.Parse(authInfoRaw)?.AsObject();
            if (authDoc is null) return NotDetected;

            var token        = authDoc["token"]?.GetValue<string>();
            var refreshToken = authDoc["refreshToken"]?.GetValue<string>();
            var host         = authDoc["host"]?.GetValue<string>() ?? "https://api-sg-central.trae.ai";
            var account      = authDoc["account"]?.AsObject();
            var email        = account?["email"]?.GetValue<string>() ?? "";

            if (token is null) return NotDetected;

            return new QuotaProviderInfo(
                IsDetected:   true,
                Email:        email,
                PlanType:     "",
                AccessToken:  token,
                RefreshToken: refreshToken,
                ExpiresAt:    null,
                ClientId:     null,
                ClientSecret: null,
                AuthMethod:   null,
                ProfileArn:   null,
                Region:       "sg-central",
                ApiHost:      host);
        }
        catch
        {
            return NotDetected;
        }
    }
}
