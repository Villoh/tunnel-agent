using TunnelAgent.Infrastructure.Engine;

namespace TunnelAgent.Tests;

public sealed class KillOnCloseJobTests
{
    [Fact]
    public void TryCreate_WindowsReturnsJob_UnixReturnsNull()
    {
        var job = KillOnCloseJob.TryCreate();
        try
        {
            if (OperatingSystem.IsWindows())
                Assert.NotNull(job);
            else
                Assert.Null(job);
        }
        finally
        {
            job?.Dispose();
        }
    }

    [Fact]
    public void Dispose_OnWindows_KillsAssignedProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var process = EnginePidFileTests.StartHangProcess();
        try
        {
            using var job = KillOnCloseJob.TryCreate();
            Assert.NotNull(job);
            Assert.True(job!.TryAssign(process));
            job.Dispose();

            Assert.True(process.WaitForExit(3000));
            process.Refresh();
            Assert.True(process.HasExited);
        }
        finally
        {
            EnginePidFileTests.TryKill(process);
        }
    }
}
