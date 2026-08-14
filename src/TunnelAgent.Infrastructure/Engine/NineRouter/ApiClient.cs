using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>
/// HTTP client for 9Router's local dashboard management API
/// (<c>http://127.0.0.1:{port}/api/...</c>). Talks to <c>/api/providers</c>,
/// <c>/api/keys</c>, and <c>/api/oauth/{provider}/{action}</c> instead of writing SQLite.
/// </summary>
/// <remarks>
/// Recent 9Router builds require an <c>auth_token</c> cookie on
/// <c>/api/providers/*</c>. When a request returns 401 and a dashboard password
/// was supplied, this client posts <c>/api/auth/login</c>, stores the cookie, and
/// retries once. An empty password tries anonymous access first and does not
/// attempt login. Request bodies that may contain API keys are never logged.
/// </remarks>
public sealed class ApiClient : IDisposable
{
    /// <summary>Cookie name issued by <c>POST /api/auth/login</c>.</summary>
    public const string AuthCookieName = "auth_token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string? _dashboardPassword;
    private readonly object _authLock = new();
    private string? _authCookie;
    private bool _disposed;

    /// <summary>
    /// Creates a client bound to <c>http://127.0.0.1:{port}</c>.
    /// </summary>
    /// <param name="port">Loopback port of the running 9Router process.</param>
    /// <param name="dashboardPassword">
    /// Optional dashboard password. Null or empty means try anonymous access and
    /// skip login on 401.
    /// </param>
    public ApiClient(int port, string? dashboardPassword = null)
        : this(port, new HttpClientHandler { UseCookies = false }, dashboardPassword, disposeHandler: true)
    {
    }

    /// <summary>
    /// Creates a client that sends requests through <paramref name="handler"/>
    /// (used by unit tests to inject a fake <see cref="HttpMessageHandler"/>).
    /// </summary>
    /// <param name="port">Loopback port used to build the base address.</param>
    /// <param name="handler">Transport used by the inner <see cref="HttpClient"/>.</param>
    /// <param name="dashboardPassword">
    /// Optional dashboard password. Null or empty means try anonymous access and
    /// skip login on 401.
    /// </param>
    public ApiClient(int port, HttpMessageHandler handler, string? dashboardPassword = null)
        : this(port, handler, dashboardPassword, disposeHandler: false)
    {
    }

    private ApiClient(int port, HttpMessageHandler handler, string? dashboardPassword, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort || port == 0)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");

