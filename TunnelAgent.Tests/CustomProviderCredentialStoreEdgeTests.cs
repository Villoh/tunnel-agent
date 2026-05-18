using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class CustomProviderCredentialStoreEdgeTests
{
    [Fact]
    public void LoadForProvider_NoCredentials_ReturnsEmpty()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);

        var credentials = store.LoadForProvider("openai");

        Assert.Empty(credentials);
    }

    [Fact]
    public void LoadForProvider_FiltersByProvider()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        store.Save("openai", "sk-openai-key", "OpenAI");
        store.Save("local-ai", "sk-local-key", "Local");

        var openaiCreds = store.LoadForProvider("openai");
        var localCreds = store.LoadForProvider("local-ai");

        Assert.Single(openaiCreds);
        Assert.Equal("OpenAI", openaiCreds[0].Label);
        Assert.Single(localCreds);
        Assert.Equal("Local", localCreds[0].Label);
    }

    [Fact]
    public void Delete_NonExistentCredential_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);

        store.Delete("openai", "nonexistent-key");
        // Should not throw
    }

    [Fact]
    public void Delete_OnlyRemovesMatchingCredential()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        store.Save("openai", "sk-key-1", "First");
        store.Save("openai", "sk-key-2", "Second");

        store.Delete("openai", "sk-key-1");

        var remaining = store.LoadForProvider("openai");
        Assert.Single(remaining);
        Assert.Equal("sk-key-2", remaining[0].ApiKey);
    }

    [Fact]
    public void SetDisabled_ReEnable_ClearsDisabledFlag()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        store.Save("openai", "sk-key", "Primary");

        store.SetDisabled("openai", "sk-key", true);
        var disabled = store.LoadForProvider("openai")[0];
        Assert.True(disabled.IsDisabled);

        store.SetDisabled("openai", "sk-key", false);
        var enabled = store.LoadForProvider("openai")[0];
        Assert.False(enabled.IsDisabled);
    }

    [Fact]
    public void SetDisabled_NonExistentCredential_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);

        store.SetDisabled("openai", "nonexistent", true);
        // Should not throw
    }
}
