# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-10

### Added

- Initial Avalonia UI with custom titlebar, collapsible sidebar, and dark/light theme toggle
- Providers view with connected service cards and sparkline usage graphs
- Agents view with install status and route provider assignment
- Activity view with request log (method, path, agent, provider, model, status, latency)
- Configuration view with General, Network, Security, and Engine sections
- `EngineDownloadService` — downloads and installs the CLIProxyAPI binary from GitHub Releases
  - Platform-aware asset selection (Windows zip, macOS/Linux tar.gz)
  - Progress reporting during download
  - Automatic first-launch download when binary is missing
- `EngineConfigService` — generates `proxy-config.yaml` in the platform settings directory
  - Windows: `%AppData%\TunnelAgent\proxy-config.yaml`
  - macOS: `~/Library/Preferences/TunnelAgent/proxy-config.yaml`
  - Linux: `~/.config/TunnelAgent/proxy-config.yaml`
- `EngineProcessService` — starts and monitors the CLIProxyAPI process
  - Launches with `-config <path>` flag
  - Health polling to confirm server is up before marking Running
  - Crash detection via process Exited event
- `EngineService` — thin orchestrator composing the three services above
- `IPlatformInfo` interface with `WindowsPlatform`, `MacOsPlatform`, `LinuxPlatform` implementations
  - Per-platform binary name, archive format, OS/arch suffix, settings/data/auth directories
- `AppSettings` and `SettingsService` — persistent settings in platform roaming directory
- Update check via GitHub redirect URL (no API rate limit)
- Auto-check for updates on startup (configurable)
- Auto-update toggle (downloads and installs silently)
- Update notification: sidebar badge, startup toast (auto-dismisses after 8s), inline ENGINE card
- Manual "Update now" button with progress bar and success banner
- Port change in UI immediately rewrites config and restarts the engine if running
- Management control panel download disabled (`disable-control-panel: true`)

### Fixed

- Binary stored in `%LocalAppData%` (not roaming) — executables should not roam
- Settings stored in roaming `%AppData%` / `~/Library/Preferences` / `~/.config`
- CLIProxyAPI binary keeps its original name (`cli-proxy-api` / `cli-proxy-api.exe`)
- Version parsing handles `CLIProxyAPI Version: 7.0.2, Commit: ...` output format
- Version comparison normalises `v` prefix to avoid false update notifications
- Update check uses GitHub HTML redirect instead of API endpoint to avoid 60 req/h rate limit
- Engine always reads version from binary at startup (never trusts cached value)
- Update notification triggers reactively from `StateChanged` rather than at a fixed startup point

[0.1.0]: https://github.com/router-for-me/tunnel-agent/releases/tag/v0.1.0
