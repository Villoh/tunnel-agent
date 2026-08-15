using System;
using System.Net;
using System.Text.Json;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>
/// Thrown when the 9Router local management API returns a non-success status.
/// Response bodies are surfaced as <see cref="Exception.Message"/>; request bodies
/// (which may contain API keys) are never included.
/// </summary>
public class NineRouterApiException : Exception
{
    /// <summary>Creates an exception for a failed management-API response.</summary>
    /// <param name="statusCode">HTTP status returned by 9Router.</param>
    /// <param name="message">Safe error text from the status line or JSON <c>error</c> field.</param>
    public NineRouterApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the HTTP status code returned by 9Router.</summary>
    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// Thrown when dashboard authentication is required and either no password was
/// configured or <c>POST /api/auth/login</c> failed.
/// </summary>
public sealed class NineRouterAuthException : NineRouterApiException
{
    /// <summary>Creates an authentication failure for a 401/403 dashboard response.</summary>
    /// <param name="message">Safe error text that does not include the password.</param>
    /// <param name="statusCode">HTTP status from the API or login attempt. Defaults to 401.</param>
    public NineRouterAuthException(string message, HttpStatusCode statusCode = HttpStatusCode.Unauthorized)
        : base(statusCode, message)
    {
    }
}

/// <summary>
/// A provider connection returned by <c>GET/POST/PUT /api/providers</c>.
/// Sensitive credential fields are omitted by 9Router in list/detail responses.
/// </summary>
public sealed record NineRouterProvider
{
    /// <summary>Gets the connection id.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the 9Router provider id (for example <c>openai</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the auth mode, typically <c>apikey</c>, <c>oauth</c>, or <c>cookie</c>.</summary>
    public string? AuthType { get; init; }

    /// <summary>Gets the dashboard display name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the per-provider priority used for fallback.</summary>
    public int? Priority { get; init; }

    /// <summary>Gets the optional global priority.</summary>
    public int? GlobalPriority { get; init; }

    /// <summary>Gets whether the connection is enabled. 9Router stores this as <c>isActive</c>.</summary>
    public bool? IsActive { get; init; }

    /// <summary>Gets the optional default model for this connection.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Gets the last test status string (for example <c>unknown</c>).</summary>
    public string? TestStatus { get; init; }

    /// <summary>Gets the last connection error, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets when <see cref="LastError"/> was recorded, if present.</summary>
    public string? LastErrorAt { get; init; }

    /// <summary>Gets provider-specific extra fields such as <c>baseUrl</c> or proxy settings.</summary>
    public JsonElement? ProviderSpecificData { get; init; }
}

/// <summary>
/// Body for <c>POST /api/providers</c> when adding an API-key (or cookie) connection.
/// </summary>
public sealed record NineRouterCreateProviderRequest
{
    /// <summary>Gets the 9Router provider id (for example <c>openai</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>Gets the dashboard display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the upstream API key or cookie value. Never logged by <see cref="ApiClient"/>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Gets the auth mode sent to 9Router. Defaults to <c>apikey</c>.</summary>
    public string AuthType { get; init; } = "apikey";

    /// <summary>Gets the optional per-provider priority.</summary>
    public int? Priority { get; init; }

    /// <summary>Gets the optional default model.</summary>
    public string? DefaultModel { get; init; }
}

/// <summary>Body for <c>PUT /api/providers/{id}</c>. Null properties are omitted from JSON.</summary>
public sealed record NineRouterUpdateProviderRequest
{
    /// <summary>Gets the replacement display name, if changing it.</summary>
    public string? Name { get; init; }

    /// <summary>Gets whether the connection should be enabled (<c>isActive</c>).</summary>
    public bool? IsActive { get; init; }

    /// <summary>Gets a replacement API key for <c>apikey</c> connections. Never logged by <see cref="ApiClient"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets the replacement per-provider priority, if changing it.</summary>
    public int? Priority { get; init; }

    /// <summary>Gets the replacement default model, if changing it.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Gets the replacement test-status string, if changing it.</summary>
    public string? TestStatus { get; init; }
}

/// <summary>Body for <c>POST /api/providers/validate</c> (dry-run credential check).</summary>
public sealed record NineRouterValidateProviderRequest
{
    /// <summary>Gets the 9Router provider id to probe.</summary>
    public required string Provider { get; init; }

