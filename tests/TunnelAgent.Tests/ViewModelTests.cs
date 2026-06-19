using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Tests;

[Collection("Localization")]
public sealed class ViewModelTests
{
    [Fact]
    public void ActivityLogViewModel_Constructor_MapsAllProperties()
    {
        var vm = new ActivityLogViewModel("GET", "/v1/models", "agent", "provider", "model", "200", "12ms", "now");

        Assert.Equal("GET", vm.Method);
        Assert.Equal("/v1/models", vm.Path);
        Assert.Equal("agent", vm.Agent);
        Assert.Equal("provider", vm.Provider);
        Assert.Equal("model", vm.Model);
        Assert.Equal("200", vm.Status);
        Assert.Equal("12ms", vm.Elapsed);
        Assert.Equal("now", vm.When);
    }

    [Fact]
    public void AgentViewModel_SetMutableProperties_RaisesChangesAndStoresValues()
    {
        var def = new AgentDefinition(
            "id", "Name", "A description",
            new[] { "binary" }, Array.Empty<string>(),
            null, "#CC785C");
        var vm = new AgentViewModel(def);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Enabled = true;
        vm.RouteProviderId = "claude";

        Assert.Equal("id", vm.Id);
        Assert.Equal("Name", vm.Name);
        Assert.Equal("binary", vm.BinaryName);
        Assert.True(vm.Enabled);
        Assert.Equal("claude", vm.RouteProviderId);
        Assert.Contains(nameof(AgentViewModel.Enabled), changed);
        Assert.Contains(nameof(AgentViewModel.RouteProviderId), changed);
    }

