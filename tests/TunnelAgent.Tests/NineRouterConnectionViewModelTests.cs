using TunnelAgent.Infrastructure.Engine.NineRouter;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Tests;

public sealed class NineRouterConnectionViewModelTests
{
    [Fact]
    public void ApplyUsage_NormalizesUsagePercentages()
    {
        var connection = new NineRouterConnectionViewModel(
            "connection-1", "codex", "Codex", true, "oauth", null);

        connection.ApplyUsage(new NineRouterUsage
        {
            Plan = "plus",
            Quotas = new()
            {
                ["primary"] = new() { DisplayName = "Primary", Used = 25, Total = 100 },
                ["weekly"] = new() { RemainingPercentage = 80 },
                ["monthly"] = new() { Remaining = 60 },
                ["credits"] = new() { Total = 1_000, Remaining = 200 },
                ["unknown"] = new()
            }
        });

        Assert.Equal("plus", connection.PlanBadge);
        Assert.True(connection.HasUsage);
        Assert.Equal(4, connection.UsageBars.Count);
        Assert.Equal(0.25, connection.UsageBars[0].Used, 2);
        Assert.Equal(0.20, connection.UsageBars[1].Used, 2);
        Assert.Equal(0.40, connection.UsageBars[2].Used, 2);
        Assert.Equal(0.80, connection.UsageBars[3].Used, 2);
    }
}