        Port = port;
        _dashboardPassword = string.IsNullOrWhiteSpace(dashboardPassword) ? null : dashboardPassword;
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TunnelAgent", TunnelAgent.AppVersion.Current));
    }

    /// <summary>Gets the loopback port this client was constructed with.</summary>
    public int Port { get; }

    /// <summary>Gets whether a dashboard password was supplied (not whether login has succeeded).</summary>
    public bool HasDashboardPassword => _dashboardPassword is not null;

    /// <summary>Lists provider connections via <c>GET /api/providers</c>.</summary>
    /// <param name="ct">Token used to cancel the request.</param>
    /// <returns>The <c>connections</c> array from the JSON envelope.</returns>
    public async Task<IReadOnlyList<NineRouterProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        using var response = await SendWithAuthRetryAsync(HttpMethod.Get, "api/providers", body: null, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<ProviderListResponse>(response, ct).ConfigureAwait(false);
        return payload.Connections ?? [];
    }

    /// <summary>Creates an API-key connection via <c>POST /api/providers</c>.</summary>
    /// <param name="request">Provider id, display name, and API key.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    /// <returns>The created connection with secrets stripped by 9Router.</returns>
    public async Task<NineRouterProvider> CreateProviderAsync(
        NineRouterCreateProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendWithAuthRetryAsync(HttpMethod.Post, "api/providers", request, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<ProviderEnvelope>(response, ct).ConfigureAwait(false);
        return payload.Connection
            ?? throw new NineRouterApiException(response.StatusCode, "Create provider response did not include a connection.");
    }

    /// <summary>Updates a connection via <c>PUT /api/providers/{id}</c>.</summary>
    /// <param name="id">Connection id from <see cref="NineRouterProvider.Id"/>.</param>
    /// <param name="request">Fields to change. Null properties are omitted.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    /// <returns>The updated connection with secrets stripped by 9Router.</returns>
    public async Task<NineRouterProvider> UpdateProviderAsync(
        string id,
        NineRouterUpdateProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendWithAuthRetryAsync(HttpMethod.Put, ProviderPath(id), request, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<ProviderEnvelope>(response, ct).ConfigureAwait(false);
        return payload.Connection
            ?? throw new NineRouterApiException(response.StatusCode, "Update provider response did not include a connection.");
    }

    /// <summary>Deletes a connection via <c>DELETE /api/providers/{id}</c>.</summary>
    /// <param name="id">Connection id from <see cref="NineRouterProvider.Id"/>.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task DeleteProviderAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var response = await SendWithAuthRetryAsync(HttpMethod.Delete, ProviderPath(id), body: null, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Dry-runs credentials via <c>POST /api/providers/validate</c> without saving them.</summary>
    /// <param name="request">Provider id and API key to probe.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<NineRouterValidationResult> ValidateProviderAsync(
        NineRouterValidateProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendWithAuthRetryAsync(HttpMethod.Post, "api/providers/validate", request, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<ValidationResponse>(response, ct).ConfigureAwait(false);
        return new NineRouterValidationResult(payload.Valid, payload.Error);
    }

    /// <summary>Tests a stored connection via <c>POST /api/providers/{id}/test</c>.</summary>
    /// <param name="id">Connection id from <see cref="NineRouterProvider.Id"/>.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<NineRouterTestResult> TestProviderAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var response = await SendWithAuthRetryAsync(HttpMethod.Post, $"{ProviderPath(id)}/test", body: null, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<TestResponse>(response, ct).ConfigureAwait(false);
        return new NineRouterTestResult(payload.Valid, payload.Error, payload.Refreshed);
    }

    /// <summary>Lists client API keys for <c>/v1</c> via <c>GET /api/keys</c>.</summary>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<IReadOnlyList<NineRouterApiKey>> ListKeysAsync(CancellationToken ct = default)
    {
        using var response = await SendWithAuthRetryAsync(HttpMethod.Get, "api/keys", body: null, ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<KeyListResponse>(response, ct).ConfigureAwait(false);
        return payload.Keys ?? [];
    }

    /// <summary>
    /// Starts a curated OAuth flow via <c>GET /api/oauth/{provider}/authorize</c>
    /// (Claude, Gemini CLI) or <c>GET /api/oauth/{provider}/device-code</c>
    /// (GitHub Copilot).
    /// </summary>
    /// <param name="providerId">9Router provider id (<c>claude</c>, <c>gemini-cli</c>, or <c>github</c>).</param>
    /// <param name="redirectUri">
    /// Loopback callback URI for authorize flows. Ignored for GitHub device-code.
    /// Defaults to <c>http://127.0.0.1:{port}/callback</c>.
    /// </param>
    /// <param name="ct">Token used to cancel the request.</param>
    /// <returns>Browser URL plus PKCE/device fields needed to finish the flow.</returns>
    public async Task<NineRouterOAuthStartResult> StartOAuthAsync(
        string providerId,
        string? redirectUri = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = NineRouterOAuthProviders.IsDeviceCode(providerId)
            ? OAuthPath(providerId, "device-code")
            : OAuthPath(providerId, "authorize") + "?redirect_uri=" +
              Uri.EscapeDataString(string.IsNullOrWhiteSpace(redirectUri)
                  ? $"http://127.0.0.1:{Port}/callback"
                  : redirectUri);

        using var response = await SendWithAuthRetryAsync(HttpMethod.Get, path, body: null, ct)
            .ConfigureAwait(false);
        var root = await ReadJsonAsync<JsonElement>(response, ct).ConfigureAwait(false);
        var browserUrl = JsonString(root, "authUrl", "verification_uri_complete", "verification_uri");
        if (string.IsNullOrWhiteSpace(browserUrl))
        {
            throw new NineRouterApiException(
                response.StatusCode,
                "OAuth start did not return a browser URL.");
        }

        return new NineRouterOAuthStartResult
        {
            Provider = providerId,
            FlowType = JsonString(root, "flowType"),
            BrowserUrl = browserUrl,
            State = JsonString(root, "state"),
            CodeVerifier = JsonString(root, "codeVerifier"),
            RedirectUri = JsonString(root, "redirectUri") ?? redirectUri,
            DeviceCode = JsonString(root, "device_code", "deviceCode"),
            IntervalSeconds = JsonInt(root, "interval") is { } interval and > 0 ? interval : 5
        };
    }

    /// <summary>
    /// One device-code poll via <c>POST /api/oauth/{provider}/poll</c>.
    /// Pending authorization returns <see cref="NineRouterOAuthPollResult.Pending"/> instead of throwing.
    /// </summary>
    /// <param name="providerId">9Router provider id (typically <c>github</c>).</param>
    /// <param name="deviceCode">Device code from <see cref="StartOAuthAsync"/>.</param>
    /// <param name="codeVerifier">PKCE verifier when the provider uses one.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<NineRouterOAuthPollResult> PollOAuthAsync(
        string providerId,
        string deviceCode,
        string? codeVerifier = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceCode);
        using var response = await SendWithAuthRetryAsync(
                HttpMethod.Post,
                OAuthPath(providerId, "poll"),
                new OAuthPollRequest(deviceCode, codeVerifier),
                ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<OAuthPollResponse>(response, ct).ConfigureAwait(false);
        var pending = payload.Pending
            || string.Equals(payload.Error, "authorization_pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.Error, "slow_down", StringComparison.OrdinalIgnoreCase);
        var error = string.IsNullOrWhiteSpace(payload.ErrorDescription) ? payload.Error : payload.ErrorDescription;
        return new NineRouterOAuthPollResult(payload.Success, pending, error, payload.Connection);
    }

    /// <summary>
    /// Polls <c>POST /api/oauth/{provider}/poll</c> until 9Router stores a connection or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="providerId">9Router provider id (typically <c>github</c>).</param>
    /// <param name="deviceCode">Device code from <see cref="StartOAuthAsync"/>.</param>
    /// <param name="codeVerifier">PKCE verifier when the provider uses one.</param>
    /// <param name="timeout">Maximum time to wait (about 2–3 minutes from the UI).</param>
    /// <param name="pollInterval">Delay between polls. Defaults to 5 seconds.</param>
    /// <param name="ct">Token used to cancel the wait.</param>
    public async Task<NineRouterProvider> PollOAuthUntilConnectedAsync(
        string providerId,
        string deviceCode,
        string? codeVerifier,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await PollOAuthAsync(providerId, deviceCode, codeVerifier, ct).ConfigureAwait(false);
            if (result.Success)
            {
                return result.Connection
                    ?? throw new NineRouterApiException(
                        HttpStatusCode.OK,
                        "OAuth succeeded but the response did not include a connection.");
            }

            if (!result.Pending)
            {
                throw new NineRouterApiException(
                    HttpStatusCode.BadRequest,
                    result.Error ?? "OAuth poll failed.");
            }

            if (DateTime.UtcNow + interval >= deadline)
            {
                throw new NineRouterApiException(
                    HttpStatusCode.RequestTimeout,
                    "OAuth timed out before the connection was saved.");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Exchanges an authorization code via <c>POST /api/oauth/{provider}/exchange</c>
    /// and stores the connection. Request bodies are never logged.
    /// </summary>
    /// <param name="providerId">9Router provider id (<c>claude</c> or <c>gemini-cli</c>).</param>
    /// <param name="code">Authorization code from the loopback redirect.</param>
    /// <param name="redirectUri">The same redirect URI passed to <see cref="StartOAuthAsync"/>.</param>
    /// <param name="codeVerifier">PKCE verifier from start (required for Claude).</param>
    /// <param name="state">OAuth state from start, when present.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<NineRouterProvider> ExchangeOAuthAsync(
        string providerId,
        string code,
        string redirectUri,
        string? codeVerifier,
        string? state,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        using var response = await SendWithAuthRetryAsync(
                HttpMethod.Post,
                OAuthPath(providerId, "exchange"),
                new OAuthExchangeRequest(code, redirectUri, codeVerifier, state),
                ct)
            .ConfigureAwait(false);
        var payload = await ReadJsonAsync<OAuthExchangeResponse>(response, ct).ConfigureAwait(false);
        return payload.Connection
            ?? throw new NineRouterApiException(response.StatusCode, "OAuth exchange did not include a connection.");
    }

    /// <summary>Creates a client API key via <c>POST /api/keys</c> with <c>{"name":"..."}</c>.</summary>
    /// <param name="name">Dashboard display name for the new key.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    /// <returns>The issued key, including the secret once.</returns>
    public async Task<NineRouterCreatedApiKey> CreateKeyAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var response = await SendWithAuthRetryAsync(
                HttpMethod.Post,
                "api/keys",
                new CreateKeyRequest(name),
                ct)
            .ConfigureAwait(false);
        return await ReadJsonAsync<NineRouterCreatedApiKey>(response, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct)
    {
        var response = await SendOnceAsync(method, relativePath, body, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _dashboardPassword is null)
            return response;

        response.Dispose();
        await LoginAsync(ct).ConfigureAwait(false);
        return await SendOnceAsync(method, relativePath, body, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
            request.Content = CreateJsonContent(body);
        AttachCookie(request);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        CaptureCookie(response);
        return response;
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = CreateJsonContent(new LoginRequest(_dashboardPassword!))
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        CaptureCookie(response);

        if (response.IsSuccessStatusCode)
        {
            var payload = await TryDeserializeAsync<LoginResponse>(response, ct).ConfigureAwait(false);
            if (payload is { Success: false })
            {
                throw new NineRouterAuthException(
                    payload.Error ?? "Dashboard login was rejected.",
                    response.StatusCode);
            }

            return;
        }

        var error = await TryReadErrorAsync(response, ct).ConfigureAwait(false);
        throw new NineRouterAuthException(
            error ?? $"Dashboard login failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode);
    }

    private void AttachCookie(HttpRequestMessage request)
    {
        string? cookie;
        lock (_authLock)
            cookie = _authCookie;
        if (string.IsNullOrEmpty(cookie))
            return;
        request.Headers.TryAddWithoutValidation("Cookie", $"{AuthCookieName}={cookie}");
    }

    private void CaptureCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        foreach (var header in values)
        {
            var token = TryParseAuthCookie(header);
            if (token is null)
                continue;
            lock (_authLock)
                _authCookie = token;
            return;
        }
    }

    internal static string? TryParseAuthCookie(string? setCookieHeader)
    {
        if (string.IsNullOrWhiteSpace(setCookieHeader))
            return null;

        foreach (var part in setCookieHeader.Split(';'))
        {
            var trimmed = part.Trim();
            const string prefix = AuthCookieName + "=";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = trimmed[prefix.Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (parsed is null)
                throw new NineRouterApiException(response.StatusCode, "Management API returned empty JSON.");
            return parsed;
        }
        catch (JsonException ex)
        {
            throw new NineRouterApiException(
                response.StatusCode,
                $"Management API returned invalid JSON: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var error = await TryReadErrorAsync(response, ct).ConfigureAwait(false);
        var message = error ?? $"9Router management API returned HTTP {(int)response.StatusCode}.";
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new NineRouterAuthException(message, response.StatusCode);
        throw new NineRouterApiException(response.StatusCode, message);
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await TryDeserializeAsync<ErrorResponse>(response, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(payload?.Error) ? null : payload.Error;
    }

    private static async Task<T?> TryDeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ByteArrayContent CreateJsonContent(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };
        return content;
    }

    private static string ProviderPath(string id) =>
        "api/providers/" + Uri.EscapeDataString(id);

    private static string OAuthPath(string providerId, string action) =>
        "api/oauth/" + Uri.EscapeDataString(providerId) + "/" + action;

    private static string? JsonString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static int? JsonInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
                return value;
            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private sealed record ProviderListResponse(List<NineRouterProvider>? Connections, string? Error);

    private sealed record ProviderEnvelope(NineRouterProvider? Connection, string? Error);

    private sealed record ValidationResponse(bool Valid, string? Error);

    private sealed record TestResponse(bool Valid, string? Error, bool Refreshed);

    private sealed record KeyListResponse(List<NineRouterApiKey>? Keys, string? Error);

    private sealed record CreateKeyRequest(string Name);

    private sealed record LoginRequest(string Password);

    private sealed record LoginResponse(bool Success, string? Error);

    private sealed record ErrorResponse(string? Error);

    private sealed record OAuthPollRequest(string DeviceCode, string? CodeVerifier);

    private sealed record OAuthPollResponse(
        bool Success,
        bool Pending,
        string? Error,
        string? ErrorDescription,
        NineRouterProvider? Connection);

    private sealed record OAuthExchangeRequest(string Code, string RedirectUri, string? CodeVerifier, string? State);

    private sealed record OAuthExchangeResponse(bool Success, NineRouterProvider? Connection, string? Error);
}
