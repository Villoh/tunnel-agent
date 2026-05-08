# Tunnel Agent

Tunnel Agent is a Windows desktop app for routing local CLI agent traffic through supported AI providers.

Current version: **0.0.1**

## Status

This repository contains the initial Avalonia UI shell. Provider connection, credential storage, and proxy behavior are scaffolded/mock-driven and ready to be wired to real services.

## Features

- Avalonia desktop UI with light/dark theme support
- GlazeWM-friendly custom titlebar using border-only decorations
- Collapsible sidebar navigation
- Provider management for Claude Code and OpenAI Codex
- Connected service status with animated running indicator
- Available models grouped by connected provider
- Recent activity/logs section
- Configuration screen for startup, network port, and credentials actions
- Brand/provider icons via `IconPacks.Avalonia`

## Requirements

- Windows
- .NET 10 SDK

## Run

```pwsh
dotnet restore
dotnet run
```

If the app is already running, stop it before rebuilding because Windows can lock `TunnelAgent.exe`.

## Project layout

```text
.
├── App.axaml(.cs)              # App theme/resources and startup
├── Program.cs                  # Desktop entrypoint
├── Themes/                     # Brushes and shared control styles
├── Views/                      # Main window and section views
├── ViewModels/                 # Main state and row/group view models
├── Services/                   # Proxy/credential service abstractions
├── Converters/                 # UI value converters
└── Assets/                     # Avalonia resources
```

## Notes

- The app uses `SystemDecorations="BorderOnly"` so tiling window managers can detect it as a normal window while the UI keeps a custom titlebar.
- The current provider/model/activity data is sample data in `MainWindowViewModel`.
- `System.Security.Cryptography.ProtectedData` is included for Windows DPAPI-backed credential storage.

## License

MIT
