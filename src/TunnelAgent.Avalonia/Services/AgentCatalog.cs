using System;
using System.Collections.Generic;

namespace TunnelAgent.Services;

public sealed record AgentDefinition(
    string Id,
    string DisplayName,
    string Description,
    string[] BinaryNames,
    string[] ConfigPaths,
    string? DocsUrl,
    string AccentHex,
    string? IconAssetPath = null,
    string? ConfiguredEnvVar = null,
    bool IconNeedsDarkBg = false);

public static class AgentCatalog
{
    private const string AssetsBase = "/Assets/agents/";

    public static readonly IReadOnlyList<AgentDefinition> All = new[]
    {
        new AgentDefinition(
            "claude-code", "Claude Code",
            "Anthropic's official CLI coding agent.",
            new[] { "claude", "claude.cmd", "claude.exe" },
            new[] { "~/.claude/settings.json" },
            "https://docs.anthropic.com/en/docs/claude-code",
            "#CC785C",
            AssetsBase + "claude-code.svg"),

        new AgentDefinition(
            "codex", "Codex CLI",
            "OpenAI Codex command-line interface.",
            new[] { "codex", "codex.cmd", "codex.exe" },
            new[] { "~/.codex/config.toml" },
            "https://github.com/openai/codex",
            "#10A37F",
            AssetsBase + "codex.svg"),

        new AgentDefinition(
            "gemini-cli", "Gemini CLI",
            "Google Gemini CLI coding agent.",
            new[] { "gemini", "gemini.cmd", "gemini.exe" },
            Array.Empty<string>(),
            "https://github.com/google-gemini/gemini-cli",
            "#4285F4",
            AssetsBase + "gemini-cli.svg",
            ConfiguredEnvVar: "GOOGLE_GEMINI_BASE_URL"),

        new AgentDefinition(
            "amp", "Amp CLI",
            "Sourcegraph's Amp coding assistant.",
            new[] { "amp", "amp.cmd", "amp.exe" },
            new[] { "~/.config/amp/settings.json", "~/.local/share/amp/secrets.json" },
            "https://ampcode.com",
            "#FF5543",
            AssetsBase + "amp.svg"),

        new AgentDefinition(
            "opencode", "OpenCode",
            "Open-source AI coding agent.",
            new[] { "opencode", "oc", "opencode.exe", "oc.exe" },
            new[] { "~/.config/opencode/opencode.json" },
            "https://opencode.ai",
            "#8B5CF6",
            AssetsBase + "opencode.svg",
            IconNeedsDarkBg: true),

        new AgentDefinition(
            "cursor-agent", "Cursor Agent",
            "Cursor's AI agent mode for code editing.",
            new[] { "cursor-agent", "cursor" },
            Array.Empty<string>(),
            "https://cursor.sh",
            "#1D9BF0",
            AssetsBase + "cursor-agent.svg",
            IconNeedsDarkBg: true),

        new AgentDefinition(
            "aider", "Aider",
            "AI pair programming in the terminal. Install via pip.",
            new[] { "aider", "aider.cmd", "aider.exe" },
            Array.Empty<string>(),
            "https://aider.chat",
            "#E5A731"),

        new AgentDefinition(
            "pi", "Pi",
            "Pi coding agent by pi.dev.",
            new[] { "pi", "pi.exe", "pi.cmd" },
            new[] { "~/.pi/agent/models.json" },
            "https://pi.dev/docs/latest/models",
            "#FFFFFF",
            AssetsBase + "pi.svg",
            IconNeedsDarkBg: true),

        new AgentDefinition(
            "factory-droid", "Factory Droid",
            "Factory's AI coding agent.",
            new[] { "droid", "factory-droid" },
            new[] { "~/.factory/settings.json" },
            "https://docs.factory.ai",
            "#238636",
            AssetsBase + "factory-droid.svg",
            IconNeedsDarkBg: true),
    };
}
