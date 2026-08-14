using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TunnelAgent.Infrastructure.Engine.NineRouter;

namespace TunnelAgent.Tests;

public sealed class NineRouterApiClientTests
{
    private const int Port = 20128;

    [Fact]
    public async Task ListProvidersAsync_200WithConnections_ParsesList()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "connections": [
                {
                  "id": "conn-1",
                  "provider": "openai",
                  "authType": "apikey",
                  "name": "OpenAI",
                  "priority": 1,
                  "isActive": true,
                  "testStatus": "unknown"
                },
                {
                  "id": "conn-2",
                  "provider": "kiro",
                  "authType": "oauth",
                  "name": "Kiro",
                  "isActive": false
                }
              ]
            }
            """);
        using var client = new ApiClient(Port, handler);

        var providers = await client.ListProvidersAsync();

        Assert.Equal(2, providers.Count);
        Assert.Equal("conn-1", providers[0].Id);
        Assert.Equal("openai", providers[0].Provider);
        Assert.Equal("apikey", providers[0].AuthType);
        Assert.Equal("OpenAI", providers[0].Name);
        Assert.Equal(1, providers[0].Priority);
        Assert.True(providers[0].IsActive);
        Assert.Equal("unknown", providers[0].TestStatus);
        Assert.Equal("conn-2", providers[1].Id);
        Assert.False(providers[1].IsActive);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/api/providers", handler.Requests[0].Path);
        Assert.Null(handler.Requests[0].Cookie);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task CreateProviderAsync_PostsCamelCaseBody_Returns201Connection()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.Created, """
            {
              "connection": {
                "id": "conn-new",
                "provider": "openai",
                "authType": "apikey",
                "name": "My OpenAI",
                "isActive": true,
                "testStatus": "unknown"
              }
            }
            """);
        using var client = new ApiClient(Port, handler);

        var created = await client.CreateProviderAsync(new NineRouterCreateProviderRequest
        {
            Provider = "openai",
            AuthType = "apikey",
            Name = "My OpenAI",
            ApiKey = "sk-test-key"
        });

        Assert.Equal("conn-new", created.Id);
        Assert.Equal("openai", created.Provider);
        Assert.Equal("My OpenAI", created.Name);
        Assert.True(created.IsActive);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("/api/providers", handler.Requests[0].Path);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("openai", body.RootElement.GetProperty("provider").GetString());
        Assert.Equal("apikey", body.RootElement.GetProperty("authType").GetString());
        Assert.Equal("My OpenAI", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("sk-test-key", body.RootElement.GetProperty("apiKey").GetString());
        Assert.False(body.RootElement.TryGetProperty("priority", out _));
    }

    [Fact]
    public async Task CreateProviderAsync_200Envelope_StillParsesConnection()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            { "connection": { "id": "conn-200", "provider": "openai", "name": "Ok" } }
            """);
        using var client = new ApiClient(Port, handler);

        var created = await client.CreateProviderAsync(new NineRouterCreateProviderRequest
        {
            Provider = "openai",
            Name = "Ok",
            ApiKey = "sk-ok"
        });

        Assert.Equal("conn-200", created.Id);
        Assert.Equal("Ok", created.Name);
    }

    [Fact]
    public async Task UpdateProviderAsync_PutsIsActive_ReturnsUpdatedConnection()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            { "connection": { "id": "conn-1", "provider": "openai", "name": "OpenAI", "isActive": false } }
            """);
        using var client = new ApiClient(Port, handler);

        var updated = await client.UpdateProviderAsync("conn-1", new NineRouterUpdateProviderRequest { IsActive = false });

        Assert.Equal("conn-1", updated.Id);
        Assert.False(updated.IsActive);
        Assert.Equal("PUT", handler.Requests[0].Method);
        Assert.Equal("/api/providers/conn-1", handler.Requests[0].Path);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(body.RootElement.GetProperty("isActive").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task DeleteProviderAsync_204Or200_Completes()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{ "message": "Connection deleted successfully" }""");
        using var client = new ApiClient(Port, handler);

        await client.DeleteProviderAsync("conn-1");

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Equal("/api/providers/conn-1", handler.Requests[0].Path);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task ValidateProviderAsync_ValidTrue_ReturnsResult()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{ "valid": true, "error": null }""");
        using var client = new ApiClient(Port, handler);

        var result = await client.ValidateProviderAsync(new NineRouterValidateProviderRequest
        {
            Provider = "openai",
            ApiKey = "sk-test-key"
        });

        Assert.True(result.Valid);
        Assert.Null(result.Error);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("/api/providers/validate", handler.Requests[0].Path);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("openai", body.RootElement.GetProperty("provider").GetString());
        Assert.Equal("sk-test-key", body.RootElement.GetProperty("apiKey").GetString());
    }

    [Fact]
    public async Task TestProviderAsync_ValidWithRefresh_ReturnsResult()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{ "valid": true, "error": null, "refreshed": true }""");
        using var client = new ApiClient(Port, handler);

        var result = await client.TestProviderAsync("conn-1");

        Assert.True(result.Valid);
        Assert.True(result.Refreshed);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("/api/providers/conn-1/test", handler.Requests[0].Path);
    }

    [Fact]
    public async Task ListKeysAsync_200WithKeys_ParsesList()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "keys": [
                { "id": "k1", "name": "default", "key": "sk-9r-secret", "machineId": "m1", "isActive": true }
              ]
            }
            """);
        using var client = new ApiClient(Port, handler);

        var keys = await client.ListKeysAsync();

        Assert.Single(keys);
        Assert.Equal("k1", keys[0].Id);
        Assert.Equal("default", keys[0].Name);
        Assert.Equal("sk-9r-secret", keys[0].Key);
        Assert.True(keys[0].IsActive);
        Assert.Equal("/api/keys", handler.Requests[0].Path);
    }

    [Fact]
    public async Task CreateKeyAsync_PostsName_Returns201Key()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.Created, """
            { "id": "k2", "name": "tunnel-agent", "key": "sk-9r-new", "machineId": "m1" }
            """);
        using var client = new ApiClient(Port, handler);

        var created = await client.CreateKeyAsync("tunnel-agent");

        Assert.Equal("k2", created.Id);
        Assert.Equal("tunnel-agent", created.Name);
        Assert.Equal("sk-9r-new", created.Key);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("tunnel-agent", body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ListProvidersAsync_401ThenLoginCookieThenRetry_Succeeds()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{ "error": "Unauthorized" }""");
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """{ "success": true, "mustChangePassword": false }""",
            setCookie: "auth_token=jwt-from-login; HttpOnly; Path=/; SameSite=Lax");
        handler.EnqueueJson(HttpStatusCode.OK, """
            { "connections": [ { "id": "conn-1", "provider": "openai", "name": "OpenAI", "isActive": true } ] }
            """);
        using var client = new ApiClient(Port, handler, dashboardPassword: "secret-password");

        var providers = await client.ListProvidersAsync();

        Assert.Single(providers);
        Assert.Equal("conn-1", providers[0].Id);
        Assert.Equal(3, handler.Requests.Count);

        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/api/providers", handler.Requests[0].Path);
        Assert.Null(handler.Requests[0].Cookie);

        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal("/api/auth/login", handler.Requests[1].Path);
        using (var loginBody = JsonDocument.Parse(handler.Requests[1].Body!))
            Assert.Equal("secret-password", loginBody.RootElement.GetProperty("password").GetString());

        Assert.Equal("GET", handler.Requests[2].Method);
        Assert.Equal("/api/providers", handler.Requests[2].Path);
        Assert.Equal("auth_token=jwt-from-login", handler.Requests[2].Cookie);
    }

    [Fact]
    public async Task ListProvidersAsync_401ThenLoginFailure_ThrowsNineRouterAuthException()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{ "error": "Unauthorized" }""");
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{ "error": "Invalid password. 2 attempt(s) left before lockout." }""");
        using var client = new ApiClient(Port, handler, dashboardPassword: "wrong-password");

        var ex = await Assert.ThrowsAsync<NineRouterAuthException>(() => client.ListProvidersAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Invalid password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrong-password", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/auth/login", handler.Requests[1].Path);
    }

    [Fact]
    public async Task ListProvidersAsync_401WithoutPassword_ThrowsWithoutLoginAttempt()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{ "error": "Unauthorized" }""");
        using var client = new ApiClient(Port, handler, dashboardPassword: null);

        var ex = await Assert.ThrowsAsync<NineRouterAuthException>(() => client.ListProvidersAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/providers", handler.Requests[0].Path);
        Assert.False(client.HasDashboardPassword);
    }

    [Fact]
    public void Constructor_InvalidPort_Throws()
    {
        using var handler = new FakeApiHandler();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiClient(0, handler));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiClient(65536, handler));
    }

    [Fact]
    public void TryParseAuthCookie_SetCookieHeader_ExtractsToken()
    {
        var token = ApiClient.TryParseAuthCookie("auth_token=abc.def; HttpOnly; Path=/; SameSite=Lax");

        Assert.Equal("abc.def", token);
    }

    private sealed class FakeApiHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json, string? setCookie = null)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (setCookie is not null)
                    response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
                return response;
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);

            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was null.");
            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
            if (!path.StartsWith('/'))
                path = "/" + path.TrimStart('/');

            request.Headers.TryGetValues("Cookie", out var cookies);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                path,
                string.IsNullOrEmpty(body) ? null : body,
                cookies?.FirstOrDefault()));

            if (_responses.Count == 0)
                throw new InvalidOperationException($"Unexpected {request.Method} {path}");

            return _responses.Dequeue()();
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string? Body, string? Cookie);
}
