# Tunnel Agent

<p align="center">
  <img src="Assets/logo.png" width="128" height="128" alt="Tunnel Agent Icon">
</p>

<p align="center">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-28a745"></a>
  <img alt="Platform" src="https://img.shields.io/badge/Platform-Windows-0078d4">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512bd4">
  <img alt="Version" src="https://img.shields.io/badge/Version-0.0.1-orange">
</p>

**Route your local CLI agent traffic through supported AI providers — from a single Windows desktop app.**

Tunnel Agent exposes an OpenAI-compatible HTTP endpoint on `127.0.0.1:7890` and lets you manage provider connections, credentials, and model availability without touching config files.

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

## Requirements

| Requirement | Version |
|-------------|---------|
| OS | Windows 10 or later |
| .NET SDK | 10.0.203 or later |

## Build from Source

1. Clone the repository:
   ```pwsh
   git clone https://github.com/Villoh/tunnel-agent.git
   cd tunnel-agent
   ```
2. Install [.NET SDK 10.0.203+](https://dotnet.microsoft.com/download)
3. Restore dependencies and run:
   ```pwsh
   dotnet restore
   dotnet run
   ```

> If the app is already running, stop it before rebuilding — Windows locks `TunnelAgent.exe` while it is open.

## Project Layout

```text
.
├── App.axaml(.cs)          # App theme/resources and startup
├── Program.cs              # Desktop entrypoint
├── Themes/                 # Brushes and shared control styles
├── Views/                  # Main window and section views
├── ViewModels/             # Main state and row/group view models
├── Services/               # Proxy/credential service abstractions
├── Converters/             # UI value converters
└── Assets/                 # Avalonia resources
```

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). Do not open a public issue.

## License

MIT License — see [LICENSE](LICENSE) for details.

---

© 2025 Villoh. All rights reserved.
