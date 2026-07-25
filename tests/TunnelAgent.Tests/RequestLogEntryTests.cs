using TunnelAgent.ViewModels;

namespace TunnelAgent.Tests;

public class RequestLogEntryTests
{
    [Fact]
    public void ParsesBackendApiCodexPath_AsCodex()
    {
        var line = "[2026-06-04 11:35:10] [5e01a728] [info ] [gin_logger.go:101] 200 |       14.832s |       127.0.0.1 | POST    \"/backend-api/codex/responses\"";
        var entry = RequestLogEntry.TryParse(line);

        Assert.NotNull(entry);
        Assert.Equal("Codex", entry!.Provider);
    }

    [Fact]
    public void ParsesUnknownBackendApiPath_AsOpenAiCompletions()
    {
        var line = "[2026-06-04 11:35:10] [5e01a728] [info ] [gin_logger.go:101] 200 |       14.832s |       127.0.0.1 | POST    \"/backend-api/other\"";
        var entry = RequestLogEntry.TryParse(line);

        Assert.NotNull(entry);
        Assert.Equal("OpenAI Completions", entry!.Provider);
    }
}
