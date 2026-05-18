using System.Text.Json;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class AppSettingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void Default_AppSettings_HasExpectedValues()
    {
        var settings = new AppSettings();

        Assert.Equal(8317, settings.Port);
        Assert.True(settings.LaunchAtLogin);
        Assert.Equal("system", settings.ThemeMode);
        Assert.True(settings.AutoCheckForUpdates);
        Assert.False(settings.AutoUpdate);
        Assert.Equal("", settings.PreferredEngineVersion);
        Assert.Empty(settings.Providers);
    }

    [Fact]
    public void SerializeDeserialize_PreservesAllProperties()
    {
        var original = new AppSettings
        {
            Port = 9001,
            LaunchAtLogin = false,
            ThemeMode = "dark",
            AutoCheckForUpdates = false,
            AutoUpdate = true,
            PreferredEngineVersion = "v1.2.3",
            RoutingStrategy = ViewModels.RoutingStrategy.FillFirst,
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;

        Assert.NotNull(deserialized);
        Assert.Equal(9001, deserialized.Port);
        Assert.False(deserialized.LaunchAtLogin);
        Assert.Equal("dark", deserialized.ThemeMode);
        Assert.False(deserialized.AutoCheckForUpdates);
        Assert.True(deserialized.AutoUpdate);
        Assert.Equal("v1.2.3", deserialized.PreferredEngineVersion);
        Assert.Equal(ViewModels.RoutingStrategy.FillFirst, deserialized.RoutingStrategy);
    }

    [Fact]
    public void SerializeDeserialize_WithProviders_PreservesAccounts()
    {
        var original = new AppSettings
        {
            Port = 8317,
            Providers =
            [
                new ProviderSettings
                {
                    Id = "local-ai",
                    Enabled = true,
                    BaseUrl = "https://local.example/v1",
                    DisplayName = "Local AI",
                    Accounts =
                    [
                        new ProviderAccountSettings { ApiKey = "sk-test-key", Label = "Primary" },
                        new ProviderAccountSettings { ApiKey = "sk-disabled", Label = "Disabled", Disabled = true }
                    ]
                },
                new ProviderSettings
                {
                    Id = "claude",
                    Enabled = false,
                    BaseUrl = "",
                    DisplayName = "",
                    Accounts = []
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Providers.Count);

        var custom = deserialized.Providers[0];
        Assert.Equal("local-ai", custom.Id);
        Assert.True(custom.Enabled);
        Assert.Equal("https://local.example/v1", custom.BaseUrl);
        Assert.Equal("Local AI", custom.DisplayName);
        Assert.Equal(2, custom.Accounts.Count);
        Assert.Equal("sk-test-key", custom.Accounts[0].ApiKey);
        Assert.Equal("Primary", custom.Accounts[0].Label);
        Assert.False(custom.Accounts[0].Disabled);
        Assert.True(custom.Accounts[1].Disabled);

        var oauth = deserialized.Providers[1];
        Assert.Equal("claude", oauth.Id);
        Assert.False(oauth.Enabled);
        Assert.Empty(oauth.Accounts);
    }

    [Fact]
    public void ProviderAccountSettings_Defaults_AreEmptyOrFalse()
    {
        var account = new ProviderAccountSettings();

        Assert.Equal("", account.ApiKey);
        Assert.Equal("", account.Label);
        Assert.False(account.Disabled);
    }

    [Fact]
    public void ProviderSettings_Defaults_HaveEmptyIdAndEmptyAccounts()
    {
        var provider = new ProviderSettings();

        Assert.Equal("", provider.Id);
        Assert.True(provider.Enabled);
        Assert.Equal("", provider.BaseUrl);
        Assert.Equal("", provider.DisplayName);
        Assert.Empty(provider.Accounts);
    }
}
