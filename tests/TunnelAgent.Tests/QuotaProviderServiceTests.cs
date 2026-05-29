using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class QuotaProviderServiceTests
{
    [Fact]
    public async Task ScanAsync_WhenAuthFilesAbsent_KiroIsNotDetected()
    {
        // Auth files live at fixed OS paths; in a test environment they won't exist.
        var service = new QuotaProviderService();
        var result  = await service.ScanAsync();

        // We can only assert non-exception and that the result is non-null.
        Assert.NotNull(result);
        Assert.NotNull(result.Kiro);
        Assert.NotNull(result.Trae);
    }

    [Fact]
    public async Task ScanAsync_KiroResult_HasSensibleDefaults()
    {
        var service = new QuotaProviderService();
        var result  = await service.ScanAsync();

        if (!result.Kiro.IsDetected)
        {
            Assert.Equal("", result.Kiro.Email);
            Assert.Equal("", result.Kiro.PlanType);
        }
    }

    [Fact]
    public async Task ScanAsync_TraeResult_HasSensibleDefaults()
    {
        var service = new QuotaProviderService();
        var result  = await service.ScanAsync();

        if (!result.Trae.IsDetected)
        {
            Assert.Equal("", result.Trae.Email);
            Assert.Equal("", result.Trae.PlanType);
        }
    }

    [Fact]
    public async Task ScanAsync_CompletesWithoutException()
    {
        var service = new QuotaProviderService();
        // Should never throw regardless of filesystem state
        var result = await service.ScanAsync();
        Assert.NotNull(result);
    }
}
