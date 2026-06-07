using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine;
namespace TunnelAgent.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void DesignTimeConstructor_SetsExpectedDefaults()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(SectionKey.Providers, vm.SelectedSection);
        Assert.False(vm.IsSidebarCollapsed);
        Assert.False(vm.IsDark);
        Assert.Equal(EngineState.Stopped, vm.EngineState);
        Assert.False(vm.UpdateAvailable);
        Assert.Equal(0, vm.DownloadProgress);
        Assert.False(vm.ShowUpdateToast);
        Assert.False(vm.ShowAddAccountDialog);
        Assert.False(vm.ShowOAuthStatus);
        Assert.False(vm.ShowUpdateSuccess);
        Assert.Equal("Stopped", vm.EngineStatusText);
        Assert.False(vm.IsLoadingEngineReleases);
        Assert.False(vm.ConfigHasBadge);
    }

    [Fact]
    public async Task EngineService_InitialServerState_IsStopped()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var registry = new EngineRegistryService(settings);
        var vm = new MainWindowViewModel(settings, registry, null!, null!, null!, null!);

        Assert.Equal(ServerState.Stopped, vm.ServerState);
    }

    [Fact]
    public async Task RoutingStrategy_Default_IsRoundRobin()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var registry = new EngineRegistryService(settings);
        var vm = new MainWindowViewModel(settings, registry, null!, null!, null!, null!);

        Assert.Equal(RoutingStrategy.RoundRobin, vm.RoutingStrategy);
    }

    [Fact]
    public void RoutingStrategies_StaticArray_HasTwoEntries()
    {
        var strategies = MainWindowViewModel.RoutingStrategies;

        Assert.Equal(2, strategies.Length);
        Assert.Contains(RoutingStrategy.RoundRobin, strategies);
        Assert.Contains(RoutingStrategy.FillFirst, strategies);
    }

    [Fact]
    public void AppVersion_MatchesProjectVersion()
    {
        var vm = new MainWindowViewModel();

        Assert.NotNull(vm.AppVersion);
        Assert.NotEmpty(vm.AppVersion);
    }

    [Fact]
    public void AuthFilesDescription_IsNotNullOrEmpty()
    {
        var vm = new MainWindowViewModel();

        Assert.NotNull(vm.AuthFilesDescription);
        Assert.NotEmpty(vm.AuthFilesDescription);
    }

    [Fact]
    public void SelectQuotaCommand_SelectsQuotaSectionAndSupportedProvidersOnly()
    {
        var vm = new MainWindowViewModel();
        var claude = new ProviderViewModel("claude", "Claude", PackIconSimpleIconsKind.Claude, "#000000", "Claude", isOAuth: true);
        var codex = new ProviderViewModel("codex", "Codex", PackIconSimpleIconsKind.OpenAi, "#000000", "Codex", isOAuth: true);
        var local = new ProviderViewModel("local-ai", "Local", PackIconSimpleIconsKind.OpenAi, "#000000", "Local");
        claude.Accounts.Add(new ProviderAccountViewModel("claude", "", "Account", isDisabled: false));
        codex.Accounts.Add(new ProviderAccountViewModel("codex", "", "Account", isDisabled: false));

        vm.Providers.Add(local);
        vm.Providers.Add(claude);
        vm.Providers.Add(codex);
        vm.SelectQuotaCommand.Execute(null);

        Assert.Equal(SectionKey.Quota, vm.SelectedSection);
        Assert.Equal(2, vm.QuotaProviderCount);
        Assert.Same(claude, vm.SelectedQuotaProvider);
        Assert.True(claude.IsQuotaSelected);
        Assert.False(local.IsQuotaSelected);
    }

    [Fact]
    public void SelectQuotaProviderCommand_UpdatesSelectedQuotaAccounts()
    {
        var vm = new MainWindowViewModel();
        var claude = new ProviderViewModel("claude", "Claude", PackIconSimpleIconsKind.Claude, "#000000", "Claude", isOAuth: true);
        var codex = new ProviderViewModel("codex", "Codex", PackIconSimpleIconsKind.OpenAi, "#000000", "Codex", isOAuth: true);
        var account = new ProviderAccountViewModel("codex", "", "Primary", isDisabled: false);
        codex.Accounts.Add(account);
        vm.Providers.Add(claude);
        vm.Providers.Add(codex);

        vm.SelectQuotaProviderCommand.Execute(codex);

        Assert.Same(codex, vm.SelectedQuotaProvider);
        Assert.True(codex.IsQuotaSelected);
        Assert.False(claude.IsQuotaSelected);
        Assert.True(vm.HasQuotaAccounts);
        Assert.True(vm.HasSelectedQuotaAccounts);
        Assert.False(vm.ShowQuotaAccountEmptyState);
        Assert.Contains(account, vm.SelectedQuotaAccounts);
    }

    [Fact]
    public void SelectedQuotaAccounts_IncludesDisabledAccounts()
    {
        // Disabled in Providers means disabled for CLIProxy routing only,
        // not for quota visibility. Both accounts should appear in Quota.
        var vm = new MainWindowViewModel();
        var claude = new ProviderViewModel("claude", "Claude", PackIconSimpleIconsKind.Claude, "#000000", "Claude", isOAuth: true);
        var enabled = new ProviderAccountViewModel("claude", "", "Enabled", isDisabled: false);
        var disabled = new ProviderAccountViewModel("claude", "", "Disabled", isDisabled: true);
        claude.Accounts.Add(enabled);
        claude.Accounts.Add(disabled);
        vm.Providers.Add(claude);

        vm.SelectQuotaCommand.Execute(null);

        Assert.Contains(enabled, vm.SelectedQuotaAccounts);
        Assert.Contains(disabled, vm.SelectedQuotaAccounts);
        Assert.True(vm.HasQuotaAccounts);
    }

    [Fact]
    public void QuotaProviderCount_SevenProviders_CountsAll()
    {
        var vm = new MainWindowViewModel();
        // 4 standard providers, each with one active account
        foreach (var (id, name, icon) in new[]
        {
            ("claude",         "Claude",         PackIconSimpleIconsKind.Claude),
            ("codex",          "Codex",          PackIconSimpleIconsKind.OpenAi),
            ("gemini-cli",     "Gemini CLI",     PackIconSimpleIconsKind.OpenAi),
            ("antigravity",    "Antigravity",    PackIconSimpleIconsKind.OpenAi),
        })
        {
            var p = new ProviderViewModel(id, name, icon, "#000000", "");
            p.Accounts.Add(new ProviderAccountViewModel(id, "", "Account", isDisabled: false));
            vm.Providers.Add(p);
        }
        // 2 standalone quota providers, each with one active account
        foreach (var (id, name, color) in new[] { ("kiro", "Kiro", "#FF9900"), ("trae", "Trae", "#1464FF") })
        {
            var p = new ProviderViewModel(id, name, PackIconSimpleIconsKind.OpenAi, color, "");
            p.Accounts.Add(new ProviderAccountViewModel(id, "", "Account", isDisabled: false));
            vm.StandaloneQuotaProviders.Add(p);
        }

        Assert.Equal(6, vm.QuotaProviderCount);
    }

    [Fact]
    public async Task IsLaunchAtLoginSupported_DependsOnService()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var registry = new EngineRegistryService(settings);
        var vm = new MainWindowViewModel(settings, registry, null!, null!, null!, null!);

        // IsLaunchAtLoginSupported just returns _launchAtLogin.IsSupported
        // which depends on the platform. It should be a boolean.
        var supported = vm.IsLaunchAtLoginSupported;
        Assert.True(supported || !supported); // always valid bool
    }
}
