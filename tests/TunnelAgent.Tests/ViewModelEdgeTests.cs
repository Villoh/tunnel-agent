using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ViewModelEdgeTests
{
    [Fact]
    public void ProviderViewModel_IsConnecting_DefaultsToFalse()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public void ProviderViewModel_IsConnecting_CanBeSet()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        vm.IsConnecting = true;
        Assert.True(vm.IsConnecting);
        vm.IsConnecting = false;
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public void ProviderViewModel_RefreshAccountCount_WithNoAccounts_ReturnsZero()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        vm.RefreshAccountCount();
        Assert.Equal(0, vm.ActiveAccountCount);
        Assert.Equal("", vm.ConnectedSubText); // n==0 returns Description ("")
    }

    [Fact]
    public void ProviderViewModel_RefreshAccountCount_AllDisabled_ReturnsZero()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        vm.Accounts.Add(new ProviderAccountViewModel("test", "key1", "A", true));
        vm.Accounts.Add(new ProviderAccountViewModel("test", "key2", "B", true));
        vm.RefreshAccountCount();

        Assert.Equal(0, vm.ActiveAccountCount);
        Assert.Equal("", vm.ConnectedSubText); // n==0 returns Description ("")
    }

    [Fact]
    public void ProviderViewModel_RefreshAccountCount_UpdatesConnectedSubText()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        vm.Accounts.Add(new ProviderAccountViewModel("test", "key1", "A", false));
        vm.Accounts.Add(new ProviderAccountViewModel("test", "key2", "B", false));
        vm.RefreshAccountCount();

        Assert.Equal(2, vm.ActiveAccountCount);
        Assert.Equal("2 connected accounts", vm.ConnectedSubText);
    }

    [Fact]
    public void ProviderAccountViewModel_Disabled_DefaultsToFalse()
    {
        var vm = new ProviderAccountViewModel("p", "key", "Label", isDisabled: false);
        Assert.False(vm.IsDisabled);
    }

    [Fact]
    public void ProviderAccountViewModel_Disabled_True()
    {
        var vm = new ProviderAccountViewModel("p", "key", "Label", isDisabled: true);
        Assert.True(vm.IsDisabled);
    }

    [Fact]
    public void ProviderAccountViewModel_IsProviderEnabled_TracksEnabledState()
    {
        var vm = new ProviderAccountViewModel("p", "key", "Label", false);
        Assert.True(vm.IsProviderEnabled); // default

        vm.IsProviderEnabled = false;
        Assert.False(vm.IsProviderEnabled);
    }

    [Fact]
    public void QuotaBarViewModel_DefaultValues()
    {
        var vm = new QuotaBarViewModel();
        Assert.Equal("", vm.Title);
        Assert.Equal("", vm.ResetIn);
        Assert.Equal(0, vm.Used);
        Assert.Equal("0% used", vm.UsedLabel);
    }

    [Fact]
    public void QuotaBarViewModel_UsedAt100Percent()
    {
        var vm = new QuotaBarViewModel { Used = 1.0 };
        Assert.Equal("100% used", vm.UsedLabel);
    }

    [Fact]
    public void QuotaBarViewModel_UsedAt50Percent()
    {
        var vm = new QuotaBarViewModel { Used = 0.5 };
        Assert.Equal("50% used", vm.UsedLabel);
    }

    [Fact]
    public void MainWindowViewModel_SelectedEngineVersionDescription_Default()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var engine = new EngineService(settings);
        var vm = new MainWindowViewModel(settings, engine, null!, null!, null!);

        Assert.Equal("Choose a CLIProxyAPI release to install.", vm.SelectedEngineVersionDescription);
    }

    [Fact]
    public void MainWindowViewModel_CanSelectEngineRelease_Defaults()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var engine = new EngineService(settings);
        var vm = new MainWindowViewModel(settings, engine, null!, null!, null!);

        // IsLoadingEngineReleases is false, engine state is Stopped/NotInstalled
        Assert.True(vm.CanSelectEngineRelease);
    }

    [Fact]
    public void MainWindowViewModel_CanInstallSelectedEngine_NoSelection_ReturnsFalse()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var engine = new EngineService(settings);
        var vm = new MainWindowViewModel(settings, engine, null!, null!, null!);

        Assert.False(vm.CanInstallSelectedEngine);
    }

    [Fact]
    public void MainWindowViewModel_IsAutoUpdateEnabled_ReflectsAutoCheck()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        settings.Current.AutoCheckForUpdates = true;
        var engine = new EngineService(settings);
        var vm = new MainWindowViewModel(settings, engine, null!, null!, null!);

        Assert.True(vm.IsAutoUpdateEnabled);

        settings.Current.AutoCheckForUpdates = false;
        Assert.False(vm.IsAutoUpdateEnabled);
    }

    [Fact]
    public void ProviderViewModel_IsEnabled_SetAndGet()
    {
        var vm = new ProviderViewModel("test", "Test", PackIconSimpleIconsKind.OpenAi, "#000", "");
        Assert.True(vm.IsEnabled); // default

        vm.IsEnabled = false;
        Assert.False(vm.IsEnabled);
    }
}
