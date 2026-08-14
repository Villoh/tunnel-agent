using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class CursorQuotaParserTests
{
    [Fact]
    public void ParsePeriodUsage_WithAutoAndApiPercents_ReturnsThoseBarsNotIncludedSpend()
    {
        var json = """
            {
              "billingCycleEnd": "1771077734000",
              "planUsage": {
                "includedSpend": 1288,
                "limit": 2000,
                "autoPercentUsed": 3.9,
                "apiPercentUsed": 2.6,
                "totalPercentUsed": 3.7
              }
            }
            """;

        var bars = CursorQuotaParser.ParsePeriodUsage(json);

        Assert.Equal(2, bars.Count);
        Assert.Equal(CursorQuotaParser.AutoTitle, bars[0].Title);
        Assert.Equal(0.039, bars[0].UsedFraction, 3);
        Assert.Equal(CursorQuotaParser.ApiTitle, bars[1].Title);
        Assert.Equal(0.026, bars[1].UsedFraction, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1771077734000), bars[0].ResetsAt);
        Assert.Equal(bars[0].ResetsAt, bars[1].ResetsAt);
    }

    [Fact]
    public void ParsePeriodUsage_WithoutPercents_FallsBackToIncludedSpendDollars()
    {
        var json = """
            {
              "planUsage": { "includedSpend": 23222, "limit": 40000 }
            }
            """;

        var bar = Assert.Single(CursorQuotaParser.ParsePeriodUsage(json));

        Assert.Equal("Included ($232.22/$400.00)", bar.Title);
        Assert.Equal(0.58055, bar.UsedFraction, 5);
        Assert.Null(bar.ResetsAt);
    }

    [Fact]
    public void ParsePeriodUsage_BillingCycleEndRfc3339_SetsReset()
    {
        var json = """
            {
              "billingCycleEnd": "2026-09-01T00:00:00.000Z",
              "planUsage": { "autoPercentUsed": 10, "apiPercentUsed": 20 }
            }
            """;

        var bars = CursorQuotaParser.ParsePeriodUsage(json);

        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00.000Z"), bars[0].ResetsAt);
    }

    [Fact]
    public void ParsePeriodUsage_BillingCycleEndUnixMsNumber_SetsReset()
    {
        var json = """
            {
              "billingCycleEnd": 1771077734000,
              "planUsage": { "autoPercentUsed": 1, "apiPercentUsed": 2 }
            }
            """;

        var bars = CursorQuotaParser.ParsePeriodUsage(json);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1771077734000), bars[0].ResetsAt);
    }

    [Fact]
    public void ParsePeriodUsage_OnDemandIndividualLimit_AddsOnDemandBar()
    {
        var json = """
            {
              "planUsage": { "autoPercentUsed": 0, "apiPercentUsed": 0 },
              "spendLimitUsage": { "individualLimit": 10000, "individualUsed": 2500 }
            }
            """;

        var bars = CursorQuotaParser.ParsePeriodUsage(json);

        Assert.Equal(3, bars.Count);
        var onDemand = bars[2];
        Assert.Equal("On-demand ($25.00/$100.00)", onDemand.Title);
        Assert.Equal(0.25, onDemand.UsedFraction);
        Assert.Null(onDemand.ResetsAt);
    }

    [Fact]
    public void ParsePeriodUsage_EmptyPlanUsage_ReturnsNoBars()
    {
        var json = """
            { "billingCycleStart": "1772124774973", "billingCycleEnd": "1772124774973", "displayThreshold": 100 }
            """;

        Assert.Empty(CursorQuotaParser.ParsePeriodUsage(json));
    }

    [Fact]
    public void ParsePeriodUsage_InvalidJson_ReturnsNoBars()
    {
        Assert.Empty(CursorQuotaParser.ParsePeriodUsage("{not-json"));
        Assert.Empty(CursorQuotaParser.ParsePeriodUsage(""));
    }

    [Fact]
    public void ParseAuthUsage_Gpt4RequestCap_ReturnsIncludedRequestsBar()
    {
        var json = """
            {
              "gpt-4": {
                "numRequests": 39,
                "maxRequestUsage": 500
              },
              "startOfMonth": "2026-02-09T17:36:37.000Z"
            }
            """;

        var bar = Assert.Single(CursorQuotaParser.ParseAuthUsage(json));

        Assert.Equal("Included requests (39/500)", bar.Title);
        Assert.Equal(39.0 / 500.0, bar.UsedFraction, 5);
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T17:36:37.000Z"), bar.ResetsAt);
    }

    [Fact]
    public void ParseUsageSummary_IndividualAndTeam_ReturnsBars()
    {
        var json = """
            {
              "billingCycleEnd": "2026-08-01T00:00:00.000Z",
              "individualUsage": { "overall": { "enabled": true, "used": 71, "limit": 10000 } },
              "teamUsage": {
                "pooled": { "enabled": true, "used": 3479810, "limit": 60000000 },
                "onDemand": { "enabled": true, "used": 0, "limit": 5000000 }
              }
            }
            """;

        var bars = CursorQuotaParser.ParseUsageSummary(json);

        Assert.Equal(3, bars.Count);
        Assert.Equal("Included (71/10000)", bars[0].Title);
        Assert.Equal(71.0 / 10000.0, bars[0].UsedFraction, 5);
        Assert.Equal("Team (3479810/60000000)", bars[1].Title);
        Assert.Equal("On-demand (0/5000000)", bars[2].Title);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00.000Z"), bars[0].ResetsAt);
    }

    [Fact]
    public void ParsePlanName_ReadsPlanInfoPlanName()
    {
        Assert.Equal("Pro", CursorQuotaParser.ParsePlanName("""{"planInfo":{"planName":"Pro"}}"""));
        Assert.Null(CursorQuotaParser.ParsePlanName("""{"planInfo":{}}"""));
        Assert.Null(CursorQuotaParser.ParsePlanName(null));
    }
}
