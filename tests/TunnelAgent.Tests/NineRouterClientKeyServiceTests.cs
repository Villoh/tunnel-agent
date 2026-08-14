using System.Net;
using System.Text;
using System.Text.Json;
using TunnelAgent.Infrastructure.Engine.NineRouter;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

[Collection("UserEnvironment")]
public sealed class NineRouterClientKeyServiceTests : IDisposable
{
    private readonly InMemoryUserEnvironmentService _env = new();
    private readonly IUserEnvironmentService _previousEnv;
    private readonly NineRouterClientKeyService _service = new();

    public NineRouterClientKeyServiceTests()
    {
        _previousEnv = TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_env);
    }

    public void Dispose()
    {
        TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_previousEnv);
    }

    [Fact]
    public async Task EnsureUserApiKeyAsync_EnvAlreadySet_DoesNotCallApi()
    {
        _env.Set(NineRouterClientKeyService.EnvVarName, "existing-key");
        using var handler = new FakeApiHandler();
        using var client = new ApiClient(20128, handler);

        await _service.EnsureUserApiKeyAsync(client);

        Assert.Empty(handler.Requests);
        Assert.Equal("existing-key", _env.Get(NineRouterClientKeyService.EnvVarName));
    }

    [Fact]
    public async Task EnsureUserApiKeyAsync_ExistingListedKey_SetsEnvWithoutCreate()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            { "keys": [ { "id": "k1", "name": "Existing", "key": "listed-secret", "isActive": true } ] }
            """);
        using var client = new ApiClient(20128, handler);

        await _service.EnsureUserApiKeyAsync(client);

        Assert.Equal("listed-secret", _env.Get(NineRouterClientKeyService.EnvVarName));
        Assert.Single(handler.Requests);
        Assert.Equal("/api/keys", handler.Requests[0].Path);
        Assert.Equal("GET", handler.Requests[0].Method);
    }

    [Fact]
    public async Task EnsureUserApiKeyAsync_NoKeys_CreatesTunnelAgentKey()
    {
        using var handler = new FakeApiHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """{ "keys": [] }""");
        handler.EnqueueJson(HttpStatusCode.Created, """
            { "id": "k-new", "name": "Tunnel Agent", "key": "created-secret", "machineId": "m1" }
            """);
        using var client = new ApiClient(20128, handler);

        await _service.EnsureUserApiKeyAsync(client);

        Assert.Equal("created-secret", _env.Get(NineRouterClientKeyService.EnvVarName));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal("/api/keys", handler.Requests[1].Path);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal(NineRouterClientKeyService.DefaultKeyName, body.RootElement.GetProperty("name").GetString());
    }

    private sealed class InMemoryUserEnvironmentService : IUserEnvironmentService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => _values[name] = value;
        public void Remove(string name) => _values.Remove(name);
    }

    private sealed class FakeApiHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<CapturedRequest> Requests { get; } = [];

        public void EnqueueJson(HttpStatusCode status, string json)
        {
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
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

            Requests.Add(new CapturedRequest(request.Method.Method, path, string.IsNullOrEmpty(body) ? null : body));
            if (_responses.Count == 0)
                throw new InvalidOperationException($"Unexpected {request.Method} {path}");
            return _responses.Dequeue()();
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string? Body);
}
