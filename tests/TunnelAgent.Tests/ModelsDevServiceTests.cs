using System.Text.Json.Nodes;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class ModelsDevServiceTests
{
    [Fact]
    public void ParseCatalog_ReadsCapabilitiesAndPerMillionPricing()
    {
        var catalog = JsonNode.Parse("""
        {
          "openai": {
            "models": {
              "gpt-test": {
                "name": "GPT Test",
                "reasoning": true,
                "modalities": { "input": ["text", "image"] },
                "limit": { "context": 200000 },
                "cost": {
                  "input": 1.25,
                  "output": 10,
                  "cache_read": 0.125,
                  "cache_write": 1.5
                }
              }
            }
          }
        }
        """);

        var models = ModelsDevService.ParseCatalog(catalog);
        var model = Assert.Single(models).Value;

        Assert.Equal(200000, model.ContextLength);
        Assert.True(model.SupportsImage);
        Assert.True(model.SupportsReasoning);
        Assert.Equal("GPT Test", model.Name);
        Assert.Equal(new ModelPrice(1.25, 10, 0.125, 1.5), model.Pricing);
        Assert.True(models.ContainsKey("openai/gpt-test"));
    }
}
