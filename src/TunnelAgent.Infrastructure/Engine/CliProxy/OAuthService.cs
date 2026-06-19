using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// Launches the CLIProxyAPI binary in OAuth login mode for a given provider.
/// The binary opens the browser, completes the OAuth flow, and writes a token file
/// to the auth-dir. The AuthFileWatcher detects the new file and updates Connected state.
/// </summary>
public sealed class OAuthService : IDisposable
{
    /// <summary>Maps provider ID → CLI login flag (without leading dash).</summary>
    private static readonly IReadOnlyDictionary<string, string> LoginFlags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"]          = "claude-login",
            ["codex"]           = "codex-login",
            ["kimi"]            = "kimi-login",
            ["antigravity"]     = "antigravity-login",
            ["xai"]             = "xai-login",
        };

    public static bool IsOAuthProvider(string providerId) =>
        LoginFlags.ContainsKey(providerId);

    private readonly ConfigService _config;
    private Process? _authProcess;
    private readonly Lock _lock = new();

    // Codex needs a keepalive newline ~12s in
    private static readonly TimeSpan CodexKeepaliveDelay = TimeSpan.FromSeconds(12);

    public OAuthService(ConfigService config) => _config = config;

    /// <summary>
    /// Starts the OAuth flow for the given provider.
    /// Returns immediately after the browser is expected to open.
    /// The AuthFileWatcher handles the completion detection.
    /// </summary>
    /// <returns>A user-facing status message.</returns>
    public async Task<(bool Success, string Message)> ConnectAsync(string providerId)
    {
        if (!LoginFlags.TryGetValue(providerId, out var flag))
            return (false, $"Provider '{providerId}' does not support OAuth login.");

        var binaryPath = DownloadService.BinaryPath;
        if (!File.Exists(binaryPath))
            return (false, "CLIProxyAPI binary is not installed yet. Start the server first.");

        // Kill any previously running auth process for this session
        CancelPreviousAuth();

        var configPath = _config.ConfigPath;
        if (!File.Exists(configPath))
            await _config.WriteConfigAsync();

        var psi = new ProcessStartInfo(binaryPath)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add($"-{flag}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var outputBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) outputBuilder.AppendLine(e.Data);
        };

        lock (_lock) { _authProcess = process; }

        process.Exited += (_, _) =>
        {
            lock (_lock) { if (_authProcess == process) _authProcess = null; }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start authentication: {ex.Message}");
        }

        // Provider-specific stdin automation
        if (providerId == "codex")
            _ = SendDelayedNewlineAsync(process, CodexKeepaliveDelay);

        // Wait up to 2s to check the process is alive and capture initial output
        await Task.Delay(1000);

        if (!process.HasExited)
            return (true, "Browser opened for authentication.\n\nComplete the login in your browser. The app will detect when you're authenticated automatically.");

        // Process exited quickly — check output
        var earlyOutput = outputBuilder.ToString();
        if (earlyOutput.Contains("Opening browser") || earlyOutput.Contains("Attempting to open URL"))
            return (true, "Browser opened for authentication. The app will detect when you're authenticated.");

        return (false, string.IsNullOrWhiteSpace(earlyOutput)
            ? "Authentication process failed unexpectedly."
            : earlyOutput.Trim());
    }

    /// <summary>Kills any active auth process (e.g. when user clicks Disconnect or starts a new auth).</summary>
    public void CancelPreviousAuth()
    {
        Process? prev;
        lock (_lock)
        {
            prev = _authProcess;
            _authProcess = null;
        }

        if (prev is null) return;
        try
        {
            if (!prev.HasExited)
                prev.Kill(entireProcessTree: true);
            prev.Dispose();
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => CancelPreviousAuth();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string ProviderDisplayName(string providerId) => providerId switch
    {
        "claude"         => "Claude Code",
        "codex"          => "OpenAI Codex",
        "kimi"           => "Kimi",
        "antigravity"    => "Antigravity",
        "xai"            => "xAI",
        _                => providerId,
    };

    private static async Task SendDelayedNewlineAsync(Process process, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            if (!process.HasExited)
                await process.StandardInput.WriteLineAsync();
        }
        catch { /* ignore — process may have exited */ }
    }
}