    [Fact]
    public void AvailableModelViewModel_Constructor_MapsAllProperties()
    {
        var vm = new AvailableModelViewModel("gpt-5", "OAuth", "1M", "OpenAI");

        Assert.Equal("gpt-5", vm.Name);
        Assert.Equal("OAuth", vm.AuthKind);
        Assert.Equal("1M", vm.Context);
        Assert.Equal("OpenAI", vm.Provider);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 2)]
    public void AvailableModelGroupViewModel_ModelCountAndHiddenModelCount_ReflectModels(int count, int hidden)
    {
        var vm = new AvailableModelGroupViewModel("OpenAI", "openai", PackIconSimpleIconsKind.OpenAi, "#000000", customIconData: "svg", isExpanded: true);
        for (var i = 0; i < count; i++)
            vm.Models.Add(new AvailableModelViewModel($"model-{i}", "OAuth", "", "OpenAI"));

        Assert.Equal("OpenAI", vm.ProviderName);
        Assert.Equal("openai", vm.ProviderId);
        Assert.True(vm.HasCustomIcon);
        Assert.True(vm.IsExpanded);
        Assert.Equal(count, vm.ModelCount);
        Assert.Equal(hidden, vm.HiddenModelCount);
    }

    [Fact]
    public void EngineReleaseViewModel_DisplayText_AddsPreForPrerelease()
    {
        var stable = new EngineReleaseViewModel(new EngineReleaseInfo("v1.0.0", "One", false, null));
        var pre = new EngineReleaseViewModel(new EngineReleaseInfo("v2.0.0-beta", "Beta", true, DateTimeOffset.UnixEpoch));

        Assert.Equal("v1.0.0", stable.DisplayText);
        Assert.Equal("v1.0.0", stable.ToString());
        Assert.Equal("v2.0.0-beta pre", pre.DisplayText);
        Assert.Equal("Beta", pre.DisplayName);
        Assert.Equal(DateTimeOffset.UnixEpoch, pre.PublishedAt);
    }

    [Fact]
    public void QuotaBarViewModel_UsedLabel_TracksUsedPercentage()
    {
        var vm = new QuotaBarViewModel { Title = "Weekly", ResetIn = "Resets in 1d" };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Used = 0.425;

        Assert.Equal("Weekly", vm.Title);
        Assert.Equal("Resets in 1d", vm.ResetIn);
        Assert.Equal("43% used", vm.UsedLabel);
        Assert.Contains(nameof(QuotaBarViewModel.Used), changed);
        Assert.Contains(nameof(QuotaBarViewModel.UsedLabel), changed);
    }

    [Theory]
    [InlineData("", "", "", false, "")]
    [InlineData("short", "", "Label", true, "Label")]
    [InlineData("1234567890abcdef", "", "", true, "1234...cdef")]
    [InlineData("1234567890abcdef", "mail@example.com", "Label", true, "mail@example.com")]
    public void ProviderAccountViewModel_DisplayAndMask_UseExpectedFallbacks(
        string apiKey, string email, string label, bool isCustomKey, string displayName)
    {
        var vm = new ProviderAccountViewModel("provider", apiKey, label, isDisabled: false)
        {
            Email = email
        };

        Assert.Equal(isCustomKey, vm.IsCustomKey);
        Assert.Equal(displayName, vm.DisplayName);
        if (apiKey.Length > 8)
            Assert.Equal("1234...cdef", vm.MaskedKey);
    }

    [Fact]
    public void ProviderAccountViewModel_DisabledAndQuotaChanges_RaiseEventsAndDerivedProperties()
    {
        var vm = new ProviderAccountViewModel("provider", "key", "Label", isDisabled: false);
        var disabledEvents = new List<bool>();
        var changed = new List<string?>();
        vm.IsDisabledChanged += (_, disabled) => disabledEvents.Add(disabled);
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsDisabled = true;
        vm.QuotaBars.Add(new QuotaBarViewModel());

        Assert.Equal([true], disabledEvents);
        Assert.True(vm.HasQuota);
        Assert.Contains(nameof(ProviderAccountViewModel.IsDisabled), changed);
        Assert.Contains(nameof(ProviderAccountViewModel.HasQuota), changed);
    }

    [Fact]
    public void ProviderViewModel_StatusTextAndColor_ReflectConnectionAndEnabledState()
    {
        var vm = new ProviderViewModel("openai", "OpenAI", PackIconSimpleIconsKind.OpenAi, "#000000", "Description", isOAuth: true);

        Assert.Equal("Description", vm.ConnectedSubText);
        Assert.Equal("#888888", vm.StatusColor);
        Assert.False(vm.HasAccounts);

        vm.Connected = true;
        Assert.Equal("1 connected account", vm.ConnectedSubText);
        Assert.Equal("#3CB371", vm.StatusColor);
        Assert.True(vm.HasAccounts);

        vm.IsEnabled = false;
        Assert.Equal("#CC7A2B", vm.StatusColor);
    }

    [Fact]
    public void ProviderViewModel_AccountCountAndEnableState_TrackAccounts()
    {
        var vm = new ProviderViewModel("local", "Local", PackIconSimpleIconsKind.OpenAi, "#000000", "Description", supportsApiKey: true);
        var active = new ProviderAccountViewModel("local", "key1", "Active", isDisabled: false);
        var disabled = new ProviderAccountViewModel("local", "key2", "Disabled", isDisabled: true);
        vm.Accounts.Add(active);
        vm.Accounts.Add(disabled);

        vm.RefreshAccountCount();
        vm.IsEnabled = false;

        Assert.Equal(1, vm.ActiveAccountCount);
        Assert.Equal("1 API key", vm.ConnectedSubText);
        Assert.True(vm.HasAccounts);
        Assert.False(active.IsProviderEnabled);
        Assert.False(disabled.IsProviderEnabled);
    }

    [Fact]
    public void ProviderAccountViewModel_CustomKeyDisplay_UsesLabelThenMaskedKey()
    {
        var labeled = new ProviderAccountViewModel("local", "1234567890abcdef", "Test", false);
        var unlabeled = new ProviderAccountViewModel("local", "1234567890abcdef", "", false);

        Assert.Equal("Test", labeled.DisplayName);
        Assert.Equal("1234...cdef", labeled.DetailText);
        Assert.Equal("1234...cdef", unlabeled.DisplayName);
        Assert.Equal("", unlabeled.DetailText);

        unlabeled.Label = "Later";

        Assert.Equal("Later", unlabeled.DisplayName);
        Assert.Equal("1234...cdef", unlabeled.DetailText);
    }

    [Fact]
    public void ProviderAccountViewModel_LabelChange_NotifiesDisplayProperties()
    {
        var vm = new ProviderAccountViewModel("local", "1234567890abcdef", "", false);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Label = "Later";

        Assert.Contains(nameof(ProviderAccountViewModel.DisplayName), changed);
        Assert.Contains(nameof(ProviderAccountViewModel.DetailText), changed);
    }

    [Fact]
    public void ProviderViewModel_CustomProviderDetailText_ShowsBaseUrl()
    {
        var vm = new ProviderViewModel("local", "Local", PackIconSimpleIconsKind.OpenAi, "#000000", "Description", supportsApiKey: true)
        {
            ApiKeyBaseUrl = "https://local.example/v1",
            IsCustomProvider = true
        };

        vm.Accounts.Add(new ProviderAccountViewModel("local", "key1", "Active", isDisabled: false));
        vm.RefreshAccountCount();

        Assert.Equal("1 API key", vm.ConnectedSubText);
        Assert.Equal("https://local.example/v1", vm.ProviderDetailText);
    }

    [Fact]
    public void ProviderViewModel_Commands_RaiseAddAndToggleExpandEvents()
    {
        var vm = new ProviderViewModel("local", "Local", PackIconSimpleIconsKind.OpenAi, "#000000", "Description");
        vm.Accounts.Add(new ProviderAccountViewModel("local", "key", "Primary", isDisabled: false));
        var addRaised = false;
        var expandEvents = new List<bool>();
        vm.AddAccountRequested += (_, _) => addRaised = true;
        vm.IsExpandedChanged += (_, expanded) => expandEvents.Add(expanded);

        vm.RequestAddAccountCommand.Execute(null);
        vm.ToggleExpandCommand.Execute(null);
        vm.ToggleExpandCommand.Execute(null);

        Assert.True(addRaised);
        Assert.Equal([true, false], expandEvents);
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void ProviderViewModel_ToggleExpandWithoutAccounts_DoesNothing()
    {
        var vm = new ProviderViewModel("local", "Local", PackIconSimpleIconsKind.OpenAi, "#000000", "Description");
        var raised = false;
        vm.IsExpandedChanged += (_, _) => raised = true;

        vm.ToggleExpandCommand.Execute(null);

        Assert.False(vm.IsExpanded);
        Assert.False(raised);
    }
}
