using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

public enum GeminiLoginStage
{
    WaitingForOAuth,
    ModeSelection,
    ProjectSelection,
    Success,
    Failed
}

public sealed record GeminiProject(int Index, string Id, string? Label);

public sealed record GeminiLoginUpdate(
    GeminiLoginStage Stage,
    string Prompt,
    IReadOnlyList<GeminiProject>? Projects = null,
    string? Detail = null);

/// <summary>
/// Runs <c>cli-proxy-api --config ... -login</c> and drives the interactive
/// Gemini authentication flow: OAuth → mode selection → project selection.
/// </summary>
public sealed class GeminiLoginService : IAsyncDisposable
{
    // How long to poll before giving up at each stage
    private static readonly TimeSpan OAuthTimeout        = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ModeSelectionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProjectTimeout      = TimeSpan.FromSeconds(30);

    private const int PollIntervalMs  = 100;
    private const int MaxBufferChars  = 256 * 1024;

    private Process?       _process;
    private Task?          _pumpTask;
    private readonly StringBuilder _buffer = new();
    private int            _readIndex;

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the CLI and waits until the browser is about to open.
    /// Returns <see cref="GeminiLoginStage.WaitingForOAuth"/> on success.
    /// </summary>
    public async Task<GeminiLoginUpdate> StartAsync(string binaryPath, string configPath, CancellationToken ct = default)
    {
        await DisposeAsync(); // kill any previous run

        if (!File.Exists(binaryPath))
            return Failed("CLIProxyAPI binary is not installed.");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo(binaryPath)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            }
        };
        _process.StartInfo.ArgumentList.Add("--config");
        _process.StartInfo.ArgumentList.Add(configPath);
        _process.StartInfo.ArgumentList.Add("-login");

        _buffer.Clear();
        _readIndex = 0;

        _process.Start();

        _pumpTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    PumpReaderAsync(_process.StandardOutput, ct),
                    PumpReaderAsync(_process.StandardError,  ct));
            }
            catch { }
        }, ct);

        // Wait until browser is about to open (fast — happens within ~1s)
        return await PollAsync(TimeSpan.FromSeconds(10), ct, snapshot =>
        {
            var clean = StripAnsi(snapshot);
            if (clean.Contains("Opening browser", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("Attempting to open URL", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("Waiting for authentication", StringComparison.OrdinalIgnoreCase))
                return new GeminiLoginUpdate(GeminiLoginStage.WaitingForOAuth,
                    "Browser opened. Complete the Google sign-in, then return here.");
            return null;
        },
        fallback: new GeminiLoginUpdate(GeminiLoginStage.WaitingForOAuth,
            "Browser opened. Complete the Google sign-in, then return here."));
    }

    /// <summary>
    /// Waits (up to 5 min) for the mode selection prompt to appear after OAuth completes.
    /// Returns <see cref="GeminiLoginStage.ModeSelection"/>.
    /// </summary>
    public Task<GeminiLoginUpdate> WaitForModeSelectionAsync(CancellationToken ct = default) =>
        PollAsync(ModeSelectionTimeout, ct, snapshot =>
        {
            var clean = StripAnsi(snapshot);
            if (clean.Contains("Enter choice", StringComparison.OrdinalIgnoreCase) ||
                clean.Contains("Select login mode", StringComparison.OrdinalIgnoreCase))
                return new GeminiLoginUpdate(GeminiLoginStage.ModeSelection,
                    "Choose the authentication mode.");
            return null;
        });

    /// <summary>
    /// Sends the mode choice (<c>1</c> = Code Assist, <c>2</c> = Google One) and waits
    /// for the next stage: <see cref="GeminiLoginStage.ProjectSelection"/> or
    /// <see cref="GeminiLoginStage.Success"/>.
    /// </summary>
    public async Task<GeminiLoginUpdate> SelectModeAsync(int mode, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return Failed("Login process is not running.");

        await _process.StandardInput.WriteLineAsync(mode.ToString());
        await _process.StandardInput.FlushAsync();

        // Google One (mode 2) goes straight to success — same timeout as OAuth
        if (mode == 2)
            return await PollAsync(OAuthTimeout, ct, DetectSuccessOrProjects);

        // Code Assist (mode 1) shows project list before success
        return await PollAsync(ModeSelectionTimeout, ct, DetectSuccessOrProjects);
    }

    /// <summary>
    /// Sends the selected project ID and waits for
    /// <see cref="GeminiLoginStage.Success"/>.
    /// </summary>
    public async Task<GeminiLoginUpdate> SelectProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return Failed("Login process is not running.");

        await _process.StandardInput.WriteLineAsync(projectId.Trim());
        await _process.StandardInput.FlushAsync();

        return await PollAsync(ProjectTimeout, ct, snapshot =>
        {
            var clean = StripAnsi(snapshot);
            if (IsSuccess(clean))
                return new GeminiLoginUpdate(GeminiLoginStage.Success, "Gemini account connected successfully.");
            return null;
        });
    }

    // ── detection helpers ─────────────────────────────────────────────────────

    private GeminiLoginUpdate? DetectSuccessOrProjects(string snapshot)
    {
        var clean = StripAnsi(snapshot);
        var delta = clean.Length > _readIndex ? clean[_readIndex..] : clean;

        if (IsSuccess(clean))
            return new GeminiLoginUpdate(GeminiLoginStage.Success, "Gemini account connected successfully.");

        // Project list appears after "Available Google Cloud projects:"
        if (delta.Contains("Enter project ID", StringComparison.OrdinalIgnoreCase) ||
            delta.Contains("Type 'ALL'", StringComparison.OrdinalIgnoreCase))
        {
            var projects = ParseProjects(delta);
            return new GeminiLoginUpdate(GeminiLoginStage.ProjectSelection,
                "Select a Google Cloud project.", Projects: projects);
        }

        return null;
    }

    private static bool IsSuccess(string clean) =>
        clean.Contains("Gemini authentication successful", StringComparison.OrdinalIgnoreCase) &&
        clean.Contains("Authentication saved", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GeminiProject> ParseProjects(string text)
    {
        // Matches lines like: [1] gen-lang-client (Personal)
        var regex = new Regex(@"\[(\d+)\]\s+(\S+)(?:\s+\(([^)]+)\))?", RegexOptions.Multiline);
        var projects = new List<GeminiProject>();
        foreach (Match m in regex.Matches(text))
        {
            var index = int.Parse(m.Groups[1].Value);
            var id    = m.Groups[2].Value;
            var label = m.Groups[3].Success ? m.Groups[3].Value : null;
            projects.Add(new GeminiProject(index, id, label));
        }
        return projects;
    }

    // ── poll loop ─────────────────────────────────────────────────────────────

    private async Task<GeminiLoginUpdate> PollAsync(
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, GeminiLoginUpdate?> detect,
        GeminiLoginUpdate? fallback = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            string snapshot;
            lock (_buffer) snapshot = _buffer.ToString();

            var update = detect(snapshot);
            if (update is not null)
            {
                _readIndex = StripAnsi(snapshot).Length;
                return update;
            }

            if (_process is { HasExited: true })
            {
                var tail = StripAnsi(snapshot).Trim();
                return Failed("Login process exited unexpectedly.", LastLines(tail));
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        string timedOut;
        lock (_buffer) timedOut = _buffer.ToString();
        return Failed("Timed out waiting for a response.", LastLines(StripAnsi(timedOut)));
    }

    // ── stdout/stderr pump ────────────────────────────────────────────────────

    private async Task PumpReaderAsync(TextReader reader, CancellationToken ct)
    {
        var buf = new char[512];
        while (true)
        {
            var read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (read <= 0) break;
            lock (_buffer)
            {
                _buffer.Append(buf, 0, read);
                if (_buffer.Length > MaxBufferChars)
                {
                    var remove = _buffer.Length - MaxBufferChars;
                    _buffer.Remove(0, remove);
                    _readIndex = Math.Max(0, _readIndex - remove);
                }
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static GeminiLoginUpdate Failed(string prompt, string? detail = null) =>
        new(GeminiLoginStage.Failed, prompt, Detail: detail);

    private static string StripAnsi(string text) =>
        Regex.Replace(text, @"\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b\][^\u0007]*(?:\u0007|\u001b\\)", string.Empty);

    private static string LastLines(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Length <= 6 ? lines : lines[^6..]);
    }

    // ── disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        finally
        {
            if (_pumpTask is not null)
            {
                try { await _pumpTask; } catch { }
                _pumpTask = null;
            }
            _process?.Dispose();
            _process = null;
        }
    }
}
