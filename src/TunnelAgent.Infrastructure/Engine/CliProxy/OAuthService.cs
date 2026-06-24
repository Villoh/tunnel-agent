using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>Outcome of an OAuth connect attempt. The UI layer maps each status to a localized message.</summary>
public enum OAuthConnectStatus
{
    /// <summary>Browser flow started; AuthFileWatcher will detect completion.</summary>
    BrowserOpened,
    /// <summary>Browser flow started and a sign-in URL was captured as a manual fallback (<c>Detail</c> = URL).</summary>
    BrowserOpenedWithUrl,
    /// <summary>Provider does not support OAuth login (<c>Detail</c> = provider id).</summary>
    NotSupported,
    /// <summary>CLIProxyAPI binary is not installed yet.</summary>
    BinaryMissing,
    /// <summary>The login process could not be started (<c>Detail</c> = error message).</summary>
    StartFailed,
    /// <summary>The login process exited with a non-zero code (<c>Detail</c> = captured output).</summary>
    Failed,
    /// <summary>The login process exited with a non-zero code and produced no output.</summary>
    FailedUnexpected,
}

/// <summary>Structured result of <see cref="OAuthService.ConnectAsync"/>. <c>Detail</c> carries dynamic, non-localizable data.</summary>
public readonly record struct OAuthConnectResult(bool Success, OAuthConnectStatus Status, string Detail = "");

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
    public async Task<OAuthConnectResult> ConnectAsync(string providerId)
    {
        if (!LoginFlags.TryGetValue(providerId, out var flag))
            return new OAuthConnectResult(false, OAuthConnectStatus.NotSupported, providerId);

        var binaryPath = DownloadService.BinaryPath;
        if (!File.Exists(binaryPath))
            return new OAuthConnectResult(false, OAuthConnectStatus.BinaryMissing);

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

        // Capture the exit code from inside the handler so callers never touch a
        // disposed Process, and always dispose once it exits (a successful login
        // keeps the process alive until the user completes the flow).
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = process.ExitCode; } catch { /* already gone */ }
            exitTcs.TrySetResult(code);

            lock (_lock) { if (_authProcess == process) _authProcess = null; }
            try { process.Dispose(); } catch { /* idempotent */ }
        };

        lock (_lock) { _authProcess = process; }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return new OAuthConnectResult(false, OAuthConnectStatus.StartFailed, ex.Message);
        }

        // Provider-specific stdin automation
        if (providerId == "codex")
            _ = SendDelayedNewlineAsync(process, CodexKeepaliveDelay);

        // Give the process ~1s: a live process means the browser flow started and
        // the AuthFileWatcher will detect completion. A quick exit is judged by
        // exit code, never by parsing stdout (which changes between binary releases).
        var finished = await Task.WhenAny(exitTcs.Task, Task.Delay(1000));

        if (finished != exitTcs.Task)
        {
            // Still running: surface the sign-in URL as a fallback for headless
            // environments where the binary could not open a browser.
            var url = ExtractAuthUrl(outputBuilder.ToString());
            return string.IsNullOrEmpty(url)
                ? new OAuthConnectResult(true, OAuthConnectStatus.BrowserOpened)
                : new OAuthConnectResult(true, OAuthConnectStatus.BrowserOpenedWithUrl, url);
        }

        var exitCode = await exitTcs.Task;
        if (exitCode == 0)
            return new OAuthConnectResult(true, OAuthConnectStatus.BrowserOpened);

        var earlyOutput = outputBuilder.ToString().Trim();
        return string.IsNullOrWhiteSpace(earlyOutput)
            ? new OAuthConnectResult(false, OAuthConnectStatus.FailedUnexpected)
            : new OAuthConnectResult(false, OAuthConnectStatus.Failed, earlyOutput);
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

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s'""<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns the first http(s) URL found in the captured output, or empty when none.</summary>
    private static string ExtractAuthUrl(string output)
    {
        if (string.IsNullOrEmpty(output)) return "";
        var match = UrlPattern.Match(output);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']') : "";
    }

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
