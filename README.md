<p align="center">
  <img src="Assets/logo.png" width="128" height="128" alt="Tunnel Agent Icon">
</p>

<h1 align="center">Tunnel Agent</h1>

<p align="center">
  <b>Route your local CLI agent traffic through supported AI providers — from a single Windows desktop app.<br>Exposes an OpenAI-compatible endpoint so any agent can connect without touching config files.</b>
</p>

<p align="center">
  <a href="https://github.com/Villoh/tunnel-agent/releases">Download</a>
  ·
  <a href="https://github.com/Villoh/tunnel-agent/issues">Issues</a>
  ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-0A84FF?style=for-the-badge&labelColor=0f0f0f&logoColor=ffffff&logo=opensourceinitiative"></a>
  <img alt="Platform" src="https://img.shields.io/badge/Platform-Windows-0A84FF?style=for-the-badge&labelColor=0f0f0f&logoColor=ffffff&logo=windows">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-0A84FF?style=for-the-badge&labelColor=0f0f0f&logoColor=ffffff&logo=dotnet">
  <img alt="Version" src="https://img.shields.io/github/v/tag/Villoh/tunnel-agent?label=Version&style=for-the-badge&color=0A84FF&labelColor=0f0f0f">
</p>

> **Early development** — provider wiring and credential storage are scaffolded, not yet connected to real services.

---

## Features

- 🖥️ **Native Windows Experience** — Clean Avalonia UI that respects your system theme (light/dark)
- 🚀 **One-Click Server Management** — Start and stop the proxy endpoint from the sidebar
- 🔐 **Credential Storage** — Windows DPAPI-backed secure credential handling
- 👥 **Provider Management** — Connect Claude Code and OpenAI Codex; more providers planned
- 🎚️ **Model Visibility** — Browse available models grouped by connected provider
- 📊 **Live Status** — Animated running indicator with real-time connection state
- 📋 **Activity Log** — Track recent requests and events
- ⚙️ **Configuration** — Control startup behaviour, network port, and credential actions
- 🪟 **Tiling WM Friendly** — Compatible with GlazeWM and other tiling window managers

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
   dotnet run
   ```

> If the app is already running, stop it before rebuilding — Windows locks `TunnelAgent.exe` while it is open.

## Credits

Tunnel Agent is inspired by [VibeProxy](https://github.com/automazeio/vibeproxy), a native macOS menu bar app that routes CLI agent traffic through AI provider subscriptions. Tunnel Agent is built on top of [CLIProxyAPIPlus](https://github.com/router-for-me/CLIProxyAPIPlus), an excellent unified proxy server for AI services with support for third-party providers. Special thanks to both projects for the concept and the foundation that made this possible.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). Do not open a public issue.

## License

MIT License — see [LICENSE](LICENSE) for details.

---

© 2025 Villoh. All rights reserved.
