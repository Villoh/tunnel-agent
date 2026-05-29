using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class QuotaFetchServiceEdgeTests
{
    [Fact]
    public async Task FetchAndApplyAsync_AllUnsupportedProviders_CompleteWithoutError()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);

        var unsupported = new[] { "local-ai", "gemini-cli", "kimi", "antigravity", "unknown" };
        foreach (var providerId in unsupported)
        {
            var provider = new ProviderViewModel(providerId, providerId, PackIconSimpleIconsKind.OpenAi, "#000000", "");
            var account = new ProviderAccountViewModel(providerId, "test-key", "Primary", isDisabled: false);
            provider.Accounts.Add(account);

            await service.FetchAndApplyAsync(provider);

            // Unsupported providers = no quota fetch, so QuotaBars stays empty
            Assert.Empty(account.QuotaBars);
        }
    }

    [Fact]
    public async Task FetchAccountPublicAsync_UnsupportedProvider_CompletesWithoutError()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var account = new ProviderAccountViewModel("unsupported", "test-key", "Primary", isDisabled: false);

        await service.FetchAccountPublicAsync("unsupported", account);

        Assert.Empty(account.QuotaBars);
    }

    [Fact]
    public async Task FetchAndApplyAsync_Claude_WithoutTokenFile_CompletesWithoutError()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var provider = new ProviderViewModel("claude", "Claude", PackIconSimpleIconsKind.Claude, "#D97757", "");
        var account = new ProviderAccountViewModel("claude", "", "test@example.com", isDisabled: false);
        provider.Accounts.Add(account);

        await service.FetchAndApplyAsync(provider);

        // No token file found → QuotaBars stays empty
        Assert.Empty(account.QuotaBars);
    }

    [Fact]
    public async Task FetchAndApplyAsync_Codex_WithoutTokenFile_CompletesWithoutError()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var provider = new ProviderViewModel("codex", "Codex", PackIconSimpleIconsKind.OpenAi, "#23262E", "");
        var account = new ProviderAccountViewModel("codex", "", "test@example.com", isDisabled: false);
        provider.Accounts.Add(account);

        await service.FetchAndApplyAsync(provider);

        Assert.Empty(account.QuotaBars);
    }

    [Fact]
    public async Task FetchAndApplyAsync_GitHubCopilot_WithoutTokenFile_CompletesWithoutError()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var provider = new ProviderViewModel("github-copilot", "GitHub Copilot", PackIconSimpleIconsKind.GitHub, "#24292E", "");
        var account = new ProviderAccountViewModel("github-copilot", "", "test@example.com", isDisabled: false);
        provider.Accounts.Add(account);

        await service.FetchAndApplyAsync(provider);

        Assert.Empty(account.QuotaBars);
    }

    [Fact]
    public async Task FetchAndApplyAsync_MultipleAccounts_ParallelCompletion()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var provider = new ProviderViewModel("local-ai", "Local AI", PackIconSimpleIconsKind.OpenAi, "#000000", "");
        provider.Accounts.Add(new ProviderAccountViewModel("local-ai", "key-1", "First", isDisabled: false));
        provider.Accounts.Add(new ProviderAccountViewModel("local-ai", "key-2", "Second", isDisabled: false));
        provider.Accounts.Add(new ProviderAccountViewModel("local-ai", "key-3", "Third", isDisabled: false));

        await service.FetchAndApplyAsync(provider);

        Assert.Equal(3, provider.Accounts.Count);
        foreach (var account in provider.Accounts)
            Assert.Empty(account.QuotaBars);
    }
}
