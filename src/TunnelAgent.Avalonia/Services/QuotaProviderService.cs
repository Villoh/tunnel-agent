using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

public sealed record QuotaScanResult(
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
        Task.FromResult(new QuotaScanResult(ScanKiro(), ScanTrae()));

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
