using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Infrastructure.Engine.NineRouter;
using TunnelAgent.Infrastructure.Services;

namespace TunnelAgent.Services;

/// <summary>
/// Ensures a 9Router client API key exists for <c>/v1</c> callers and stores it
/// in the user environment as <see cref="EnvVarName"/>.
/// </summary>
public sealed class NineRouterClientKeyService
{
    /// <summary>User-environment variable agents use as the 9Router <c>/v1</c> Bearer token.</summary>
    public const string EnvVarName = "TUNNEL_AGENT_9ROUTER_API_KEY";

    /// <summary>Display name used when creating a new dashboard key.</summary>
    public const string DefaultKeyName = "Tunnel Agent";

    /// <summary>
    /// If <see cref="EnvVarName"/> is empty, lists existing <c>/api/keys</c> entries
    /// and creates one named <see cref="DefaultKeyName"/> when none exist, then
    /// writes the secret to the user environment off the UI thread.
    /// </summary>
    /// <param name="client">Management API client bound to the running 9Router port.</param>
    /// <param name="ct">Token used to cancel list/create.</param>
    public async Task EnsureUserApiKeyAsync(ApiClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!string.IsNullOrWhiteSpace(UserEnvironmentService.Get(EnvVarName)))
            return;

        var keys = await client.ListKeysAsync(ct).ConfigureAwait(false);
        var secret = keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k.Key))?.Key;
        if (string.IsNullOrWhiteSpace(secret))
        {
            var created = await client.CreateKeyAsync(DefaultKeyName, ct).ConfigureAwait(false);
            secret = created.Key;
        }

        if (string.IsNullOrWhiteSpace(secret))
            return;

        var value = secret;
        await Task.Run(() => UserEnvironmentService.Set(EnvVarName, value), ct).ConfigureAwait(false);
    }
}
