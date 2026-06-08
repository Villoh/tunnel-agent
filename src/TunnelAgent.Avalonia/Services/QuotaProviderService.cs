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

    public async Task<QuotaScanResult> ScanAsync() =>
        new QuotaScanResult(await ScanCursorAsync(), await ScanKiroAsync(), await ScanTraeAsync());

    private static async Task<QuotaProviderInfo> ScanCursorAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath  = Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(dbPath)) return NotDetected;

            string? accessToken = null, refreshToken = null, email = null, planType = null;
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM ItemTable WHERE key LIKE 'cursorAuth/%'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
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

    private static async Task<QuotaProviderInfo> ScanKiroAsync()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(userProfile, ".aws", "sso", "cache", "kiro-auth-token.json");
            if (!File.Exists(path)) return NotDetected;

            var json = await File.ReadAllTextAsync(path);
            var doc  = JsonNode.Parse(json)?.AsObject();
            if (doc is null) return NotDetected;

            var accessToken  = doc["accessToken"]?.GetValue<string>();
            var refreshToken = doc["refreshToken"]?.GetValue<string>();
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
                        var hashDoc = JsonNode.Parse(await File.ReadAllTextAsync(hashPath))?.AsObject();
                        clientId     ??= hashDoc?["client_id"]?.GetValue<string>();
                        clientSecret ??= hashDoc?["client_secret"]?.GetValue<string>();
                    }
                    catch { }
                }
            }

            // profileArn fallback: check kiro.kiroagent/profile.json
            if (profileArn is null)
            {
                var profilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Kiro", "User", "globalStorage", "kiro.kiroagent", "profile.json");
                if (File.Exists(profilePath))
                {
                    try
                    {
                        var pd = JsonNode.Parse(await File.ReadAllTextAsync(profilePath))?.AsObject();
                        profileArn = pd?["arn"]?.GetValue<string>();
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

    private static async Task<QuotaProviderInfo> ScanTraeAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "Trae", "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) return NotDetected;

            var json = await File.ReadAllTextAsync(path);
            var doc  = JsonNode.Parse(json)?.AsObject();
            if (doc is null) return NotDetected;

            string? token = null, refreshToken = null, email = null;
            var host = "https://api-sg-central.trae.ai";

            // storage.json value is plain JSON on macOS but Electron safeStorage-encrypted on Windows.
            // Try plain JSON first; fall back to reading the token from completion.log.
            var authInfoRaw = doc["iCubeAuthInfo://icube.cloudide"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(authInfoRaw))
            {
                try
                {
                    var authDoc = JsonNode.Parse(authInfoRaw)?.AsObject();
                    token        = authDoc?["token"]?.GetValue<string>();
                    refreshToken = authDoc?["refreshToken"]?.GetValue<string>();
                    host         = authDoc?["host"]?.GetValue<string>() ?? host;
                    email        = authDoc?["account"]?["email"]?.GetValue<string>();
                }
                catch { }
            }

            // Fallback: extract token + userName from the most recent completion.log
            if (token is null)
            {
                var (logToken, logUser) = await ReadTraeTokenFromLogsAsync(appData);
                token = logToken;
                email ??= logUser;
            }

            if (token is null) return NotDetected;

            return new QuotaProviderInfo(
                IsDetected:   true,
                Email:        email ?? "",
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

    /// <summary>
    /// Scans the most recent Trae completion.log for a Cloud-IDE-JWT bearer token.
    /// Used as fallback when storage.json values are Electron safeStorage-encrypted (Windows).
    /// </summary>
    internal static async Task<(string? token, string? userName)> ReadTraeTokenFromLogsAsync(string appData)
    {
        try
        {
            var logsRoot = Path.Combine(appData, "Trae", "logs");
            if (!Directory.Exists(logsRoot)) return (null, null);

            string? latestLog = null;
            DateTime latestWrite = DateTime.MinValue;
            foreach (var session in Directory.GetDirectories(logsRoot))
            foreach (var window in Directory.GetDirectories(session, "window*"))
            {
                var candidate = Path.Combine(window, "exthost",
                    "trae.ai-code-completion", "completion.log");
                if (!File.Exists(candidate)) continue;
                var w = File.GetLastWriteTimeUtc(candidate);
                if (w > latestWrite) { latestWrite = w; latestLog = candidate; }
            }
            if (latestLog is null) return (null, null);

            const int ReadTail = 64 * 1024;
            string tail;
            await using (var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
            using (var sr = new StreamReader(fs))
            {
                fs.Seek(Math.Max(0, fs.Length - ReadTail), SeekOrigin.Begin);
                tail = await sr.ReadToEndAsync();
            }

            // Extract token
            const string TokenMarker = "Cloud-IDE-JWT ";
            var tokenIdx = tail.LastIndexOf(TokenMarker, StringComparison.Ordinal);
            string? token = null;
            if (tokenIdx >= 0)
            {
                var s = tokenIdx + TokenMarker.Length;
                var e = tail.IndexOfAny(new[] { '"', ' ', '\n', '\r' }, s);
                token = e < 0 ? tail[s..] : tail[s..e];
            }

            // Extract userName: "userName: user12345"
            const string UserMarker = "userName: ";
            var userIdx = tail.LastIndexOf(UserMarker, StringComparison.Ordinal);
            string? userName = null;
            if (userIdx >= 0)
            {
                var s = userIdx + UserMarker.Length;
                var e = tail.IndexOfAny(new[] { '\n', '\r', ',' , '"', '}' }, s);
                userName = (e < 0 ? tail[s..] : tail[s..e]).Trim();
            }

            return (token, userName);
        }
        catch { return (null, null); }
    }

}
