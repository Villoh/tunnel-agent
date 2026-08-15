using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>9Router settings used to configure model-combo strategies.</summary>
public sealed record NineRouterSettings
{
    /// <summary>Gets per-provider routing overrides keyed by provider id.</summary>
    public Dictionary<string, NineRouterProviderStrategy> ProviderStrategies { get; init; } = [];

    /// <summary>Gets the per-combo strategy overrides keyed by combo name.</summary>
    public Dictionary<string, NineRouterComboStrategy> ComboStrategies { get; init; } = [];
}

/// <summary>Routing override for one 9Router provider.</summary>
public sealed record NineRouterProviderStrategy
{
    /// <summary>Gets the account-selection strategy, such as <c>round-robin</c>.</summary>
    public string? FallbackStrategy { get; init; }

    /// <summary>Gets the number of calls to make before rotating accounts.</summary>
    public int? StickyRoundRobinLimit { get; init; }

    /// <summary>Gets provider-specific settings that Tunnel Agent does not manage.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Strategy override for one 9Router combo.</summary>
public sealed record NineRouterComboStrategy
{
    /// <summary>Gets the routing strategy, such as <c>fallback</c>, <c>round-robin</c>, or <c>fusion</c>.</summary>
    public string? FallbackStrategy { get; init; }

    /// <summary>Gets the optional model used to judge Fusion panel responses.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>Gets settings that Tunnel Agent does not manage.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>A model combo returned by <c>GET /api/combos</c>.</summary>
public sealed record NineRouterCombo
{
    /// <summary>Gets the combo id.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the name exposed to clients as a model.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the ordered model fallback chain.</summary>
    public List<string>? Models { get; init; }

    /// <summary>Gets the optional combo type reported by 9Router.</summary>
    public string? Kind { get; init; }
}

/// <summary>Body for <c>POST /api/combos</c>.</summary>
public sealed record NineRouterCreateComboRequest
{
    /// <summary>Gets the combo name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the ordered models in the combo.</summary>
    public required List<string> Models { get; init; }
}

/// <summary>Body for <c>PUT /api/combos/{id}</c>.</summary>
public sealed record NineRouterUpdateComboRequest
{
    /// <summary>Gets the replacement combo name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the replacement ordered models.</summary>
    public required List<string> Models { get; init; }
}

/// <summary>Body for <c>PATCH /api/settings</c> when replacing provider routing overrides.</summary>
public sealed record NineRouterUpdateSettingsRequest
{
    /// <summary>Gets the complete per-provider routing-override map.</summary>
    public required Dictionary<string, NineRouterProviderStrategy> ProviderStrategies { get; init; }
}

/// <summary>Body for <c>PATCH /api/settings</c> when replacing combo strategies.</summary>
public sealed record NineRouterUpdateComboStrategiesRequest
{
    /// <summary>Gets the complete per-combo strategy map.</summary>
    public required Dictionary<string, NineRouterComboStrategy> ComboStrategies { get; init; }
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

/// <summary>Usage limits returned by <c>GET /api/usage/{connectionId}</c>.</summary>
public sealed record NineRouterUsage
{
    /// <summary>Gets the plan reported by the upstream provider, if any.</summary>
    public string? Plan { get; init; }

    /// <summary>Gets the provider's explanation when usage is unavailable.</summary>
    public string? Message { get; init; }

    /// <summary>Gets usage windows keyed by their provider-specific names.</summary>
    public Dictionary<string, NineRouterUsageQuota>? Quotas { get; init; }
}

/// <summary>One normalized 9Router usage window.</summary>
public sealed record NineRouterUsageQuota
{
    /// <summary>Gets the display name supplied by the provider, if any.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the consumed amount.</summary>
    public double? Used { get; init; }

    /// <summary>Gets the total allocation.</summary>
    public double? Total { get; init; }

    /// <summary>Gets the remaining amount or percentage when the provider supplies one.</summary>
    public double? Remaining { get; init; }

    /// <summary>Gets the explicit remaining percentage, if supplied.</summary>
    public double? RemainingPercentage { get; init; }

