using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>Result of probing an upstream OpenAI-compatible provider's <c>/models</c> endpoint.</summary>
public sealed record UpstreamModelsResult(bool Success, IReadOnlyList<string> Models, string? Error);

/// <summary>
/// Queries an upstream OpenAI-compatible provider's <c>{base-url}/models</c> endpoint
/// directly (not through the proxy) to validate the URL/key and list available models.
/// </summary>
public sealed class UpstreamModelFetchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// GET <c>{baseUrl}/models</c> with the provider API key. Returns the model ids on HTTP 200,
    /// or a failure with an error message on any non-success status or network error.
    /// </summary>
    public async Task<UpstreamModelsResult> FetchAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new UpstreamModelsResult(false, [], "Base URL is required.");

        var url = $"{baseUrl.TrimEnd('/')}/models";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new UpstreamModelsResult(false, [], $"Invalid provider URL: {url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await Http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
                return new UpstreamModelsResult(false, [], $"Provider returned HTTP {(int)resp.StatusCode} from {url}");

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var data = body?["data"]?.AsArray();
            if (data is null)
                return new UpstreamModelsResult(false, [], "Provider response did not contain a model list.");

            var models = data
                .Select(item => item?["id"]?.GetValue<string>() ?? "")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (models.Count == 0)
                return new UpstreamModelsResult(false, [], "Provider returned an empty model list.");

            return new UpstreamModelsResult(true, models, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UpstreamModelsResult(false, [], "Model lookup was cancelled.");
        }
        catch (Exception ex)
        {
            return new UpstreamModelsResult(false, [], $"Couldn't reach the provider URL: {ex.Message}");
        }
    }
}
