# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] - 2026-05-21

### Added

- First-class Perplexity engine support alongside CLIProxyAPI, including engine switching from Providers and Configuration.
- Perplexity account management UI with add, remove, default-account selection, and reset confirmation.
- Full-window overlays for account dialogs and confirmation flows so dimming covers the whole app.
- Sliding top tab bars with animated focus indicator for Providers and Configuration.
- GitHub release metadata caching with manual invalidation on refresh/update checks to avoid rate limiting.
- Automatic install-on-startup support for the Perplexity engine when it is missing.
- Sidebar dual-engine status overview for CLIProxyAPI and Perplexity.

### Changed

- Providers page now supports both CLIProxyAPI and Perplexity with focused engine state, per-engine endpoint/start-stop controls, and engine-specific model loading.
- Configuration page reorganized into top tabs: General, CLIProxyAPI, and Perplexity.
- Perplexity security/configuration actions now point to the app settings folder, not the CLIProxy auth folder.
- Perplexity accounts are now stored as separate JSON files under the Tunnel Agent settings directory instead of `settings.json` or `.cli-proxy-api`.
- App startup now defaults Providers focus to CLIProxyAPI while keeping engine selection logic consistent across pages.
- Sidebar branding updated: smaller Tunnel Agent lockup in sidebar, version removed from titlebar.

### Fixed

- Available models now query the active engine endpoint/port instead of always using CLIProxyAPI.
- Restored provider-group expansion UI for available models after the multi-engine refactor.
- Perplexity startup health polling no longer crashes the app on `HttpClient` timeout retries.
- Perplexity process startup now surfaces early stderr/crash details instead of only showing a generic timeout.
- Provider account quota bars are rendered again in the expanded account cards.
- Window-root dialogs now dim the full app instead of only the content pane.
- Sidebar status rendering/alignment issues after layout refactor.
- Migration added from legacy `settings.json` Perplexity accounts into file-based account storage.

## [0.2.5] - 2026-05-18

### Added

- Check for update button in Configuration → Engine row, visible when no update is pending. Disables while checking.
- Theme selector in Configuration → General: System, Light, or Dark. Persisted across restarts. Sidebar toggle still cycles between Light and Dark in session.
- Confirmation dialog before resetting credentials, rendered as a window-level overlay so it floats correctly over the full app.
- Default settings are written to `settings.json` when the file is empty or missing fields, so new installs and manual edits always produce a complete settings file.
- 230+ passing unit tests.

### Changed

- Repository restructured into `src/TunnelAgent.Avalonia`, `src/TunnelAgent.Core`, `src/TunnelAgent.Infrastructure`, `src/TunnelAgent.Abstractions`, and `tests/TunnelAgent.Tests`. `TunnelAgent.slnx` at root.
- `IsDark` removed from `AppSettings`; theme preference is now `ThemeMode = system|light|dark`.
- `LogLevel` removed from `AppSettings`; CLIProxyAPI always starts with `debug: false`.
- Credential reset now backs up managed token files to `.tunnelagent-backup/{timestamp}/` before deleting them, and only deletes TunnelAgent-managed files (OAuth prefixes and `openai-compat-*.json`). Unrelated JSON in the auth folder is preserved.
- Status messages from credential operations (reset, open folder) appear in the Configuration view, not the Providers view.
- Reset credentials confirmation and status feedback are shown in the correct UI context.

### Fixed

- Loading an empty `settings.json` now correctly writes and applies default values.
- OAuth `ConnectAsync` test for a known provider no longer launches the real CLIProxyAPI binary during unit test runs.
- `LaunchAtLoginService` no longer references the `Program` type from the Avalonia project when running from Infrastructure.

### Security

- Pinned `Tmds.DBus.Protocol` to `0.21.3` to resolve CVE-2026-39959 (high severity).

## [0.2.4] - 2026-05-18

### Added

- Routing strategy option in Configuration: Round Robin (even distribution) or Fill First (use first account until limit). Applies to both API keys and OAuth accounts.
- "Report an issue" link in sidebar footer opens the GitHub issues page.
- Test suite with 75 passing tests covering converters, engine config, OAuth token detection, provider catalog, auth file watcher, settings, credentials, view models, and service smoke tests.

### Changed

- Auto-update toggle is now disabled when auto-check for updates is off, preventing an inconsistent configuration.
- ComboBox controls show a hand cursor on hover to indicate they are selectable.
- Agents section is disabled in the UI until fully implemented.

## [0.2.3] - 2026-05-17

### Added

- Launch at login now works across Windows, macOS, and Linux.
- System tray controls let users show or hide the window, manage CLIProxyAPI, open configuration, open the auth folder, and quit the app.

### Changed

- The desktop app now targets cross-platform .NET instead of a Windows-only target framework.
- Closing the main window hides the app to the tray; quitting from the tray exits the app and stops the server.

## [0.2.2] - 2026-05-17

### Added

- Configuration now includes richer CLIProxyAPI release selection and install controls.

### Changed

- Provider state and server startup behavior are more reliable during initialization.
- Removed the unused Activity section from the desktop navigation.

## [0.2.1] - 2026-05-14

### Added

- Provider management now supports connected accounts, enable/disable state, OAuth and custom providers, live quota bars, and available models from the running proxy.
- Provider rows were redesigned with clearer connection status, account panels, refresh actions, and provider icons.
- OAuth callback pages now show branded success and error states.

### Fixed

- Provider account detection, quota display, refresh animations, add-account tooltips, and account enable/disable persistence now behave correctly.
- CLIProxyAPI owns the OAuth callback port directly, avoiding unsupported callback interception in the desktop app.

## [0.2.0] - 2026-05-14

### Added

