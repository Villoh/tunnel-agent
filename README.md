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

## Features

- 🖥️ **Native Windows Experience**: clean Avalonia UI that respects your system theme and keeps local engine management in one place
- 🔀 **Multi-Engine Control**: run and manage both CLIProxyAPI and Perplexity from the same desktop app, with per-engine configuration, endpoint controls, and status
- 🚀 **One-Click Server Management**: start and stop each local engine directly from the Providers view
- 🔐 **Credential Storage**: secure handling for OAuth tokens, custom provider API keys, and file-based Perplexity session accounts stored under Tunnel Agent settings
- 👥 **Provider Management**: connect Claude Code, OpenAI Codex, Gemini CLI, Kimi, GitHub Copilot, Antigravity, and custom OpenAI-compatible providers
- 🧠 **Perplexity WebUI Sessions**: add multiple Perplexity accounts, set a default session, reset accounts safely, and auto-install the Perplexity engine when needed
- 🎚️ **Model Visibility**: browse available models grouped by connected provider or engine source, including Perplexity-backed model listings
- 📊 **Live Status**: see sidebar engine health plus focused engine state, endpoint, and release info
- ⚙️ **Configuration**: control startup behaviour, per-engine ports, updates, release selection, routing strategy, and credential actions

## Engine Overview

### CLIProxyAPI

CLIProxyAPI remains the main unified OpenAI-compatible local proxy for OAuth and upstream providers. Tunnel Agent lets you:

- install and update CLIProxyAPI releases
- switch to a pinned release instead of always following latest
- manage connected providers and custom OpenAI-compatible accounts
- inspect provider quotas and available models exposed by the running proxy

### Perplexity

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
| GitHub Copilot           | OAuth               |
| OpenAI Codex             | API key             |
| Kimi (Moonshot)          | API key             |
| Antigravity              | API key             |
| Perplexity               | WebUI session token |
| Custom OpenAI-compatible | API key + base URL  |

### Agents

> Agent integration is not yet implemented. The Agents section is planned for a future release.

## Auth File Storage

Tunnel Agent stores credentials and session data in the following locations.

### CLIProxyAPI OAuth tokens and custom keys

Managed by CLIProxyAPI. Tunnel Agent reads and modifies files in:

| Platform | Path                            |
| -------- | ------------------------------- |
| Windows  | `%UserProfile%\.cli-proxy-api\` |
| macOS    | `~/.cli-proxy-api/`             |
| Linux    | `~/.cli-proxy-api/`             |

The credential reset action backs up and removes only TunnelAgent-managed files (`openai-compat-*.json`, OAuth tokens). Unrelated files in the folder are preserved.

### Perplexity WebUI session tokens

Each Perplexity account is stored as a separate JSON file:

| Platform | Path                                                              |
| -------- | ----------------------------------------------------------------- |
| Windows  | `%AppData%\TunnelAgent\perplexity-accounts\{id}.json`             |
| macOS    | `~/Library/Preferences/TunnelAgent/perplexity-accounts/{id}.json` |
| Linux    | `~/.config/TunnelAgent/perplexity-accounts/{id}.json`             |

Session tokens are stored in plain text; access is restricted to the current user by OS file permissions. Reset backs up files to `.backup/{timestamp}/` before deleting.

## App Data Storage

Tunnel Agent writes non-credential app data to the following locations.

### Engine binaries

| Platform | Path                                                |
| -------- | --------------------------------------------------- |
| Windows  | `%LocalAppData%\TunnelAgent\engine\`                |
| macOS    | `~/Library/Application Support/TunnelAgent/engine/` |
| Linux    | `~/.local/share/TunnelAgent/engine/`                |

### Application settings

| Platform | Path                                              |
| -------- | ------------------------------------------------- |
| Windows  | `%AppData%\TunnelAgent\settings.json`             |
| macOS    | `~/Library/Preferences/TunnelAgent/settings.json` |
| Linux    | `~/.config/TunnelAgent/settings.json`             |

## Development

**Requirements:** Windows 10 or later · [.NET SDK 10.0.203+](https://dotnet.microsoft.com/download)

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