    /// <summary>Gets the ISO-8601 reset time, if supplied.</summary>
    public string? ResetAt { get; init; }
}

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

/// <summary>Aggregated usage returned by <c>GET /api/usage/stats</c>.</summary>
public sealed record NineRouterUsageStats
{
    /// <summary>Gets the request count for the selected period.</summary>
    public long TotalRequests { get; init; }
    /// <summary>Gets total prompt/input tokens.</summary>
    public long TotalPromptTokens { get; init; }
    /// <summary>Gets total completion/output tokens.</summary>
    public long TotalCompletionTokens { get; init; }
    /// <summary>Gets total cached tokens.</summary>
    public long TotalCachedTokens { get; init; }
    /// <summary>Gets the cost reported by 9Router.</summary>
    public double TotalCost { get; init; }
    /// <summary>Gets usage grouped by provider.</summary>
    public Dictionary<string, NineRouterUsageBucket> ByProvider { get; init; } = [];
    /// <summary>Gets currently active requests.</summary>
    public List<NineRouterActiveRequest> ActiveRequests { get; init; } = [];
}

/// <summary>One grouped usage value returned by 9Router.</summary>
public sealed record NineRouterUsageBucket
{
    /// <summary>Gets the request count.</summary>
    public long Requests { get; init; }
    /// <summary>Gets prompt/input tokens.</summary>
    public long PromptTokens { get; init; }
    /// <summary>Gets completion/output tokens.</summary>
    public long CompletionTokens { get; init; }
    /// <summary>Gets cached tokens.</summary>
    public long CachedTokens { get; init; }
    /// <summary>Gets reported cost.</summary>
    public double Cost { get; init; }
}

/// <summary>One active request returned by 9Router.</summary>
public sealed record NineRouterActiveRequest
{
    /// <summary>Gets the requested model.</summary>
    public string? Model { get; init; }
    /// <summary>Gets the upstream provider.</summary>
    public string? Provider { get; init; }
    /// <summary>Gets the selected connection label.</summary>
    public string? Account { get; init; }
    /// <summary>Gets the number of matching active requests.</summary>
    public long Count { get; init; }
}

/// <summary>One request record returned by <c>GET /api/usage/request-details</c>.</summary>
public sealed record NineRouterRequestDetail
{
    /// <summary>Gets the request id.</summary>
    public string? Id { get; init; }
    /// <summary>Gets the upstream provider.</summary>
    public string? Provider { get; init; }
    /// <summary>Gets the requested model.</summary>
    public string? Model { get; init; }
    /// <summary>Gets the 9Router connection id.</summary>
    public string? ConnectionId { get; init; }
    /// <summary>Gets the request timestamp.</summary>
    public string? Timestamp { get; init; }
    /// <summary>Gets the final request status.</summary>
    public string? Status { get; init; }
    /// <summary>Gets the timing object returned by 9Router.</summary>
    public JsonElement? Latency { get; init; }
    /// <summary>Gets the token counters returned by 9Router.</summary>
    public NineRouterUsageTokens? Tokens { get; init; }
}

/// <summary>Token counters reported with an individual request.</summary>
public sealed record NineRouterUsageTokens
{
    /// <summary>Gets prompt tokens, when reported in OpenAI format.</summary>
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; init; }
    /// <summary>Gets input tokens, when reported in Anthropic format.</summary>
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }
    /// <summary>Gets completion tokens, when reported in OpenAI format.</summary>
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; init; }
    /// <summary>Gets output tokens, when reported in Anthropic format.</summary>
    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }
    /// <summary>Gets cached tokens, when reported in OpenAI format.</summary>
    [JsonPropertyName("cached_tokens")]
    public long CachedTokens { get; init; }
    /// <summary>Gets cache-read input tokens, when reported in Anthropic format.</summary>
    [JsonPropertyName("cache_read_input_tokens")]
    public long CacheReadInputTokens { get; init; }
}

/// <summary>Paged response returned by <c>GET /api/usage/request-details</c>.</summary>
public sealed record NineRouterRequestDetailsPage
{
    /// <summary>Gets the requested page of redacted request metadata.</summary>
    public List<NineRouterRequestDetail> Details { get; init; } = [];
    /// <summary>Gets server-side paging information.</summary>
    public NineRouterPagination Pagination { get; init; } = new();
}

/// <summary>Paging information returned by 9Router.</summary>
public sealed record NineRouterPagination
{
    /// <summary>Gets the current one-based page.</summary>
    public int Page { get; init; } = 1;
    /// <summary>Gets the selected page size.</summary>
    public int PageSize { get; init; }
    /// <summary>Gets the total result count.</summary>
    public int TotalItems { get; init; }
    /// <summary>Gets the total page count.</summary>
    public int TotalPages { get; init; }
    /// <summary>Gets whether a next page exists.</summary>
    public bool HasNext { get; init; }
    /// <summary>Gets whether a previous page exists.</summary>
    public bool HasPrev { get; init; }
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
