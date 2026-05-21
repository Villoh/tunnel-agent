<p align="center">
  <img src="src/TunnelAgent.Avalonia/Assets/logo.png" width="128" height="128" alt="Tunnel Agent Icon">
</p>

<h1 align="center">Tunnel Agent</h1>

<p align="center">
  Use your OAuth subscriptions or API keys in any coding agent, Factory Droid, Cursor, and more. Tunnel Agent manages provider authentication, runs CLIProxyAPI and Perplexity locally, and gives you one desktop UI to control both engines.<br><br>One click to connect. Works with whatever you already have.
</p>

<p align="center">
  <a href="https://github.com/Villoh/tunnel-agent/releases">Download</a>
  ·
  <a href="https://github.com/Villoh/tunnel-agent/issues">Issues</a>
  ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-0A84FF?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=opensourceinitiative"></a>
  <img alt="Platform" src="https://img.shields.io/badge/Platform-Windows-0A84FF?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=windows">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-0A84FF?style=for-the-badge&labelColor=ffffff&logoColor=000000&logo=dotnet">
  <img alt="Version" src="https://img.shields.io/github/v/tag/Villoh/tunnel-agent?label=Version&style=for-the-badge&color=0A84FF&labelColor=ffffff">
</p>

---

## Features

- 🖥️ **Native Windows Experience** — Clean Avalonia UI that respects your system theme and keeps local engine management in one place
- 🔀 **Multi-Engine Control** — Run and manage both CLIProxyAPI and Perplexity from the same desktop app, with per-engine configuration, endpoint controls, and status
- 🚀 **One-Click Server Management** — Start and stop each local engine directly from the Providers view
- 🔐 **Credential Storage** — Secure handling for OAuth tokens, custom provider API keys, and file-based Perplexity session accounts stored under Tunnel Agent settings
- 👥 **Provider Management** — Connect Claude Code, OpenAI Codex, Gemini CLI, Kimi, GitHub Copilot, Antigravity, Qwen, and custom OpenAI-compatible providers
- 🧠 **Perplexity WebUI Sessions** — Add multiple Perplexity accounts, set a default session, reset accounts safely, and auto-install the Perplexity engine when needed
- 🎚️ **Model Visibility** — Browse available models grouped by connected provider or engine source, including Perplexity-backed model listings
- 📊 **Live Status** — See sidebar engine health plus focused engine state, endpoint, and release info
- ⚙️ **Configuration** — Control startup behaviour, per-engine ports, updates, release selection, routing strategy, and credential actions

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
- view Perplexity endpoint status and available models from the Perplexity server
- keep Perplexity on a selected release instead of being forced to latest

## Build from Source

**Requirements:** Windows 10 or later · [.NET SDK 10.0.203+](https://dotnet.microsoft.com/download)

1. Clone the repository:
   ```pwsh
   git clone https://github.com/Villoh/tunnel-agent.git
   cd tunnel-agent
   ```
2. Restore dependencies and run:
   ```pwsh
   dotnet restore
   dotnet run --project src/TunnelAgent.Avalonia/TunnelAgent.Avalonia.csproj
   ```

> If the app is already running, stop it before rebuilding — Windows locks `TunnelAgent.exe` while it is open.

## Credits

Tunnel Agent is inspired by [VibeProxy](https://github.com/automazeio/vibeproxy), a native macOS menu bar app that routes CLI agent traffic through AI provider subscriptions. Tunnel Agent is built on top of [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI), an excellent unified proxy server for AI services with support for third-party providers. Special thanks to both projects for the concept and the foundation that made this possible.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). Do not open a public issue.

## License

MIT License — see [LICENSE](LICENSE) for details.
