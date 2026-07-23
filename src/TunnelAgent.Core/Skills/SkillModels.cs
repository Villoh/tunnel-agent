using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TunnelAgent.Core.Skills;

public enum AsmProvisionState
{
    CheckingPrerequisites,
    NotAvailable,
    NotInstalled,
    Installing,
    Ready,
    UpdateAvailable,
    Error
}

public sealed record AsmPrerequisiteStatus(
    string? NodePath,
    string? NodeVersion,
    string? NpmPath,
    string? NpmVersion,
    bool IsCompatible,
    string FailureReason);

public class SkillSummary
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Repo { get; set; }
    public string? InstallCommand { get; set; }
    public string? Status { get; set; }
    public string? Path { get; set; }
    public string? Scope { get; set; }
    public string? Provider { get; set; }
    public string? License { get; set; }
    public string? Effort { get; set; }
    public List<SkillWarning> Warnings { get; set; } = [];

    [JsonIgnore]
    public bool Installed => string.Equals(Status, "installed", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(Path);

    [JsonIgnore]
    public string Source
    {
        get
        {
            const string prefix = "asm install ";
            if (!string.IsNullOrWhiteSpace(InstallCommand) && InstallCommand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return InstallCommand[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(Repo) ? $"github:{Repo}" : Name;
        }
    }
}

public sealed class SkillDetail : SkillSummary
{
    public string? DirName { get; set; }
    public string? RealPath { get; set; }
    public int TokenCount { get; set; }
}

public sealed class SkillWarning
{
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class SkillAudit
{
    public DateTimeOffset? ScannedAt { get; set; }
    public string SkillName { get; set; } = "";
    public string SkillPath { get; set; } = "";
    public string Verdict { get; set; } = "unknown";
    public string VerdictReason { get; set; } = "";
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public List<SkillCodeScan> CodeScans { get; set; } = [];
    public List<SkillPermission> Permissions { get; set; } = [];
}

public sealed class SkillCodeScan
{
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public List<SkillAuditMatch> Matches { get; set; } = [];
}

public sealed class SkillAuditMatch
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Match { get; set; } = "";
    public string Severity { get; set; } = "";
}

public sealed class SkillPermission
{
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<SkillAuditMatch> Evidence { get; set; } = [];
}

public sealed class InstallResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed record CommandResult(bool Success, string Output);
