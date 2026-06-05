<p align="center">
  <img src="src/TunnelAgent.Avalonia/Assets/logo.png" width="128" height="128" alt="Tunnel Agent Icon">
</p>

<h1 align="center">Tunnel Agent</h1>

<p align="center">
  Desktop UI for <a href="https://github.com/router-for-me/CLIProxyAPI">CLIProxyAPI</a> and <a href="https://github.com/henrique-coder/perplexity-webui-scraper">Perplexity WebUI Scraper</a>. Manage OAuth providers, API keys, and Perplexity session accounts, then point any coding agent at the local endpoint.<br><br>One click to connect. Works with whatever you already have.
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-146CF9?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=opensourceinitiative"></a>
  <img alt="Platform" src="https://img.shields.io/badge/Platform-Windows-146CF9?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=windows">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-146CF9?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=dotnet">
  <img alt="Version" src="https://img.shields.io/github/v/tag/Villoh/tunnel-agent?label=Version&style=for-the-badge&color=146CF9&labelColor=ffffff">
</p>

<p align="center">
  <a href="https://github.com/Villoh/tunnel-agent/releases">Download</a>
  ·
  <a href="https://github.com/Villoh/tunnel-agent/issues">Issues</a>
  ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

---

> [!WARNING]
> This app has only been tested on **Windows**. It may require changes to work correctly on Linux or macOS. Contributions to add full support for both platforms are very welcome.

## Features

- 🖥️ **Native Windows Experience**: clean Avalonia UI that respects your system theme and keeps local engine management in one place
- 🔀 **Multi-Engine Control**: run and manage both CLIProxyAPI and Perplexity from the same desktop app, with per-engine configuration, endpoint controls, and status
- 🚀 **One-Click Server Management**: start and stop each local engine directly from the Providers view
- 🔐 **Credential Storage**: secure handling for OAuth tokens, custom provider API keys, and file-based Perplexity session accounts stored under Tunnel Agent settings
- 👥 **Provider Management**: connect Claude Code, OpenAI Codex, Gemini CLI, Kimi, Antigravity, and custom OpenAI-compatible providers
- 🧠 **Perplexity WebUI Sessions**: add multiple Perplexity accounts, set a default session, reset accounts safely, and auto-install the Perplexity engine when needed
- 🎚️ **Model Visibility**: browse available models grouped by connected provider or engine source, including Perplexity-backed model listings
- 📊 **Live Status**: see sidebar engine health plus focused engine state, endpoint, and release info
- ⚙️ **Configuration**: control startup behaviour, per-engine ports, updates, release selection, routing strategy, and credential actions

## Engine Overview

### [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)

CLIProxyAPI remains the main unified OpenAI-compatible local proxy for OAuth and upstream providers. Tunnel Agent lets you:

- install and update CLIProxyAPI releases
- switch to a pinned release instead of always following latest
- manage connected providers and custom OpenAI-compatible accounts
- inspect provider quotas and available models exposed by the running proxy

### [Perplexity WebUI Scraper](https://github.com/henrique-coder/perplexity-webui-scraper)

Tunnel Agent now includes first-class support for the Perplexity WebUI Scraper engine. You can:

- install and start Perplexity from the same UI
- manage Perplexity session token accounts separately from CLIProxy auth files
- store each Perplexity account as its own JSON file under the Tunnel Agent settings directory
- generate a Perplexity session token directly from the UI with a guided email/OTP/TOTP wizard
- edit account labels in-place from the account list
- view Perplexity endpoint status and available models from the Perplexity server
- keep Perplexity on a selected release instead of being forced to latest

## Supported Ecosystem

### AI Providers

| Provider                 | Auth method         |
| ------------------------ | ------------------- |
| Claude (Anthropic)       | OAuth               |
| Gemini CLI (Google)      | OAuth               |
| OpenAI Codex             | OAuth               |
| Kimi (Moonshot)          | OAuth               |
| Antigravity              | OAuth               |
| Perplexity               | WebUI session token |
| Custom OpenAI-compatible | API key + base URL  |

### IDE Quota Tracking (Monitor Only)

| IDE    | Description                                        |
| ------ | -------------------------------------------------- |
| Cursor | Auto-detected when installed and logged in         |
| Kiro   | Auto-detected when installed and logged in         |
| Trae   | Auto-detected when installed and logged in         |

> These IDEs are only used for quota usage monitoring. They cannot be used as providers for the proxy.

### Agents

| Agent           | Config method                  |
| --------------- | ------------------------------ |
| Claude Code     | `~/.claude/settings.json`      |
| Codex CLI       | `~/.codex/config.toml` + `auth.json` |
| Gemini CLI      | Environment variables          |
| Amp             | `~/.config/amp/settings.json` + `~/.local/share/amp/secrets.json` |
| OpenCode        | `~/.config/opencode/opencode.json` |
| Pi              | `~/.pi/agent/models.json`      |
| Factory Droid   | `~/.factory/settings.json`     |
| Aider           | Environment variables          |

## Installation

**Installer (recommended):** download `TunnelAgent-x.y.z-win-Setup.exe` from the [latest release](https://github.com/Villoh/tunnel-agent/releases/latest) and run it. Updates are applied automatically in the background.

**Portable:** download `TunnelAgent-x.y.z-win-x64-portable.zip`, extract it, and run `TunnelAgent.exe`. No installation required.

**Scoop:**
```powershell
scoop bucket add villoh https://github.com/Villoh/scoop-bucket
scoop install tunnel-agent
```

**Requirements:** Windows 10 or later.

## Usage

1. **Start the engine**: click the play button next to the endpoint. The status indicator turns green when the engine is running.
2. **Connect providers**: open the Providers tab for CLIProxyAPI, connect OAuth providers (Claude, Gemini CLI, Kimi…) or add custom API key accounts.
3. **Configure your agents**: open the Agents tab to auto-write or manually copy the proxy config for each agent. For unsupported clients, copy the endpoint (e.g. `http://127.0.0.1:8317/v1`) from the Providers header and configure it manually.
4. **Track quota**: open the Quota tab to see remaining quota for connected CLIProxyAPI providers and standalone IDE accounts (Kiro, Trae).

## Development

**Requirements:** [.NET SDK 10.0.203+](https://dotnet.microsoft.com/download)

Clone and run:

```pwsh
git clone https://github.com/Villoh/tunnel-agent.git
cd tunnel-agent
dotnet restore
dotnet run --project src/TunnelAgent.Avalonia/TunnelAgent.Avalonia.csproj
```

> If the app is already running, stop it before rebuilding; Windows locks `TunnelAgent.exe` while it is open.

## Credits

- [VibeProxy](https://github.com/automazeio/vibeproxy): original concept and inspiration
- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI): the unified proxy that powers multi-provider support
- [perplexity-webui-scraper](https://github.com/henrique-coder/perplexity-webui-scraper): Perplexity engine and session token CLI
- [Quotio](https://github.com/nguyenphutrong/quotio): reference implementation for quota tracking and IDE account detection

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). Do not open a public issue.

## License

MIT License, see [LICENSE](LICENSE) for details.