    /// <summary>Gets the API key to validate. Never logged by <see cref="ApiClient"/>.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>Result of <c>POST /api/providers/validate</c>.</summary>
/// <param name="Valid">Whether 9Router accepted the credentials.</param>
/// <param name="Error">Failure text from 9Router, if any.</param>
public sealed record NineRouterValidationResult(bool Valid, string? Error);

/// <summary>Result of <c>POST /api/providers/{id}/test</c>.</summary>
/// <param name="Valid">Whether the stored connection responded successfully.</param>
/// <param name="Error">Failure text from 9Router, if any.</param>
/// <param name="Refreshed">Whether 9Router refreshed OAuth credentials during the test.</param>
public sealed record NineRouterTestResult(bool Valid, string? Error, bool Refreshed);

/// <summary>
/// A client API key from <c>GET /api/keys</c>. These keys authenticate callers of
/// <c>/v1</c>; they are not upstream provider keys.
/// </summary>
public sealed record NineRouterApiKey
{
    /// <summary>Gets the key id.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the dashboard display name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the secret key value when 9Router includes it. Never logged by <see cref="ApiClient"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the machine id the key is bound to.</summary>
    public string? MachineId { get; init; }

    /// <summary>Gets whether the key is enabled.</summary>
    public bool? IsActive { get; init; }

    /// <summary>Gets the ISO-8601 creation timestamp, if present.</summary>
    public string? CreatedAt { get; init; }
}

/// <summary>Response from <c>POST /api/keys</c> (201). Includes the newly issued secret once.</summary>
public sealed record NineRouterCreatedApiKey
{
    /// <summary>Gets the new key id.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the dashboard display name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the newly issued secret. Never logged by <see cref="ApiClient"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the machine id the key is bound to.</summary>
    public string? MachineId { get; init; }
}

/// <summary>9Router OAuth provider ids and flow helpers.</summary>
public static class NineRouterOAuthProviders
{
    /// <summary>Claude Code OAuth (<c>authorization_code_pkce</c>).</summary>
    public const string Claude = "claude";

    /// <summary>Gemini CLI OAuth (<c>authorization_code</c>).</summary>
    public const string GeminiCli = "gemini-cli";

    /// <summary>GitHub Copilot OAuth (<c>device_code</c>). Stored as provider id <c>github</c>.</summary>
    public const string GitHub = "github";

    /// <summary>Default wait for the user to finish browser sign-in.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2.5);

    /// <summary>Whether <paramref name="providerId"/> starts through <c>GET .../device-code</c>.</summary>
    public static bool IsDeviceCode(string providerId) =>
        providerId.Equals("github", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("kiro", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("kimi", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("kimi-coding", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("kilocode", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("codebuddy-cn", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("codebuddy-intl", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("qoder", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("grok-cli", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of starting a 9Router OAuth flow
/// (<c>GET /api/oauth/{provider}/authorize</c> or <c>.../device-code</c>).
/// </summary>
public sealed record NineRouterOAuthStartResult
{
    /// <summary>Gets the 9Router provider id used in the request path.</summary>
    public required string Provider { get; init; }

    /// <summary>Gets the flow type when 9Router included it (<c>authorization_code_pkce</c>, <c>device_code</c>, …).</summary>
    public string? FlowType { get; init; }

    /// <summary>
    /// Gets the URL to open in the system browser: <c>authUrl</c> for authorize
    /// flows, or <c>verification_uri_complete</c> / <c>verification_uri</c> for device code.
    /// </summary>
    public string? BrowserUrl { get; init; }

    /// <summary>Gets the PKCE/OAuth state to send back on exchange.</summary>
    public string? State { get; init; }

    /// <summary>Gets the PKCE verifier. Never logged by <see cref="ApiClient"/>.</summary>
    public string? CodeVerifier { get; init; }

    /// <summary>Gets the redirect URI registered for this authorize request.</summary>
    public string? RedirectUri { get; init; }

    /// <summary>Gets the device code for <c>POST .../poll</c>. Never logged by <see cref="ApiClient"/>.</summary>
    public string? DeviceCode { get; init; }

    /// <summary>Gets the suggested poll interval in seconds (GitHub default is 5).</summary>
    public int IntervalSeconds { get; init; } = 5;

    /// <summary>Gets the fixed local callback port required by providers such as Codex and xAI.</summary>
    public int? FixedPort { get; init; }

    /// <summary>Gets the local callback path required by the provider.</summary>
    public string? CallbackPath { get; init; }

    /// <summary>
    /// Gets device-flow fields that 9Router expects back as <c>extraData</c> while polling.
    /// Sensitive values are never logged.
    /// </summary>
    public JsonElement? ExtraData { get; init; }
}

/// <summary>Result of one <c>POST /api/oauth/{provider}/poll</c> attempt.</summary>
/// <param name="Success">Whether 9Router stored a connection.</param>
/// <param name="Pending">Whether the user has not finished authorizing yet.</param>
/// <param name="Error">Failure or pending error code/description from 9Router.</param>
/// <param name="Connection">The new connection when <paramref name="Success"/> is true.</param>
public sealed record NineRouterOAuthPollResult(
    bool Success,
    bool Pending,
    string? Error,
    NineRouterProvider? Connection);
