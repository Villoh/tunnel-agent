namespace TunnelAgent.Tests;

public sealed class TestTempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "TunnelAgent.Tests",
        Guid.NewGuid().ToString("N"));

    public TestTempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; file watchers may still release handles on Windows.
        }
    }
}
