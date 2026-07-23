using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Core.Skills;

namespace TunnelAgent.Infrastructure.Skills;

public sealed class AsmCliService(AsmProvisionService provision)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Task<IReadOnlyList<SkillSummary>> ListInstalledAsync(string scope, CancellationToken ct = default) =>
        RunJsonAsync<IReadOnlyList<SkillSummary>>(["list", "-p", "agents", "-s", scope, "--json"], ct);

    public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, string scope, CancellationToken ct = default) =>
        RunJsonAsync<IReadOnlyList<SkillSummary>>(["search", query, "-p", "agents", "-s", scope, "--json"], ct);

    public Task<SkillDetail> InspectAsync(string skill, string scope, CancellationToken ct = default) =>
        RunJsonAsync<SkillDetail>(["inspect", skill, "-s", scope, "--json"], ct);

    public Task<SkillAudit> AuditSecurityAsync(string source, CancellationToken ct = default) =>
        RunJsonAsync<SkillAudit>(["audit", "security", source, "--json"], ct);

    public Task<InstallResult> InstallAsync(string source, string scope, CancellationToken ct = default) =>
        RunJsonAsync<InstallResult>(["install", source, "-p", "agents", "-s", scope, "--yes", "--json"], ct);

    public async Task<CommandResult> UninstallAsync(string skill, string scope, CancellationToken ct = default) =>
        new(true, await RunAsync(["uninstall", skill, "-p", "agents", "-s", scope, "--yes"], ct).ConfigureAwait(false));

    public async Task<CommandResult> AuditDedupeAsync(string scope, CancellationToken ct = default) =>
        new(true, await RunAsync(["audit", "-s", scope, "--yes", "--json"], ct).ConfigureAwait(false));

    public async Task<CommandResult> BundleInstallAsync(string bundle, string scope, CancellationToken ct = default) =>
        new(true, await RunAsync(["bundle", "install", bundle, "-p", "agents", "-s", scope, "--yes", "--json"], ct).ConfigureAwait(false));

    public async Task<string> BinaryVersionAsync(CancellationToken ct = default) => (await RunAsync(["--version"], ct).ConfigureAwait(false)).Trim();

    internal static T ParseJson<T>(string output)
    {
        for (var index = output.Length - 1; index >= 0; index--)
        {
            if (output[index] is not ('{' or '[')) continue;
            try
            {
                var value = JsonSerializer.Deserialize<T>(output[index..], JsonOptions);
                if (value is not null) return value;
            }
            catch (JsonException) { }
        }
        throw new AsmCliException("ASM output did not contain valid JSON.");
    }

    private async Task<T> RunJsonAsync<T>(IReadOnlyList<string> args, CancellationToken ct) =>
        ParseJson<T>(await RunAsync(args, ct).ConfigureAwait(false));

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!provision.IsAsmInstalled()) throw new AsmCliException("ASM is not installed.");
        if (string.IsNullOrWhiteSpace(provision.NodePath)) throw new AsmCliException("Run prerequisite check before invoking ASM.");

        var psi = new ProcessStartInfo(provision.NodePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(provision.EntryPointPath);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new AsmCliException("Failed to start ASM.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new AsmCliException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim(), process.ExitCode);
        return output;
    }

}

public sealed class AsmCliException(string message, int? exitCode = null) : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}
