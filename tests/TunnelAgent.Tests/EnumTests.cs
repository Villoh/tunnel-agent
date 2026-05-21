using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Tests;

public sealed class EnumTests
{
    [Fact]
    public void ServerState_HasAllExpectedValues()
    {
        var values = Enum.GetValues<ServerState>();
        Assert.Equal(4, values.Length);
        Assert.Contains(ServerState.Stopped, values);
        Assert.Contains(ServerState.Starting, values);
        Assert.Contains(ServerState.Running, values);
        Assert.Contains(ServerState.Error, values);
    }

    [Fact]
    public void SectionKey_HasAllExpectedValues()
    {
        var values = Enum.GetValues<SectionKey>();
        Assert.Contains(SectionKey.Providers, values);
        Assert.Contains(SectionKey.Agents, values);
        Assert.Contains(SectionKey.Configuration, values);
        Assert.Contains(SectionKey.ConfigGeneral, values);
        Assert.Contains(SectionKey.ConfigCliProxy, values);
        Assert.Contains(SectionKey.ConfigPerplexity, values);
    }

    [Fact]
    public void EngineState_HasAllExpectedValues()
    {
        var values = Enum.GetValues<EngineState>();
        Assert.Equal(7, values.Length);
        Assert.Contains(EngineState.NotInstalled, values);
        Assert.Contains(EngineState.Downloading, values);
        Assert.Contains(EngineState.Installing, values);
        Assert.Contains(EngineState.Stopped, values);
        Assert.Contains(EngineState.Starting, values);
        Assert.Contains(EngineState.Running, values);
        Assert.Contains(EngineState.Error, values);
    }

    [Fact]
    public void RoutingStrategy_HasAllExpectedValues()
    {
        var values = Enum.GetValues<RoutingStrategy>();
        Assert.Equal(2, values.Length);
        Assert.Contains(RoutingStrategy.RoundRobin, values);
        Assert.Contains(RoutingStrategy.FillFirst, values);
    }

    [Theory]
    [InlineData(ServerState.Stopped, "Stopped")]
    [InlineData(ServerState.Starting, "Starting")]
    [InlineData(ServerState.Running, "Running")]
    [InlineData(ServerState.Error, "Error")]
    public void ServerState_ToString_ReturnsExpectedName(ServerState state, string expected)
    {
        Assert.Equal(expected, state.ToString());
    }

    [Theory]
    [InlineData(EngineState.NotInstalled, "NotInstalled")]
    [InlineData(EngineState.Downloading, "Downloading")]
    [InlineData(EngineState.Installing, "Installing")]
    [InlineData(EngineState.Stopped, "Stopped")]
    [InlineData(EngineState.Starting, "Starting")]
    [InlineData(EngineState.Running, "Running")]
    [InlineData(EngineState.Error, "Error")]
    public void EngineState_ToString_ReturnsExpectedName(EngineState state, string expected)
    {
        Assert.Equal(expected, state.ToString());
    }

    [Theory]
    [InlineData(RoutingStrategy.RoundRobin, "RoundRobin")]
    [InlineData(RoutingStrategy.FillFirst, "FillFirst")]
    public void RoutingStrategy_ToString_ReturnsExpectedName(RoutingStrategy strategy, string expected)
    {
        Assert.Equal(expected, strategy.ToString());
    }
}
