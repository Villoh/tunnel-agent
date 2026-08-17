using System.Net;
using System.Net.Sockets;
using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine;
using TunnelAgent.Infrastructure.Engine.NineRouter;

namespace TunnelAgent.Tests;

public sealed class NineRouterProcessServiceTests
{
    [Fact]
    public void Constructor_InitialState_IsStopped()
    {
        var service = new ProcessService();

        Assert.False(service.IsRunning);
        Assert.Equal(EngineState.Stopped, service.State);
        Assert.Equal(0, service.Port);
        Assert.Null(service.LastError);
        Assert.Equal(EngineErrorKind.None, service.LastErrorKind);
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_DoesNotThrowAndSetsStopped()
    {
        var service = new ProcessService();

        await service.StopAsync();
        await service.StopAsync();

        Assert.Equal(EngineState.Stopped, service.State);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_PortInUse_SetsPortInUseAndError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var temp = new TestTempDirectory();
            var serverPath = CreateServerEntry(temp);
            var service = new ProcessService();

            await service.StartAsync(temp.File("unused-node"), serverPath, port);

            Assert.Equal(EngineState.Error, service.State);
            Assert.Equal(EngineErrorKind.PortInUse, service.LastErrorKind);
            Assert.False(service.IsRunning);
            Assert.Equal(port, service.Port);
            Assert.Contains(port.ToString(), service.LastError);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void CreateStartInfo_NodeServer_SetsArgsEnvAndWorkingDirectory()
    {
        using var temp = new TestTempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var serverPath = Path.Combine(appDir, "custom-server.js");
        var nodePath = Path.Combine(temp.Path, "node");

        var startInfo = ProcessService.CreateStartInfo(nodePath, serverPath, 20128);

        Assert.Equal(nodePath, startInfo.FileName);
        Assert.Equal(
            new[] { "--dns-result-order=ipv4first", "--max-old-space-size=6144", serverPath },
            startInfo.ArgumentList.ToArray());
        Assert.Equal(appDir, startInfo.WorkingDirectory);
        Assert.Equal("20128", startInfo.Environment["PORT"]);
        Assert.Equal("127.0.0.1", startInfo.Environment["HOSTNAME"]);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(
            startInfo.Environment.ContainsKey("PATH") || startInfo.Environment.ContainsKey("Path"),
            "Current process environment should be inherited.");
    }

    [Fact]
    public async Task StartAsync_ReapsRecordedLeftoverProcess()
    {
        using var temp = new TestTempDirectory();
        var serverPath = CreateServerEntry(temp);
        var pidPath = EnginePidFile.PathForServerEntry(serverPath);
        var port = GetFreePort();
        var missingNode = temp.File(OperatingSystem.IsWindows() ? "nonexistent-node.exe" : "nonexistent-node");

        using var leftover = EnginePidFileTests.StartHangProcess();
        try
        {
            EnginePidFile.Write(pidPath, leftover, serverPath);
            var service = new ProcessService();

            await service.StartAsync(missingNode, serverPath, port);

            leftover.WaitForExit(2000);
            leftover.Refresh();
            Assert.True(leftover.HasExited);
            Assert.False(File.Exists(pidPath));
            Assert.Equal(EngineState.Error, service.State);
            Assert.Equal(EngineErrorKind.LaunchFailed, service.LastErrorKind);
        }
        finally
        {
            EnginePidFileTests.TryKill(leftover);
        }
    }

    [Fact]
    public async Task StartAsync_NonexistentNodeExecutable_SetsLaunchFailed()
    {
        using var temp = new TestTempDirectory();
        var serverPath = CreateServerEntry(temp);
        var missingNode = temp.File(OperatingSystem.IsWindows() ? "nonexistent-node.exe" : "nonexistent-node");
        var service = new ProcessService();
        var port = GetFreePort();

        await service.StartAsync(missingNode, serverPath, port);

        Assert.Equal(EngineState.Error, service.State);
        Assert.Equal(EngineErrorKind.LaunchFailed, service.LastErrorKind);
        Assert.False(service.IsRunning);
        Assert.Equal(port, service.Port);
        Assert.Contains("Failed to launch engine", service.LastError);
    }

    private static string CreateServerEntry(TestTempDirectory temp)
    {
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var serverPath = Path.Combine(appDir, "custom-server.js");
        File.WriteAllText(serverPath, "// stub");
        return serverPath;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
