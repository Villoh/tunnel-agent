using System.Text.Json.Nodes;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class CustomProviderCredentialStoreTests
{
    [Fact]
    public void Save_NewCredential_PersistsMaskedLabelAndRecord()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);

        var (record, created) = store.Save("local ai", "sk-1234567890abcdef");

        Assert.True(created);
        Assert.Equal("local ai", record.ProviderId);
        Assert.Equal("sk-1234567890abcdef", record.ApiKey);
        Assert.Equal("sk-12345...cdef", record.Label);
        Assert.False(record.IsDisabled);
        Assert.True(File.Exists(record.FilePath));
        Assert.StartsWith("openai-compat-local-ai-", Path.GetFileName(record.FilePath));
    }

    [Fact]
    public void Save_DuplicateCredential_ReturnsExistingAndDoesNotCreateSecondFile()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        store.Save("openai", "sk-test", "Primary");

        var (record, created) = store.Save("openai", "sk-test", "Ignored");

        Assert.False(created);
        Assert.Equal("Primary", record.Label);
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void Save_DisabledDuplicate_ReenablesExistingCredential()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        store.Save("openai", "sk-test", "Primary");
        store.SetDisabled("openai", "sk-test", true);

        var (_, created) = store.Save("openai", "sk-test", "Primary");

        Assert.False(created);
        var record = Assert.Single(store.LoadForProvider("openai"));
        Assert.False(record.IsDisabled);
    }

    [Fact]
    public void Delete_ExistingCredential_RemovesFileAndRecord()
    {
        using var temp = new TestTempDirectory();
        var store = new CustomProviderCredentialStore(temp.Path);
        var (record, _) = store.Save("openai", "sk-test", "Primary");

        store.Delete("openai", "sk-test");

        Assert.False(File.Exists(record.FilePath));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void LoadAll_InvalidOrDifferentAuthFiles_IgnoresThem()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("openai-compat-bad.json"), "not json");
        File.WriteAllText(temp.File("openai-compat-wrong.json"), new JsonObject
        {
            ["type"] = "oauth",
            ["provider"] = "openai",
            ["api_key"] = "sk-test"
        }.ToJsonString());
        var store = new CustomProviderCredentialStore(temp.Path);

        Assert.Empty(store.LoadAll());
    }
}
