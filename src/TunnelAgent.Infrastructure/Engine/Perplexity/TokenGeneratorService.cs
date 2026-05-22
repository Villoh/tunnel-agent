using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Infrastructure.Engine.Perplexity;

public enum TokenFlowStage
{
    Email,
    Verification,
    Totp,
    Success,
    Failed
}

public sealed record TokenFlowUpdate(TokenFlowStage Stage, string Prompt, string? Token = null, string? Detail = null);

/// <summary>Runs `perplexity-webui-scraper token` with redirected stdin/stdout.</summary>
public sealed class TokenGeneratorService : IAsyncDisposable
{
    private Process? _process;
    private Task? _pumpTask;
    private readonly StringBuilder _buffer = new();
    private int _readIndex;

    public async Task<TokenFlowUpdate> StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(DownloadService.BinaryPath))
            return new(TokenFlowStage.Failed, "Perplexity engine is not installed.");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo(DownloadService.BinaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        _process.StartInfo.ArgumentList.Add("token");
        _process.Start();
        _buffer.Clear();
        _readIndex = 0;

        _pumpTask = Task.Run(async () =>
        {
            try
            {
                var stdout = _process.StandardOutput;
                var stderr = _process.StandardError;
                var stdOutTask = PumpReaderAsync(stdout, ct);
                var stdErrTask = PumpReaderAsync(stderr, ct);
                await Task.WhenAll(stdOutTask, stdErrTask);
            }
            catch { }
        }, ct);

        return await WaitForNextUpdateAsync(ct);
    }

    public async Task<TokenFlowUpdate> SubmitAsync(string input, TokenFlowStage submittedStage = TokenFlowStage.Email, CancellationToken ct = default)
    {
        if (_process is null || _process.HasExited)
            return new(TokenFlowStage.Failed, "Token generator is not running.");

        await _process.StandardInput.WriteLineAsync(input);
        await _process.StandardInput.FlushAsync();
        return await WaitForNextUpdateAsync(ct, submittedStage);
    }

    private async Task<TokenFlowUpdate> WaitForNextUpdateAsync(CancellationToken ct, TokenFlowStage? submittedStage = null)
    {
        for (var i = 0; i < 900; i++)
        {
            ct.ThrowIfCancellationRequested();
            string snapshot;
            lock (_buffer) snapshot = _buffer.ToString();

            if (TryBuildUpdate(snapshot, out var update) && IsExpectedUpdateAfterSubmit(update!, submittedStage))
            {
                _readIndex = snapshot.Length;
                return update!;
            }

            if (_process is { HasExited: true })
            {
                var tail = snapshot.Trim();
                return new(TokenFlowStage.Failed, "Token generation failed.", Detail: LastLines(tail));
            }

            await Task.Delay(100, ct);
        }

        string timedOutSnapshot;
        lock (_buffer) timedOutSnapshot = _buffer.ToString();
        return new(TokenFlowStage.Failed, "Timed out waiting for token generator.", Detail: LastLines(timedOutSnapshot));
    }

    private static bool IsExpectedUpdateAfterSubmit(TokenFlowUpdate update, TokenFlowStage? submittedStage) =>
        submittedStage switch
        {
            TokenFlowStage.Verification => update.Stage is TokenFlowStage.Success or TokenFlowStage.Totp or TokenFlowStage.Failed,
            TokenFlowStage.Totp => update.Stage is TokenFlowStage.Success or TokenFlowStage.Failed,
            _ => true
        };

    private bool TryBuildUpdate(string snapshot, out TokenFlowUpdate? update)
    {
        var delta = snapshot.Length > _readIndex ? snapshot[_readIndex..] : snapshot;
        var cleanSnapshot = StripAnsi(snapshot);
        var cleanDelta = StripAnsi(delta);

        // 1. Success — always check full snapshot so token is findable anywhere
        const string successMarker = "Your session token:";
        var tokenIndex = cleanSnapshot.IndexOf(successMarker, StringComparison.OrdinalIgnoreCase);
        if (tokenIndex >= 0)
        {
            var tokenText = cleanSnapshot[(tokenIndex + successMarker.Length)..].Trim();
            var token = tokenText.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(token))
            {
                update = new(TokenFlowStage.Success, "Token generated successfully.", token);
                return true;
            }
        }

        // 2. Error markers
        if (cleanDelta.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            cleanDelta.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            cleanDelta.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            cleanDelta.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            update = new(TokenFlowStage.Failed, "Token generation failed.", Detail: LastLines(cleanDelta));
            return true;
        }

        // 3. TOTP — returned after email verification when account has two-factor auth enabled
        if (cleanDelta.Contains("Enter TOTP code", StringComparison.OrdinalIgnoreCase))
        {
            update = new(TokenFlowStage.Totp, "Enter two-factor authentication code",
                Detail: "Use the 6-digit code from your authenticator app.");
            return true;
        }

        // 4. Verification — check BEFORE email so echoed email input doesn't false-trigger email stage
        if (cleanDelta.Contains("Enter code or paste link", StringComparison.OrdinalIgnoreCase))
        {
            update = new(TokenFlowStage.Verification, "Enter 6-digit code or paste magic link",
                Detail: "Check your email for Perplexity verification message.");
            return true;
        }

        // 5. Email — only if we haven't already seen it (_readIndex == 0 means fresh start)
        if (_readIndex == 0 && cleanDelta.Contains("Enter your Perplexity email", StringComparison.OrdinalIgnoreCase))
        {
            update = new(TokenFlowStage.Email, "Enter your Perplexity email");
            return true;
        }

        update = null;
        return false;
    }

    private static string StripAnsi(string text) =>
        Regex.Replace(text, "\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007]*(?:\u0007|\u001b\\\\)", string.Empty);

    private async Task PumpReaderAsync(TextReader reader, CancellationToken ct)
    {
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;
            lock (_buffer) _buffer.Append(buffer, 0, read);
        }
    }

    private static string LastLines(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Length <= 6 ? lines : lines[^6..]);
    }

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