#### Provider Catalog

- `ProviderCatalogService` — orchestrates the full provider lifecycle: auth-dir sync, add/remove accounts, enable/disable, config.yaml rewrite
- `OAuthTokenDetector` — reads `~/.cli-proxy-api/{prefix}-{email}*.json` files to detect connected OAuth providers and parse account metadata (email, plan badge)
- `AuthFileWatcher` — `FileSystemWatcher` with 400 ms debounce over `~/.cli-proxy-api/` to react to new/removed token files in real time
- `CustomProviderCredentialStore` — reads/writes `openai-compat-{id}-{uuid}.json` credential files, compatible with CLIProxyAPI format
- `OAuthService` — launches `cli-proxy-api -<provider>-login` to trigger OAuth browser flow; handles Gemini stdin newline, Codex keepalive, Copilot device-code extraction
- `QuotaFetchService` — fetches live quota from provider APIs using the `access_token` in local token files:
  - **Claude**: `api.anthropic.com/api/oauth/usage` → `five_hour` + `seven_day` utilization %
  - **Codex**: `chatgpt.com/backend-api/wham/usage` → `primary_window` + `secondary_window` used %
  - **Copilot**: `api.github.com/copilot_internal/user` → `premium_interactions` + `chat` quota
- `ModelFetchService` — calls `/v1/models` on the running proxy and groups results by `owned_by`

#### Providers UI (VibeProxy-inspired redesign)

- Provider rows: `[Toggle] [Icon] [Name + colored status line] [action buttons] [›]`
  - Status sub-line: green when connected, orange when disabled, muted otherwise
  - `ConnectedSubText`: "N connected accounts · Round-robin w/ auto-failover"
  - `[disabled]` chip badge when provider is toggled off
- **Toggle pill** with animated sliding white thumb (RotateTransform, CSS-like transition)
- Toggle auto-disabled when no accounts exist; auto-off when last account is removed
- **Expand chevron** (`›` / `˅`) — only visible when provider has accounts; expands account panel
- **`+` icon button** — single entry point for OAuth connect/add-account (always visible for OAuth providers)
- **`LogOut` icon button** — disconnect all accounts (visible only when connected); consistent with per-account disconnect
- Connecting chip badge shown while OAuth flow is in flight
- Custom providers: "Add Account" button opens inline dialog

#### Expanded Account Panel

- Per-account card with email/label, plan badge (PLUS/PRO/FREE), masked API key chip
- **Quota bars**: `ProgressBar` (green fill, 0–1 range) with title, "N% used" label, "Resets in Xd Yh" countdown
- `HasQuota` properly reactive via `CollectionChanged` on `QuotaBars` collection
- **Refresh `↺` button** per account — re-fetches quota from provider API with spinning animation (`RotateTransform.Angle` 0→360, 0.7 s linear)
- Enable/disable toggle per account — **persisted to disk** on change:
  - OAuth accounts: patches `"disabled"` field in `{prefix}-{email}*.json`
  - Custom accounts: patches `openai-compat-*.json` via `CustomProviderCredentialStore`
- `LogOut` icon to remove/disconnect individual account

#### Available Models

- Section shows "Start the server to see available models" (`ServerOff` icon) when proxy is stopped
- When server starts: auto-fetches `/v1/models`, groups by provider (`owned_by`), shows model count per group
- Expandable model groups with model ID (monospace), context hint, provider chip, auth-kind chip
- Total model count updates reactively via `CollectionChanged` on `AvailableModelGroups`

### Changed

- `AppSettings` extended with `ProviderSettings` and `ProviderAccountSettings` classes
- `EngineConfigService` rewrote config generation to produce real `oauth-excluded-models` and `openai-compatibility` YAML blocks from live provider state
- `MainWindowViewModel` wires `ProviderCatalogService`; providers loaded from catalog (not seeded)
- `App.axaml.cs` instantiates and passes `EngineConfigService` + `ProviderCatalogService` to the ViewModel
- `ProvidersView` header renamed "Services" to match VibeProxy

### Fixed

- Provider row `Button` wrapper disabled all children (toggle, connect) — replaced with `Grid` + name-only clickable `Button.provider-row-name`
- Grey background on unconnected provider rows — forced `Transparent` on `ContentPresenter` in base state
- `window.close()` removed from OAuth callback page (doesn't work in non-script-opened tabs)
- OAuth callback page `Setter.Value` crash (`Cannot use a control as a Setter value`) — replaced Content setter with `ControlTemplate`
- `RenderTransform` animation crash — replaced string value with `RotateTransform` + `(RotateTransform.Angle)` property path
- Duplicate "connecting…" chip (appeared in both name area and action buttons)
- `TotalAvailableModelCount` always returning 0 — wired `CollectionChanged` on `AvailableModelGroups`
- `RemoveAccountAsync` for OAuth accounts (was passing empty `ApiKey`) — now uses `Email` to find token file
- Account enable/disable toggle not persisted across restarts — wired `IsDisabledChanged` event to disk write
- Add account tooltip showing `True`/`False` — replaced converter binding with static string
- Quota bar spacing (no gap between bars) — increased `Margin` to `0,6,0,0`
- Refresh button `IsRefreshing` not always restored — dispatched to UI thread in `finally` block

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

[Unreleased]: https://github.com/Villoh/tunnel-agent/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/Villoh/tunnel-agent/compare/v0.2.5...v0.3.0
[0.2.5]: https://github.com/Villoh/tunnel-agent/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/Villoh/tunnel-agent/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/Villoh/tunnel-agent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/Villoh/tunnel-agent/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Villoh/tunnel-agent/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Villoh/tunnel-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Villoh/tunnel-agent/releases/tag/v0.1.0
